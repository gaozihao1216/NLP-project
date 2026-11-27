// 舰船占位视觉（灰盒）：Box 本体 + 舰首标 + 逐炮座视觉炮塔。
//
// 尺寸口径：hull 半径 0.2–0.37 km，本体 Box 取 0.9×0.5×0.28 单位（长×宽×高，1 单位 = 1 km）。
// 以后换 Blender 舰模只替换 Build() 里的实体，Sync() 的快照驱动逻辑不变。
//
// 炮塔方向：TurretSnapshot.AimLocalDir 是舰体坐标单位向量，视觉炮塔是舰体子物体，
// 直接设 localRotation 即可，舰体转头时炮塔自动跟随（契约注释明确要求这样做）。

using System.Collections.Generic;
using UnityEngine;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Presentation
{
    public class ShipView : MonoBehaviour
    {
        public EntityId ShipId { get; private set; }
        public bool IsAliveMark { get; private set; }

        private Color _baseColor;
        private Transform _body;
        private readonly List<Transform> _turrets = new List<Transform>();

        public void Build(ShipSnapshot s)
        {
            ShipId = s.ShipId;
            gameObject.name = "Ship." + s.ShipId;
            _baseColor = PickColor(s);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(transform, false);
            body.transform.localScale = new Vector3(0.5f, 0.28f, 0.9f);
            DisableCollider(body);
            _body = body.transform;

            // 舰首标：贴在 +Z 前端的细长条。没有模型时它是唯一能读出舰首方向的线索。
            GameObject nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "NoseMark";
            nose.transform.SetParent(transform, false);
            nose.transform.localPosition = new Vector3(0f, 0f, 0.52f);
            nose.transform.localScale = new Vector3(0.06f, 0.06f, 0.16f);
            SetColor(nose, new Color(1f, 0.85f, 0.3f));
            DisableCollider(nose);
        }

        public void Sync(ShipSnapshot s)
        {
            ShipId = s.ShipId;
            IsAliveMark = s.IsAlive;
            if (!s.IsAlive)
            {
                gameObject.SetActive(false);
                return;
            }
            gameObject.SetActive(true);

            transform.position = SimMath.ToWorld(s.X, s.Y, s.Z);
            transform.rotation = Quaternion.Euler(s.Pitch, s.Yaw, s.Roll);

            if (_body != null)
            {
                Renderer r = _body.GetComponent<MeshRenderer>();
                if (r != null)
                {
                    // 血量越低越暗，挨打反馈不用等特效。
                    r.material.color = Color.Lerp(Color.black, _baseColor, 0.35f + 0.65f * Mathf.Clamp01(s.Health01));
                }
            }

            SyncTurrets(s);
        }

        private void SyncTurrets(ShipSnapshot s)
        {
            TurretSnapshot[] turrets = s.Turrets;
            if (turrets == null) return;

            if (_turrets.Count != turrets.Length)
            {
                foreach (Transform t in _turrets)
                {
                    if (t != null) Destroy(t.gameObject);
                }
                _turrets.Clear();
                for (int i = 0; i < turrets.Length; i++)
                {
                    GameObject gun = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    gun.name = "Turret." + turrets[i].TurretId;
                    gun.transform.SetParent(transform, false);
                    gun.transform.localScale = new Vector3(0.1f, 0.1f, 0.32f);
                    SetColor(gun, new Color(0.32f, 0.34f, 0.38f));
                    DisableCollider(gun);
                    _turrets.Add(gun.transform);
                }
            }

            for (int i = 0; i < turrets.Length && i < _turrets.Count; i++)
            {
                Transform t = _turrets[i];
                t.localPosition = SimMath.ToWorld(turrets[i].MountLocal);
                Vector3 dir = SimMath.ToWorldDir(turrets[i].AimLocalDir);
                if (dir.sqrMagnitude > 1e-8f)
                {
                    t.localRotation = Quaternion.FromToRotation(Vector3.forward, dir.normalized);
                }
                // 装填中的炮管压暗，玩家不用读 HUD 也能看出哪门炮好了。
                Renderer r = t.GetComponent<MeshRenderer>();
                if (r != null)
                {
                    float ready = turrets[i].Loaded ? 1f : 0.45f + 0.55f * Mathf.Clamp01(turrets[i].Reload01);
                    r.material.color = new Color(0.32f * ready, 0.34f * ready, 0.38f * ready);
                }
            }
        }

        private static Color PickColor(ShipSnapshot s)
        {
            if (s.IsAlly && s.Role == SquadRole.Flagship) return new Color(0.25f, 0.55f, 1f);   // 玩家旗舰：蓝
            if (s.IsAlly) return new Color(0.3f, 0.8f, 0.45f);                                  // 友军：绿
            return new Color(0.9f, 0.28f, 0.24f);                                               // 敌舰：红
        }

        private static void SetColor(GameObject go, Color c)
        {
            MeshRenderer r = go.GetComponent<MeshRenderer>();
            if (r != null) r.material.color = c;
        }

        private static void DisableCollider(GameObject go)
        {
            Collider col = go.GetComponent<Collider>();
            if (col != null) col.enabled = false;
        }
    }
}
