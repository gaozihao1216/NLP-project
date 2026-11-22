// 战斗契约：应用层与其他模块只依赖这一层，不接触 Combat.Domain 内部。

using System;
using StarPawn.Foundation;

namespace StarPawn.Combat.Contracts
{
    /// <summary>战斗开始输入。只含解析后的数据，不含对战略模块的反向引用。</summary>
    public sealed class BattleStartSpec
    {
        public EntityId BattleId;
        public FleetEntry[] Allies;
        public FleetEntry[] Enemies;
        public string BattlefieldLayout;
        public BattleObjective Objective;
        public int RandomSeed;
        public float FixedStep;
    }

    public sealed class FleetEntry
    {
        public EntityId ShipId;
        public ContentId DefinitionId;
        public SquadRole Role;
        public SpawnPoint Spawn;
    }

    public readonly struct SpawnPoint
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Heading;
        public SpawnPoint(float x, float y, float heading) { X = x; Y = y; Heading = heading; }
    }

    public enum SquadRole { Flagship, Escort, Scout, Carrier }
    public enum SquadOrder { Follow, AttackTarget, Scout, Withdraw, Hold, Launch }

    public sealed class BattleObjective
    {
        public string Kind;           // "DestroyAll" / "Survive" / "ProtectTarget"
        public EntityId TargetId;
        public float TimeLimitSeconds;
    }

    /// <summary>玩家/AI 输入统一走命令。分队和敌舰行为也必须生成命令，不能绕过装填与射程限制。</summary>
    public abstract class CombatCommand
    {
        public EntityId ShipId;
    }

    public sealed class HelmCommand : CombatCommand
    {
        public float Throttle;
        public float Rudder;
    }

    public sealed class AimCommand : CombatCommand
    {
        public float WorldX;
        public float WorldY;
    }

    public sealed class FireCommand : CombatCommand { }

    public sealed class SquadCommand : CombatCommand
    {
        public SquadRole Squad;
        public SquadOrder Order;
        public EntityId TargetId;
    }

    /// <summary>只读战场快照。表现层消费它，不能把插值结果写回仿真。</summary>
    public sealed class BattleSnapshot
    {
        public SimClock Clock;
        public ShipSnapshot[] Ships;
        public ProjectileSnapshot[] Projectiles;
        public BattlePhase Phase;
    }

    public sealed class ShipSnapshot
    {
        public EntityId ShipId;
        public ContentId DefinitionId;
        public bool IsAlly;
        public SquadRole Role;
        public float X;
        public float Y;
        public float Heading;
        public float Speed;
        public float Health01;
        public bool IsAlive;
        public float Reload01;
    }

    public sealed class ProjectileSnapshot
    {
        public EntityId ProjectileId;
        public float X;
        public float Y;
    }

    public enum BattlePhase { Running, Victory, Defeat }

    /// <summary>瞬时反馈事件，供特效/音效/副官提示消费。</summary>
    public sealed class BattleEvent
    {
        public string Kind;           // "Fire" / "Hit" / "Critical" / "Ricochet" / "Destroyed"
        public EntityId SourceId;
        public EntityId TargetId;
        public SimClock At;
        public string Detail;
    }

    /// <summary>战斗事实，不指定剧情节点、不指定最终军功。奖励由战略侧决定。</summary>
    public sealed class BattleResult
    {
        public EntityId BattleId;
        public bool Victory;
        public EntityId[] DestroyedEnemyIds;
        public EntityId[] LostAllyIds;
        public float ElapsedSeconds;
        public bool ObjectivesComplete;
    }

    public interface ICombatRuntime
    {
        void Initialize(BattleStartSpec spec);
        CommandResult Submit(CombatCommand command);
        void Step();
        BattleSnapshot ReadSnapshot();
        BattleEvent[] DrainEvents();
        bool TryGetResult(out BattleResult result);
    }
}
