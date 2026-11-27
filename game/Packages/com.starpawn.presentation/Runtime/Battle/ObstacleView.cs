// 陨石视觉：低模球占位，半径直接取快照（RadiusKm），血量越低越暗。
// 每个 ID 派生一个固定随机姿态与色偏，保证同一局里石块有差别且可复现。

using UnityEngine;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Presentation
{
    public class ObstacleView : MonoBehaviour
    {
        public EntityId ObstacleId { get; private set; }

        private Color _baseColor;

        public void Build(ObstacleSnapshot s)
        {
            ObstacleId = s.ObstacleId;
            gameObject.name = "Obstacle." + s.ObstacleId;

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            body.name = "Body";
            body.transform.SetParent(transform, false);
            body.transform.localScale = Vector3.one;
            Collider col = body.GetComponent<Collider>();
            if (col != null) col.enabled = false;
            MeshRenderer r = body.GetComponent<MeshRenderer>();
            if (r != null) r.material.color = _baseColor = PickColor(s.ObstacleId.ToString());

            // 视觉姿态固定但各不相同；不做翻滚（设定：完全静止）。
            int hash = s.ObstacleId.ToString().GetHashCode();
            body.transform.localRotation = Quaternion.Euler(hash % 360, (hash / 7) % 360, (hash / 13) % 360);
            body.transform.localScale = Vector3.one;

            Sync(s);
        }

        public void Sync(ObstacleSnapshot s)
        {
            transform.position = SimMath.ToWorld(s.X, s.Y, s.Z);
            transform.localScale = Vector3.one * Mathf.Max(0.1f, s.RadiusKm * 2f / SimMath.KmPerUnit);

            if (!s.IsAlive)
            {
                gameObject.SetActive(false);
                return;
            }
            gameObject.SetActive(true);

            MeshRenderer r = GetComponentInChildren<MeshRenderer>();
            if (r != null)
            {
                r.material.color = Color.Lerp(Color.black, _baseColor, 0.35f + 0.65f * Mathf.Clamp01(s.Health01));
            }
        }

        private static Color PickColor(string seed)
        {
            int hash = seed.GetHashCode();
            float hue = (hash % 100) / 100f * 0.08f; // 灰棕到灰之间微差
            Color c = Color.Lerp(new Color(0.42f, 0.37f, 0.31f), new Color(0.5f, 0.48f, 0.45f), hue);
            return c;
        }
    }
}
