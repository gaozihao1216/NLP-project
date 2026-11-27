// 玩家输入 → 战斗命令。键位以 docs/combat-controls.md 第 0/2 节为准（2026-11-27 按文档重写）。
//
// 分档（combat-controls.md §0）：
//   核心  W/S 俯仰、A/D 偏航、空格 推力档循环
//   常用  左键开火、Tab 锁定
//   辅助  Q/E 滚转、F 飞控、1/2/3 能源档
//   高级  Shift 反推
//
// 鼠标三维瞄准（§2.1，锁定微调除外）：
//   光标压在舰上 → 射线与包围球求交，取最近命中点；
//   光标在空白处 → 射线与"距相机 200 km、垂直于相机朝向的过场平面"求交；
//   已锁定       → 不发 AimCommand（仿真按锁定目标解提前量；±3° 微调待契约支持，
//                  见 combat-controls.md 缺口清单"鼠标只能给世界点"一行）。
//
// sticky 与按住有效的区别（§2）：
//   角加速度输入按住有效（仿真每 tick 清零）；推力档位与飞控 sticky；开火按住连射。
//
// 能量三档的具体比例与 Domain 的 PowerAllocation.ForPreset 一致；
// 但 Presentation 不引用 Domain（asmdef 边界），所以这里显式写值——
// 改档位比例时两处要同步，这一条写在两边的注释里互相提醒。

using UnityEngine;
using StarPawn.Application;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Presentation
{
    public class BattleInputController : MonoBehaviour
    {
        /// <summary>推力档位循环（combat-controls.md §2：0 → 1/3 → 2/3 → 1 → 0；档位数[待定]，按文档示例四档）。</summary>
        private static readonly float[] ThrustSteps = { 0f, 1f / 3f, 2f / 3f, 1f };
        /// <summary>光标拾取舰用的包围球半径（km）。舰体碰撞半径 0.2–0.37，拾取放宽到 1 km 好点。</summary>
        private const float PickRadiusKm = 1f;
        /// <summary>空白处瞄准的过场平面距离（km，距相机，§2.1）。</summary>
        private const float AimPlaneDistanceKm = 200f;

        /// <summary>玩家操控的旗舰。快照里找不到（阵亡）时 HasPlayer=false，不发操舵命令。</summary>
        public EntityId PlayerShipId { get; private set; }
        public bool HasPlayer { get; private set; }

        public float ThrustSetpoint { get; private set; }
        public bool FlightAssist { get; private set; }
        public EntityId LockedTargetId { get; private set; }
        public PowerPreset PowerMode { get; private set; }
        /// <summary>未锁定且本 tick 鼠标给出了瞄准点时为 true（HUD 显示瞄准模式用）。</summary>
        public bool ManualAimActive { get; private set; }

        /// <summary>最近一次被拒绝的命令 / 输入提示（HUD 显示失败原因，不再靠猜）。</summary>
        public string LastCommandError { get; private set; }

        private ShipSnapshot _player;
        private CampaignFacade _facade; // Tick 时缓存，供离散输入（锁定/能量档）提交命令
        private int _thrustStep;
        private bool _assist = true;

        public void ResetFor(BattleStartSpec spec)
        {
            PlayerShipId = new EntityId();
            HasPlayer = false;
            _thrustStep = 0;
            ThrustSetpoint = 0f;
            _assist = true;
            FlightAssist = true;
            LockedTargetId = new EntityId();
            PowerMode = PowerPreset.Balanced;
            ManualAimActive = false;
            LastCommandError = null;
            _player = null;
        }

        /// <summary>离散输入放 Update（按一下只触发一次）。</summary>
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Space)) CycleThrust();
            if (Input.GetKeyDown(KeyCode.F)) _assist = !_assist;
            if (Input.GetKeyDown(KeyCode.Tab)) ToggleLock();
            if (Input.GetKeyDown(KeyCode.Alpha1)) SendPower(PowerPreset.Navigation);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SendPower(PowerPreset.Balanced);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SendPower(PowerPreset.Engagement);
        }

        /// <summary>持续输入放 FixedUpdate（每 tick 提交一次当前状态）。</summary>
        public void Tick(CampaignFacade facade, BattleSnapshot snap)
        {
            if (facade == null || snap == null) return;
            _facade = facade;

            FindPlayer(snap);
            if (!HasPlayer || _player == null) return;

            // 角加速度输入：按住有效（仿真侧每 tick 清零，飞控负责回正）。
            float pitch = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
            float yaw = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
            float roll = (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f);

            // 推力：档位 sticky；Shift 反推按住时压到 -0.5，松开回档位（反推不是刹车，§2）。
            float thrust = ThrustSetpoint;
            if (Input.GetKey(KeyCode.LeftShift)) thrust = -0.5f;

            Submit(new HelmCommand
            {
                ShipId = PlayerShipId,
                Thrust = thrust,
                Yaw = Mathf.Clamp(yaw, -1f, 1f),
                Pitch = Mathf.Clamp(pitch, -1f, 1f),
                Roll = Mathf.Clamp(roll, -1f, 1f),
                FlightAssist = _assist
            });

            // 左键开火：按住即射，装填/射界/能源由仿真把关（§5.2 的检查顺序在仿真侧）。
            if (Input.GetMouseButton(0))
            {
                Submit(new FireCommand { ShipId = PlayerShipId });
            }

            // 鼠标瞄准：仅未锁定时发 AimCommand（§2.1）；锁定时仿真自己解提前量。
            ManualAimActive = false;
            if (LockedTargetId.IsEmpty)
            {
                Camera cam = BattleSceneController.Instance != null
                    ? BattleSceneController.Instance.MainCamera : null;
                if (cam != null && TryMouseAimPoint(cam, snap, out Vector3 aimKm))
                {
                    ManualAimActive = true;
                    Submit(new AimCommand
                    {
                        ShipId = PlayerShipId,
                        WorldX = aimKm.x, WorldY = aimKm.y, WorldZ = aimKm.z
                    });
                }
            }
        }

        // ---- 离散动作 ----

        private void CycleThrust()
        {
            _thrustStep = (_thrustStep + 1) % ThrustSteps.Length;
            ThrustSetpoint = ThrustSteps[_thrustStep];
        }

        private void ToggleLock()
        {
            if (!HasPlayer) return;

            // 已锁定 → 再按一次解除（§2）。
            if (!LockedTargetId.IsEmpty)
            {
                LockedTargetId = new EntityId();
                Submit(new LockTargetCommand { ShipId = PlayerShipId, TargetId = new EntityId() });
                return;
            }

            // 未锁定 → 锁定光标下的敌舰（§2.1：光标压舰）。光标下没有就不锁，提示玩家。
            Camera cam = BattleSceneController.Instance != null
                ? BattleSceneController.Instance.MainCamera : null;
            BattleSnapshot snap = BattleSceneController.Instance != null
                ? BattleSceneController.Instance.Snapshot : null;
            if (cam == null || snap == null || snap.Ships == null) return;

            ShipSnapshot underCursor = PickShipUnderCursor(cam, snap, hostileOnly: true);
            if (underCursor == null)
            {
                LastCommandError = "光标下没有敌舰，按 Tab 前先把准星移到敌舰上";
                return;
            }

            LockedTargetId = underCursor.ShipId;
            Submit(new LockTargetCommand { ShipId = PlayerShipId, TargetId = underCursor.ShipId });
        }

        private void SendPower(PowerPreset preset)
        {
            if (!HasPlayer) return;
            PowerMode = preset;
            PresetShares(preset, out float prop, out float shield, out float weap);
            Submit(new PowerAllocationCommand
            {
                ShipId = PlayerShipId,
                Propulsion = prop, Shield = shield, Weapons = weap, Preset = preset
            });
        }

        // ---- 鼠标 → 世界点（§2.1 的前两条规则）----

        private static bool TryMouseAimPoint(Camera cam, BattleSnapshot snap, out Vector3 aimKm)
        {
            aimKm = Vector3.zero;

            ShipSnapshot ship = PickShipUnderCursor(cam, snap, hostileOnly: false);
            if (ship != null)
            {
                aimKm = SimMath.ToWorld(ship.X, ship.Y, ship.Z);
                return true;
            }

            // 空白处：射线与"距相机 200 km、垂直于相机朝向的平面"求交。
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            Vector3 fwd = cam.transform.forward;
            float denom = Vector3.Dot(ray.direction, fwd);
            if (Mathf.Abs(denom) < 1e-4f) return false;

            float t = AimPlaneDistanceKm / denom;
            if (t <= 0f) return false;
            aimKm = ray.origin + ray.direction * t;
            return true;
        }

        /// <summary>
        /// 光标下的舰（§2.1 第一条：射线与包围球求交，取沿射线最近者）。
        /// hostileOnly=true 只挑敌舰（Tab 锁定用），false 友敌都算（AimCommand 瞄准用）。
        /// </summary>
        private static ShipSnapshot PickShipUnderCursor(Camera cam, BattleSnapshot snap, bool hostileOnly)
        {
            if (snap == null || snap.Ships == null) return null;

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            ShipSnapshot best = null;
            float bestT = float.MaxValue;

            for (int i = 0; i < snap.Ships.Length; i++)
            {
                ShipSnapshot s = snap.Ships[i];
                if (!s.IsAlive) continue;
                if (hostileOnly && s.IsAlly) continue;

                Vector3 center = SimMath.ToWorld(s.X, s.Y, s.Z);
                Vector3 toCenter = center - ray.origin;
                float along = Vector3.Dot(toCenter, ray.direction);
                if (along < 0f) continue; // 在相机背后

                float closestSq = toCenter.sqrMagnitude - along * along;
                float rSq = PickRadiusKm * PickRadiusKm;
                if (closestSq > rSq) continue;

                float halfChord = Mathf.Sqrt(rSq - closestSq);
                float t = along - halfChord; // 进入点，取最近
                if (t < 0f) t = 0f;
                if (t < bestT)
                {
                    bestT = t;
                    best = s;
                }
            }
            return best;
        }

        // ---- 公共 ----

        private void FindPlayer(BattleSnapshot snap)
        {
            if (_player != null && _player.IsAlive && _player.IsAlly)
            {
                HasPlayer = true;
                return;
            }

            _player = null;
            if (snap.Ships == null) { HasPlayer = false; return; }

            for (int i = 0; i < snap.Ships.Length; i++)
            {
                ShipSnapshot s = snap.Ships[i];
                if (s.IsAlly && s.Role == SquadRole.Flagship && s.IsAlive)
                {
                    _player = s;
                    PlayerShipId = s.ShipId;
                    HasPlayer = true;
                    return;
                }
            }
            HasPlayer = false; // 旗舰阵亡：不再发命令，HUD 显示败北
        }

        private void Submit(CombatCommand cmd)
        {
            if (_facade == null) return;
            CommandResult r = _facade.SubmitCombatCommand(cmd);
            if (!r.Success) LastCommandError = r.Error.ToString();
            else if (cmd is FireCommand || cmd is HelmCommand) LastCommandError = null;
        }

        /// <summary>三档比例，与 CombatShip.ForPreset 保持一致（Presentation 不引用 Domain，两处需同步）。</summary>
        private static void PresetShares(PowerPreset preset, out float prop, out float shield, out float weapons)
        {
            switch (preset)
            {
                case PowerPreset.Navigation:
                    prop = 0.5f; shield = 0.2f; weapons = 0.3f;
                    break;
                case PowerPreset.Engagement:
                    prop = 0.2f; shield = 0.3f; weapons = 0.5f;
                    break;
                default: // Balanced / Custom
                    prop = 0.34f; shield = 0.33f; weapons = 0.33f;
                    break;
            }
        }

        private void LateUpdate()
        {
            // 锁定目标失效时同步本地状态（仿真会自动解锁并发 LockLost）。
            if (!LockedTargetId.IsEmpty && _player != null && _player.LockedTargetId.IsEmpty)
            {
                LockedTargetId = new EntityId();
            }
        }
    }
}
