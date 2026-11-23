// 舰队契约：维护持久舰船身份与编成，不碰战场逐帧血量与 3D 对象。
using System;
using StarPawn.Foundation;

namespace StarPawn.Fleet.Contracts
{
    /// <summary>战备状态。决定能否出战，与战场内 IsAlive 是两套概念。</summary>
    public enum ShipReadiness { Ready, Damaged, InRepair, Destroyed }

    /// <summary>玩家拥有的舰船长期状态。与 ShipDefinition、CombatShipState、ShipView 分离。</summary>
    public sealed class OwnedShipState
    {
        public EntityId ShipId;
        public ContentId DefinitionId;
        public string Name;
        public float HullIntegrity01;
        public bool IsInFleet;
        public SquadRole AssignedRole;
        public ShipReadiness Readiness;
    }

    public enum SquadRole { Flagship, Escort, Scout, Carrier }

    public sealed class FleetSnapshot
    {
        public OwnedShipState[] Ships;
        public EntityId[] Formation;
    }

    /// <summary>战后回写用的中性事实结构。Fleet 不引用 Combat 契约，由应用层映射。</summary>
    public sealed class ShipDamageReport
    {
        public EntityId ShipId;
        public bool IsLost;
        public float HullAfter01;
    }

    public interface IFleetService
    {
        FleetSnapshot Read();
        CommandResult SetRole(EntityId shipId, SquadRole role);
        CommandResult SetFormation(EntityId[] shipIds);
        /// <summary>
        /// 战后回写：击沉舰标记为 Destroyed 且退出舰队；幸存舰按 HullAfter01 更新，
        /// 低于阈值转 Damaged，仍可出战但继承损伤；维修完成由单独命令处理。
        /// </summary>
        CommandResult ApplyBattleDamage(ShipDamageReport[] reports);
        /// <summary>维修。把舰船恢复到 Ready，HullIntegrity01 回到 1。</summary>
        CommandResult Repair(EntityId shipId);
        /// <summary>查询某舰能否出战：在舰队中、非 Destroyed、HullIntegrity01 &gt; 0。</summary>
        bool IsLaunchable(EntityId shipId);
    }
}
