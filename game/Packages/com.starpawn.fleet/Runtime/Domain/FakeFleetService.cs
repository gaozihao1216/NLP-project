using System;
using System.Collections.Generic;
using StarPawn.Fleet.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Fleet.Domain
{
    public sealed class FakeFleetService : IFleetService, IStateCheckpoint
    {
        private readonly List<OwnedShipState> _ships = new List<OwnedShipState>();

        public FakeFleetService()
        {
            _ships.Add(new OwnedShipState { ShipId = new EntityId("ship.flag.001"),
                DefinitionId = new ContentId("ship.g_cruiser"), Name = "唐龙座舰",
                HullIntegrity01 = 1f, IsInFleet = true,
                AssignedRole = SquadRole.Flagship, Readiness = ShipReadiness.Ready });
            _ships.Add(new OwnedShipState { ShipId = new EntityId("ship.esc.001"),
                DefinitionId = new ContentId("ship.d_destroyer"), Name = "护航舰",
                HullIntegrity01 = 1f, IsInFleet = true,
                AssignedRole = SquadRole.Escort, Readiness = ShipReadiness.Ready });
        }

        public FleetSnapshot Read()
        {
            // 深拷贝：元素对象也重建，外部改不动内部。
            var ships = new OwnedShipState[_ships.Count];
            for (int i = 0; i < _ships.Count; i++)
            {
                var s = _ships[i];
                ships[i] = new OwnedShipState
                {
                    ShipId = s.ShipId, DefinitionId = s.DefinitionId, Name = s.Name,
                    HullIntegrity01 = s.HullIntegrity01, IsInFleet = s.IsInFleet,
                    AssignedRole = s.AssignedRole, Readiness = s.Readiness
                };
            }
            var formation = new List<EntityId>();
            foreach (var s in _ships) if (s.IsInFleet && s.Readiness != ShipReadiness.Destroyed) formation.Add(s.ShipId);
            return new FleetSnapshot { Ships = ships, Formation = formation.ToArray() };
        }

        public CommandResult SetRole(EntityId shipId, SquadRole role)
        {
            foreach (var s in _ships)
                if (s.ShipId == shipId) { s.AssignedRole = role; return CommandResult.Ok(); }
            return CommandResult.Fail("SHIP_NOT_FOUND", "舰船不存在");
        }

        public CommandResult SetFormation(EntityId[] shipIds)
        {
            if (shipIds == null) return CommandResult.Fail("INVALID_FORMATION", "编成不能为空");
            return CommandResult.Ok();
        }

        public CommandResult ApplyBattleDamage(ShipDamageReport[] reports)
        {
            if (reports == null) return CommandResult.Ok();
            foreach (var r in reports)
            {
                foreach (var s in _ships)
                {
                    if (s.ShipId != r.ShipId) continue;
                    if (r.IsLost)
                    {
                        s.HullIntegrity01 = 0f;
                        s.IsInFleet = false;
                        s.Readiness = ShipReadiness.Destroyed;
                    }
                    else
                    {
                        s.HullIntegrity01 = Math.Max(0f, Math.Min(1f, r.HullAfter01));
                        s.Readiness = s.HullIntegrity01 >= 0.99f ? ShipReadiness.Ready : ShipReadiness.Damaged;
                    }
                    break;
                }
            }
            return CommandResult.Ok();
        }

        public CommandResult Repair(EntityId shipId)
        {
            foreach (var s in _ships)
            {
                if (s.ShipId != shipId) continue;
                if (s.Readiness == ShipReadiness.Destroyed)
                    return CommandResult.Fail("SHIP_DESTROYED", "已击沉舰船无法维修");
                s.HullIntegrity01 = 1f;
                s.Readiness = ShipReadiness.Ready;
                return CommandResult.Ok();
            }
            return CommandResult.Fail("SHIP_NOT_FOUND", "舰船不存在");
        }

        public bool IsLaunchable(EntityId shipId)
        {
            foreach (var s in _ships)
                if (s.ShipId == shipId)
                    return s.IsInFleet && s.Readiness != ShipReadiness.Destroyed && s.HullIntegrity01 > 0f;
            return false;
        }

        // 检查点：深拷贝，供应用层事务回滚。
        public object CaptureState()
        {
            var snap = new List<OwnedShipState>();
            foreach (var s in _ships)
                snap.Add(new OwnedShipState
                {
                    ShipId = s.ShipId, DefinitionId = s.DefinitionId, Name = s.Name,
                    HullIntegrity01 = s.HullIntegrity01, IsInFleet = s.IsInFleet,
                    AssignedRole = s.AssignedRole, Readiness = s.Readiness
                });
            return snap;
        }

        public void RestoreState(object state)
        {
            var snap = state as List<OwnedShipState>;
            if (snap == null) return;
            _ships.Clear();
            foreach (var s in snap) _ships.Add(s);
        }
    }
}
