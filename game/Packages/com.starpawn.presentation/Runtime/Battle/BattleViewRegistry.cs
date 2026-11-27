// EntityId → View 的映射与生命周期管理。
//
// 规则（vertical-slice-plan）：
//   快照出现新实体 → 创建 View；死亡 → Sync 内隐藏；
//   弹丸从快照消失 → 销毁 View（弹丸是瞬态实体，不能无限堆积）。
// BattleEvent 不读 Detail 文案，特效由 Kind + Shot 结构化载荷驱动。

using System.Collections.Generic;
using UnityEngine;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Presentation
{
    public class BattleViewRegistry : MonoBehaviour
    {
        public BattleFx Fx;

        private readonly Dictionary<EntityId, ShipView> _ships = new Dictionary<EntityId, ShipView>();
        private readonly Dictionary<EntityId, ProjectileView> _projectiles = new Dictionary<EntityId, ProjectileView>();
        private readonly Dictionary<EntityId, ObstacleView> _obstacles = new Dictionary<EntityId, ObstacleView>();

        /// <summary>最近一次快照，供 HUD 查目标位置 / 距离。</summary>
        private readonly Dictionary<EntityId, ShipSnapshot> _lastShips = new Dictionary<EntityId, ShipSnapshot>();
        private readonly Dictionary<EntityId, ObstacleSnapshot> _lastObstacles = new Dictionary<EntityId, ObstacleSnapshot>();

        public void Sync(BattleSnapshot snap)
        {
            if (snap == null) return;

            HashSet<EntityId> aliveProjectiles = new HashSet<EntityId>();

            if (snap.Ships != null)
            {
                for (int i = 0; i < snap.Ships.Length; i++)
                {
                    ShipSnapshot s = snap.Ships[i];
                    _lastShips[s.ShipId] = s;
                    ShipView view = GetOrCreateShip(s);
                    view.Sync(s);
                }
            }

            if (snap.Projectiles != null)
            {
                for (int i = 0; i < snap.Projectiles.Length; i++)
                {
                    ProjectileSnapshot p = snap.Projectiles[i];
                    aliveProjectiles.Add(p.ProjectileId);
                    ProjectileView view;
                    if (!_projectiles.TryGetValue(p.ProjectileId, out view))
                    {
                        GameObject go = new GameObject("ProjectileView");
                        go.transform.SetParent(transform, false);
                        view = go.AddComponent<ProjectileView>();
                        view.Build(p);
                        _projectiles.Add(p.ProjectileId, view);
                    }
                    view.Sync(p);
                }
            }

            // 快照里消失的弹丸 = 已命中或超时，销毁视觉。
            List<EntityId> dead = new List<EntityId>();
            foreach (KeyValuePair<EntityId, ProjectileView> kv in _projectiles)
            {
                if (!aliveProjectiles.Contains(kv.Key)) dead.Add(kv.Key);
            }
            for (int i = 0; i < dead.Count; i++)
            {
                Destroy(_projectiles[dead[i]].gameObject);
                _projectiles.Remove(dead[i]);
            }

            if (snap.Obstacles != null)
            {
                for (int i = 0; i < snap.Obstacles.Length; i++)
                {
                    ObstacleSnapshot o = snap.Obstacles[i];
                    _lastObstacles[o.ObstacleId] = o;
                    ObstacleView view;
                    if (!_obstacles.TryGetValue(o.ObstacleId, out view))
                    {
                        GameObject go = new GameObject("ObstacleView");
                        go.transform.SetParent(transform, false);
                        view = go.AddComponent<ObstacleView>();
                        view.Build(o);
                        _obstacles.Add(o.ObstacleId, view);
                    }
                    view.Sync(o);
                }
            }
        }

        public void HandleEvents(BattleEvent[] events)
        {
            if (events == null || Fx == null) return;

            for (int i = 0; i < events.Length; i++)
            {
                BattleEvent e = events[i];
                switch (e.Kind)
                {
                    case "Fire":
                        if (e.Shot != null)
                        {
                            Fx.Flash(SimMath.ToWorld(e.Shot.Origin),
                                new Color(1f, 0.9f, 0.45f), 0.55f, 0.12f);
                        }
                        break;

                    case "Hit":
                        Fx.Flash(PositionOf(e.TargetId, 1.2f),
                            new Color(1f, 0.75f, 0.25f), 1.1f);
                        break;

                    case "Critical":
                        Fx.Flash(PositionOf(e.TargetId, 1.4f),
                            new Color(1f, 0.45f, 0.15f), 1.8f, 0.3f);
                        break;

                    case "Ricochet":
                        Fx.Flash(PositionOf(e.TargetId, 1.0f),
                            new Color(0.85f, 0.9f, 1f), 0.6f, 0.15f);
                        break;

                    case "Destroyed":
                        Fx.Flash(PositionOf(e.TargetId, 1.5f),
                            new Color(1f, 0.5f, 0.2f), 3.5f, 0.5f);
                        break;

                    default:
                        break; // PowerLow / LockLost / NotReady 等：灰盒阶段不做成特效
                }
            }
        }

        /// <summary>事件目标的位置（供特效落点）。找不到实体时返回原点并给出最大可见尺寸提示。</summary>
        private Vector3 PositionOf(EntityId id, float fallbackScaleHint)
        {
            ShipSnapshot s;
            if (_lastShips.TryGetValue(id, out s)) return SimMath.ToWorld(s.X, s.Y, s.Z);

            ObstacleSnapshot o;
            if (_lastObstacles.TryGetValue(id, out o)) return SimMath.ToWorld(o.X, o.Y, o.Z);

            return Vector3.zero;
        }

        public ShipSnapshot TryGetShipSnapshot(EntityId id)
        {
            ShipSnapshot s;
            return _lastShips.TryGetValue(id, out s) ? s : null;
        }

        public ShipView TryGetShipView(EntityId id)
        {
            ShipView v;
            return _ships.TryGetValue(id, out v) ? v : null;
        }

        private ShipView GetOrCreateShip(ShipSnapshot s)
        {
            ShipView view;
            if (_ships.TryGetValue(s.ShipId, out view)) return view;

            GameObject go = new GameObject("ShipView");
            go.transform.SetParent(transform, false);
            view = go.AddComponent<ShipView>();
            view.Build(s);
            _ships.Add(s.ShipId, view);
            return view;
        }

        /// <summary>重开局：销毁全部 View，清空映射。</summary>
        public void ResetAll()
        {
            foreach (KeyValuePair<EntityId, ShipView> kv in _ships)
            {
                if (kv.Value != null) Destroy(kv.Value.gameObject);
            }
            foreach (KeyValuePair<EntityId, ProjectileView> kv in _projectiles)
            {
                if (kv.Value != null) Destroy(kv.Value.gameObject);
            }
            foreach (KeyValuePair<EntityId, ObstacleView> kv in _obstacles)
            {
                if (kv.Value != null) Destroy(kv.Value.gameObject);
            }
            _ships.Clear();
            _projectiles.Clear();
            _obstacles.Clear();
            _lastShips.Clear();
            _lastObstacles.Clear();
        }
    }
}
