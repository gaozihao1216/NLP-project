// 战斗契约：应用层与其他模块只依赖这一层，不接触 Combat.Domain 内部。
//
// 空间约定（重要）：仿真是二维平面运动，X/Y 为战场局部公里坐标，Heading 为度。
// 3D 表现层负责把平面坐标渲染成星空画面，不反过来把表现结果写回仿真。
// 星图绝对坐标（光分）只在进入战斗时由应用层换算成这里的公里坐标。

using System;
using System.Collections.Generic;
using StarPawn.Foundation;

namespace StarPawn.Combat.Contracts
{
    /// <summary>
    /// 战斗开始输入。只含解析后的数据，不含对战略模块的反向引用。
    /// 必须自包含：任务 ID 与星系锚点都在 spec 里，结算不依赖门面当时的当前任务。
    /// </summary>
    public sealed class BattleStartSpec
    {
        public EntityId BattleId;
        /// <summary>产生这场战斗的任务，奖励从它读，不由门面的可变字段决定。</summary>
        public string MissionId;
        /// <summary>出发星系。战斗只做局部仿真，不修改星图。</summary>
        public EntityId OriginSystemId;
        /// <summary>交战星系。胜利后由结算工作流主张管辖，Combat 自己不改星图。</summary>
        public EntityId TargetSystemId;
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
        /// <summary>玩家分队槽位 0..3；敌舰也填，用于统一的编成校验。</summary>
        public int SquadIndex;
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

    /// <summary>
    /// 战斗契约的共享校验。Combat.Domain 与 Application 都调用这里，
    /// 避免并行开发时两个人各写一套边界检查，行为慢慢分叉。
    /// </summary>
    public static class BattleRules
    {
        /// <summary>玩家最多指挥四个分队，对应原型的四个槽位。</summary>
        public const int MaxSquads = 4;

        public static CommandResult ValidateSpec(BattleStartSpec spec)
        {
            if (spec == null) return CommandResult.Fail("NULL_SPEC", "战斗输入为空");
            if (spec.BattleId.IsEmpty) return CommandResult.Fail("BAD_BATTLE_ID", "缺少战斗 ID");
            if (spec.FixedStep <= 0f) return CommandResult.Fail("BAD_FIXED_STEP", "仿真步长必须为正");
            if (spec.Allies == null || spec.Enemies == null)
                return CommandResult.Fail("MISSING_FLEET", "敌我编成不能为空");

            var seen = new HashSet<EntityId>();
            var squads = new HashSet<int>();
            foreach (var e in Enumerate(spec.Allies, spec.Enemies))
            {
                if (!e.IsLaunchable) continue;
                if (e.ShipId.IsEmpty || !seen.Add(e.ShipId))
                    return CommandResult.Fail("BAD_SHIP_ID", "舰船 ID 为空或重复: " + e.ShipId);
                if (e.SquadIndex < 0 || e.SquadIndex >= MaxSquads)
                    return CommandResult.Fail("SQUAD_OUT_OF_RANGE", "分队序号必须在 0..3: " + e.SquadIndex);
                squads.Add(e.SquadIndex);
                if (e.InheritedHull01 <= 0f || e.InheritedHull01 > 1f)
                    return CommandResult.Fail("BAD_HULL", "继承战损必须在 (0,1]: " + e.InheritedHull01);
            }
            if (squads.Count > MaxSquads)
                return CommandResult.Fail("TOO_MANY_SQUADS", "分队数超过 " + MaxSquads);
            return CommandResult.Ok();
        }

        private static IEnumerable<FleetEntry> Enumerate(FleetEntry[] a, FleetEntry[] b)
        {
            foreach (var e in a) yield return e;
            foreach (var e in b) yield return e;
        }

        /// <summary>三路能量必须非负且和为 1。归一化不能掩盖负投入。</summary>
        public static CommandResult ValidatePower(PowerAllocationCommand cmd)
        {
            if (cmd == null) return CommandResult.Fail("NULL_COMMAND", "能量命令为空");
            if (cmd.Propulsion < 0f || cmd.Shield < 0f || cmd.Weapons < 0f)
                return CommandResult.Fail("INVALID_POWER", "三路能量比例不能为负");
            float sum = cmd.Propulsion + cmd.Shield + cmd.Weapons;
            if (sum < 0.999f || sum > 1.001f)
                return CommandResult.Fail("INVALID_POWER", "三路能量比例之和必须为 1，当前 " + sum);
            return CommandResult.Ok();
        }
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
