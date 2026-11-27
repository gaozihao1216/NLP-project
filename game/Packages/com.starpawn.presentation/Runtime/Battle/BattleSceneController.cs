// 战斗场景总控：把 Bootstrap 装配出的 CampaignFacade 接进 Unity 的帧循环。
//
//   FixedUpdate（1/30s，与仿真 FixedStep 一致）：提交输入 → StepBattle → 读快照 → 排空事件
//   Update：View 同步、追舰相机、胜负检测与结算
//
// 组合根在 Assets/Game/Bootstrap/BattleDemoLauncher.cs（Bootstrap 组装件）——
// 它调用 GameBootstrap.Create() 后通过 Launch() 把门面交进来；
// Presentation 不引用 Bootstrap（Bootstrap 已反向引用本包，再引会成环）。

using System;
using UnityEngine;
using StarPawn.Application;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Presentation
{
    public class BattleSceneController : MonoBehaviour
    {
        public static BattleSceneController Instance { get; private set; }

        public BattleSnapshot Snapshot { get; private set; }
        public BattleInputController InputState { get; private set; }
        public Camera MainCamera { get; private set; }
        public BattleResult Result { get; private set; }
        public bool ResultShown { get; private set; }
        public string SettleMessage { get; private set; }

        private CampaignFacade _facade;
        private Func<CampaignFacade> _facadeFactory;
        private BattleViewRegistry _registry;
        private BattleHud _hud;

        private float _camDistance = 8f;
        private float _orbitYaw;
        private float _orbitPitch;

        /// <summary>
        /// 由组合根调用：创建场景总控并把门面交进来。
        /// facadeFactory 用于结束后按 Enter 重开（重新装配一套 CampaignFacade）。
        /// </summary>
        public static BattleSceneController Launch(CampaignFacade facade, Func<CampaignFacade> facadeFactory)
        {
            if (facade == null) throw new ArgumentNullException("facade");
            GameObject go = new GameObject("BattleSceneController");
            BattleSceneController c = go.AddComponent<BattleSceneController>();
            c._facade = facade;
            c._facadeFactory = facadeFactory;
            return c;
        }

        private void Awake()
        {
            Instance = this;
            Time.fixedDeltaTime = 1f / 30f; // 与 BattleStartSpec.FixedStep 严格一致，Step 才是等间隔的

            StarfieldBuilder.Build();
            MainCamera = BuildCamera();

            GameObject views = new GameObject("BattleViews");
            _registry = views.AddComponent<BattleViewRegistry>();

            GameObject fxGo = new GameObject("BattleFx");
            BattleFx fx = fxGo.AddComponent<BattleFx>();
            _registry.Fx = fx;

            GameObject inputGo = new GameObject("BattleInput");
            InputState = inputGo.AddComponent<BattleInputController>();

            GameObject hudGo = new GameObject("BattleHud");
            _hud = hudGo.AddComponent<BattleHud>();
            _hud.Controller = this;
            _hud.Registry = _registry;
            _hud.Input = InputState;

            StartBattle();
        }

        private static Camera BuildCamera()
        {
            GameObject go = new GameObject("BattleCamera");
            Camera cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.backgroundColor = new Color(0.01f, 0.01f, 0.03f);
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 60000f;
            go.tag = "MainCamera";
            go.transform.position = new Vector3(0f, 5f, -15f);
            go.transform.rotation = Quaternion.Euler(10f, 0f, 0f);
            return cam;
        }

        private void StartBattle()
        {
            Result = null;
            ResultShown = false;
            SettleMessage = null;
            _registry.ResetAll();
            _registry.Fx.ClearAll();

            // 与 CLI 同一套敌舰编成：3 艘巡逻艇，260 km 正面。
            EntityId[] enemies =
            {
                new EntityId("enemy.0"),
                new EntityId("enemy.1"),
                new EntityId("enemy.2")
            };
            _facade.LaunchBattle(enemies);
            Snapshot = _facade.ReadBattleSnapshot();
            InputState.ResetFor(null);
        }

        private void FixedUpdate()
        {
            if (_facade == null) return;

            if (Snapshot == null) Snapshot = _facade.ReadBattleSnapshot();

            InputState.Tick(_facade, Snapshot);
            _facade.StepBattle();

            Snapshot = _facade.ReadBattleSnapshot();
            _registry.HandleEvents(_facade.DrainBattleEvents());
        }

        private void Update()
        {
            if (Snapshot != null) _registry.Sync(Snapshot);

            UpdateOrbit();
            FollowCamera();

            if (!ResultShown && _facade.TryGetBattleResult(out BattleResult result))
            {
                Result = result;
                ResultShown = true;
                CommandResult settle = _facade.SettleBattle(result.BattleId);
                SettleMessage = settle.Success
                    ? "结算完成：击沉 " + result.DestroyedEnemyIds.Length + " / 损失 " + result.LostAllyIds.Length +
                      "，用时 " + result.ElapsedSeconds.ToString("0") + " s"
                    : "结算失败：" + settle.Error;
                Debug.Log("[BattleDemo] " + SettleMessage);
            }

            if (ResultShown && Input.GetKeyDown(KeyCode.Return))
            {
                if (_facadeFactory != null) _facade = _facadeFactory();
                StartBattle();
            }
        }

        /// <summary>右键拖动环绕旗舰（yaw/pitch 偏移），松手缓慢回正。左键留给开火，滚轮留给变焦。</summary>
        private void UpdateOrbit()
        {
            if (Input.GetMouseButton(1))
            {
                _orbitYaw += Input.GetAxis("Mouse X") * 2.5f;
                _orbitPitch = Mathf.Clamp(_orbitPitch + Input.GetAxis("Mouse Y") * 1.8f, -60f, 60f);
            }
            else
            {
                float k = Mathf.Clamp01(Time.deltaTime * 2f);
                _orbitYaw = Mathf.Lerp(_orbitYaw, 0f, k);
                _orbitPitch = Mathf.Lerp(_orbitPitch, 0f, k);
            }
        }

        /// <summary>追舰第三人称：贴在旗舰后方上方（叠加右键环视偏移），滚轮缩放。旗舰阵亡后镜头悬停在最后位置。</summary>
        private void FollowCamera()
        {
            ShipSnapshot player = FindPlayerShip();
            if (player != null)
            {
                _camDistance = Mathf.Clamp(_camDistance - Input.mouseScrollDelta.y * 0.8f, 3f, 80f);

                Vector3 pos = SimMath.ToWorld(player.X, player.Y, player.Z);
                Quaternion rot = Quaternion.Euler(player.Pitch, player.Yaw, player.Roll);
                Vector3 forward = rot * Vector3.forward;

                Quaternion orbit = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);
                Vector3 desired = pos + orbit * (-forward * _camDistance + Vector3.up * (_camDistance * 0.35f));
                float k = Mathf.Clamp01(Time.deltaTime * 3.5f);
                MainCamera.transform.position = Vector3.Lerp(MainCamera.transform.position, desired, k);
                MainCamera.transform.LookAt(pos + forward * 10f);
            }
        }

        private ShipSnapshot FindPlayerShip()
        {
            if (Snapshot == null || Snapshot.Ships == null) return null;
            for (int i = 0; i < Snapshot.Ships.Length; i++)
            {
                ShipSnapshot s = Snapshot.Ships[i];
                if (s.IsAlly && s.Role == SquadRole.Flagship && s.IsAlive) return s;
            }
            return null;
        }
    }
}
