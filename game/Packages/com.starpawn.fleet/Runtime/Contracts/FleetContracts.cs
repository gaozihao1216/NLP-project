// 舰队契约：维护持久舰船身份与编成，不碰战场逐帧血量与 3D 对象。
using System;
using StarPawn.Foundation;

namespace StarPawn.Fleet.Contracts
{
    /// <summary>玩家拥有的舰船长期状态。与 ShipDefinition、CombatShipState、ShipView 分离。</summary>
    public sealed class OwnedShipState
    {
        public EntityId ShipId;
        public ContentId DefinitionId;
        public string Name;
        public float HullIntegrity01;
        public bool IsInFleet;
        public SquadRole AssignedRole;
    }

    public enum SquadRole { Flagship, Escort, Scout, Carrier }

    public sealed class FleetSnapshot
    {
        public OwnedShipState[] Ships;
        public EntityId[] Formation;
    }

    public interface IFleetService
    {
        FleetSnapshot Read();
        CommandResult SetRole(EntityId shipId, SquadRole role);
        CommandResult SetFormation(EntityId[] shipIds);
        // 战斗结束后由应用层调用，输入战损事实； Fleet 自己算长期损伤，不反向调用战斗。
        CommandResult ApplyBattleDamage(EntityId[] lostShipIds, float[] hullAfter);
    }
}
