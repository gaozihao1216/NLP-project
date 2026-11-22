using System;
using System.Collections.Generic;
using StarPawn.Fleet.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Fleet.Domain
{
    public sealed class FakeFleetService : IFleetService
    {
        private readonly List<OwnedShipState> _ships = new List<OwnedShipState>();
        public FakeFleetService()
        {
            _ships.Add(new OwnedShipState { ShipId = new EntityId("ship.flag.001"),
                DefinitionId = new ContentId("ship.g_cruiser"), Name = "唐龙座舰",
                HullIntegrity01 = 1f, IsInFleet = true, AssignedRole = SquadRole.Flagship });
            _ships.Add(new OwnedShipState { ShipId = new EntityId("ship.esc.001"),
                DefinitionId = new ContentId("ship.d_destroyer"), Name = "护航舰",
                HullIntegrity01 = 1f, IsInFleet = true, AssignedRole = SquadRole.Escort });
        }
        public FleetSnapshot Read() => new FleetSnapshot { Ships = _ships.ToArray(),
            Formation = new[] { _ships[0].ShipId, _ships[1].ShipId } };
        public CommandResult SetRole(EntityId shipId, SquadRole role)
        {
            foreach (var s in _ships) if (s.ShipId == shipId) { s.AssignedRole = role; return CommandResult.Ok(); }
            return CommandResult.Fail("SHIP_NOT_FOUND", "舰船不存在");
        }
        public CommandResult SetFormation(EntityId[] shipIds) => CommandResult.Ok();
        public CommandResult ApplyBattleDamage(EntityId[] lostShipIds, float[] hullAfter)
        {
            for (int i = 0; i < lostShipIds.Length; i++)
                foreach (var s in _ships) if (s.ShipId == lostShipIds[i]) s.HullIntegrity01 = hullAfter[i];
            return CommandResult.Ok();
        }
    }
}
