// 弹丸视觉：小球沿运动方向拉长成曳光。
//
// 快照只有位置没有速度，方向用上一帧位置差反推（纯视觉，不回写仿真）。
// 弹速 220 km/s、tick 1/30s → 每 tick 约 7.3 单位；帧率 60 时每帧约 3.7 单位，
// 拉长系数 1.2 让弹丸呈现短曳光而不是一串跳动的点。

using UnityEngine;
using StarPawn.Combat.Contracts;

namespace StarPawn.Presentation
{
    public class ProjectileView : MonoBehaviour
    {
        private Vector3 _lastPos;
        private bool _hasLast;

        public void Build(ProjectileSnapshot s)
        {
            gameObject.name = "Projectile." + s.ProjectileId;
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            body.name = "Body";
            body.transform.SetParent(transform, false);
            body.transform.localScale = Vector3.one;
            Collider col = body.GetComponent<Collider>();
            if (col != null) col.enabled = false;
            MeshRenderer r = body.GetComponent<MeshRenderer>();
            if (r != null) r.material.color = new Color(1f, 0.82f, 0.4f);

            _lastPos = SimMath.ToWorld(s.X, s.Y, s.Z);
            _hasLast = false;
            transform.position = _lastPos;
        }

        public void Sync(ProjectileSnapshot s)
        {
            Vector3 pos = SimMath.ToWorld(s.X, s.Y, s.Z);

            if (_hasLast)
            {
                Vector3 delta = pos - _lastPos;
                float travel = delta.magnitude;
                if (travel > 0.01f)
                {
                    transform.rotation = Quaternion.FromToRotation(Vector3.forward, delta / travel);
                    transform.localScale = new Vector3(0.12f, 0.12f, Mathf.Max(0.35f, travel * 1.2f));
                }
            }

            transform.position = pos;
            _lastPos = pos;
            _hasLast = true;
        }
    }
}
