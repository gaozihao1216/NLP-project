// 战斗契约：应用层与其他模块只依赖这一层，不接触 Combat.Domain 内部。
//
// 空间约定（重要）：仿真是二维平面运动，X/Y 为战场局部公里坐标，Heading 为度。
// 3D 表现层负责把平面坐标渲染成星空画面，不反过来把表现结果写回仿真。
// 星图绝对坐标（光分）只在进入战斗时由应用层换算成这里的公里坐标。

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

    /// <summary>
    /// 已解析的战斗属性。由内容定义在进入战斗前算好，Combat 不查内容库、不认识 ContentId 的含义。
    /// 字段不可变，避免不同舰共用同一份属性对象时被互相改写。
    /// </summary>
    public sealed class ResolvedShipStats
    {
        public readonly float MaxHull;
        public readonly float MaxSpeedKps;
        public readonly float TurnRateDps;
        public readonly float GunDamage;
        public readonly float GunRangeKm;
        public readonly float ShellVelocityKps;
        public readonly float ReloadSeconds;
        public readonly float ShieldCapacity;
        public readonly float ShieldRegenPerSec;
        public readonly float Accuracy01;

        public ResolvedShipStats(float maxHull, float maxSpeedKps, float turnRateDps,
            float gunDamage, float gunRangeKm, float shellVelocityKps, float reloadSeconds,
            float shieldCapacity, float shieldRegenPerSec, float accuracy01)
        {
            MaxHull = maxHull; MaxSpeedKps = maxSpeedKps; TurnRateDps = turnRateDps;
            GunDamage = gunDamage; GunRangeKm = gunRangeKm; ShellVelocityKps = shellVelocityKps;
            ReloadSeconds = reloadSeconds; ShieldCapacity = shieldCapacity;
            ShieldRegenPerSec = shieldRegenPerSec; Accuracy01 = accuracy01;
        }

        /// <summary>测试与假实现用的默认属性。</summary>
        public static ResolvedShipStats Default => new ResolvedShipStats(
            100f, 20f, 30f, 10f, 300f, 3f, 3f, 60f, 2f, 0.9f);
    }

    public sealed class FleetEntry
    {
        public EntityId ShipId;
        public ContentId DefinitionId;
        public SquadRole Role;
        public SpawnPoint Spawn;
        public ResolvedShipStats Stats;
        /// <summary>继承的战损：0=上一场被摧毁，1=完好。Combat 不得无条件重置为满。</summary>
        public float InheritedHull01;
        public bool IsLaunchable => InheritedHull01 > 0f && Stats != null;
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

    /// <summary>能量三档预设。原作靠一句口令切换，不做实时滑条。</summary>
    public enum PowerPreset { Navigation, Balanced, Engagement, Custom }

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

    /// <summary>能量分配。三路比例需非负且和为 1，由 Combat 校验。</summary>
    public sealed class PowerAllocationCommand : CombatCommand
    {
        public float Propulsion;
        public float Shield;
        public float Weapons;
        public PowerPreset Preset;
    }

    /// <summary>只读战场快照。表现层消费它，不能把插值结果写回仿真。</summary>
    public sealed class BattleSnapshot
    {
        public SimClock Clock;
        public ShipSnapshot[] Ships;
        public ProjectileSnapshot[] Projectiles;
        public ObstacleSnapshot[] Obstacles;
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
        public float Shield01;
        public PowerPreset PowerMode;
    }

    public sealed class ProjectileSnapshot
    {
        public EntityId ProjectileId;
        public float X;
        public float Y;
    }

    /// <summary>有血量的静止掩体。无速度、无物理翻滚；炮弹命中后扣血，血量归零后不再挡弹。</summary>
    public sealed class ObstacleSnapshot
    {
        public EntityId ObstacleId;
        public float X;
        public float Y;
        public float RadiusKm;
        public float Health01;
        public bool IsAlive;
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

    /// <summary>逐舰战后事实。Fleet 据此回写长期损伤，Combat 不认识长期状态。</summary>
    public sealed class ShipBattleOutcome
    {
        public EntityId ShipId;
        public bool IsLost;
        public float HullAfter01;
        public int AmmoRemaining;
    }

    /// <summary>战斗事实，不指定剧情节点、不指定最终军功。奖励由战略侧决定。</summary>
    public sealed class BattleResult
    {
        public EntityId BattleId;
        public bool Victory;
        public EntityId[] DestroyedEnemyIds;
        public EntityId[] LostAllyIds;
        public ShipBattleOutcome[] ShipOutcomes;
        public float ElapsedSeconds;
        public bool ObjectivesComplete;
    }

    /// <summary>
    /// 战斗会话。表现层/应用层通过它推进战斗、发命令、读快照与事件。
    /// 由 CampaignFacade 暴露，避免外部直接持有 ICombatRuntime 而绕过结算。
    /// </summary>
    public interface IBattleSession
    {
        BattleStartSpec Spec { get; }
        CommandResult Submit(CombatCommand command);
        void Step();
        BattleSnapshot ReadSnapshot();
        BattleEvent[] DrainEvents();
        bool TryGetResult(out BattleResult result);
    }

    public interface ICombatRuntime : IBattleSession
    {
        void Initialize(BattleStartSpec spec);
    }
}
