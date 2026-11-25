// 战斗契约：应用层与其他模块只依赖这一层，不接触 Combat.Domain 内部。
//
// 空间约定（重要）：仿真是三维的。契约层不绑 Unity，但坐标按右手系理解：
//   X 右、Y 上、Z 前；舰体局部 +Z 为舰首。
// 单位：位置公里、速度 km/s、加速度 km/s²、角度度、角速度度/秒、时间秒。
// 星图绝对坐标（光分）只在进入战斗时由应用层换算成这里的公里坐标。

using System;
using System.Collections.Generic;
using StarPawn.Foundation;

namespace StarPawn.Combat.Contracts
{
    /// <summary>
    /// 三维向量。Domain 程序集是 noEngineReferences，不能用 UnityEngine.Vector3，
    /// 而提前量解算又必须和表现层共用同一套数学，所以放在契约层。
    /// </summary>
    public readonly struct Vec3
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);
        public static readonly Vec3 Up = new Vec3(0f, 1f, 0f);
        public static readonly Vec3 Forward = new Vec3(0f, 0f, 1f);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(float s, Vec3 a) => a * s;
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public float SqrLength => X * X + Y * Y + Z * Z;
        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);

        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        /// <summary>零向量归一化返回零向量，不产生 NaN。</summary>
        public Vec3 Normalized
        {
            get
            {
                float len = Length;
                return len > 1e-6f ? new Vec3(X / len, Y / len, Z / len) : Zero;
            }
        }

        public static float Distance(Vec3 a, Vec3 b) => (a - b).Length;

        public override string ToString() => "(" + X.ToString("0.###") + ", " + Y.ToString("0.###") + ", " + Z.ToString("0.###") + ")";
    }

    /// <summary>转动轴。三轴的转速上限与角加速度各自独立，都是舰体参数。</summary>
    public enum ManeuverAxis { Yaw = 0, Pitch = 1, Roll = 2 }

    /// <summary>
    /// 机动参数。全部来自舰体配置（content/ships 的 maneuver 块），不在战斗代码里写倍率。
    /// 舰级差异（轻快 / 笨重）只能靠这里的数不同来体现。
    /// </summary>
    public readonly struct ManeuverProfile
    {
        /// <summary>主推进最大加速度 km/s²。</summary>
        public readonly float MaxThrustKmS2;
        public readonly float YawRateDps;
        public readonly float PitchRateDps;
        public readonly float RollRateDps;
        public readonly float YawAccelDps2;
        public readonly float PitchAccelDps2;
        public readonly float RollAccelDps2;

        public ManeuverProfile(float maxThrustKmS2,
            float yawRateDps, float pitchRateDps, float rollRateDps,
            float yawAccelDps2, float pitchAccelDps2, float rollAccelDps2)
        {
            MaxThrustKmS2 = maxThrustKmS2;
            YawRateDps = yawRateDps; PitchRateDps = pitchRateDps; RollRateDps = rollRateDps;
            YawAccelDps2 = yawAccelDps2; PitchAccelDps2 = pitchAccelDps2; RollAccelDps2 = rollAccelDps2;
        }

        public float Rate(ManeuverAxis axis)
        {
            switch (axis)
            {
                case ManeuverAxis.Yaw: return YawRateDps;
                case ManeuverAxis.Pitch: return PitchRateDps;
                default: return RollRateDps;
            }
        }

        public float Accel(ManeuverAxis axis)
        {
            switch (axis)
            {
                case ManeuverAxis.Yaw: return YawAccelDps2;
                case ManeuverAxis.Pitch: return PitchAccelDps2;
                default: return RollAccelDps2;
            }
        }

        /// <summary>
        /// 缺省推导：只在配置缺失时使用（假实现、单元测试、旧调用点）。
        /// **不要把它当运行时规则** —— 真实数值一律走配置，否则舰级差异调不出来。
        /// </summary>
        public static ManeuverProfile Derive(float maxSpeedKps, float yawRateDps)
        {
            float pitch = yawRateDps * 0.6f;
            float roll = yawRateDps * 1.8f;
            return new ManeuverProfile(
                maxSpeedKps / 8f,
                yawRateDps, pitch, roll,
                yawRateDps * 4f, pitch * 4f, roll * 4f);
        }

        public static ManeuverProfile Default => Derive(20f, 30f);
    }

    /// <summary>
    /// 能源参数。同样全部来自舰体配置（content/ships 的 power 块），代码里不写死耗电率。
    ///
    /// 单位是「能量点」而不是千瓦：这里的数值只为游戏手感标定，不追求物理量纲一致。
    /// 容量 / 满推力耗电率 ≈ 满推力能撑的秒数，调手感就调这两个。
    /// </summary>
    public readonly struct PowerProfile
    {
        /// <summary>储能总量。</summary>
        public readonly float Capacity;
        /// <summary>反应堆再生速率（每秒回充点数）。</summary>
        public readonly float RegenPerSec;
        /// <summary>主推进满档时的耗电率（每秒）。</summary>
        public readonly float PropulsionDrainPerSec;
        /// <summary>三轴姿态控制满输入时的额外耗电率（每秒）。RCS 和主推进共用一路电力。</summary>
        public readonly float TurnDrainPerSec;
        /// <summary>护盾按标称速率回充时的耗电率（每秒）。</summary>
        public readonly float ShieldDrainPerSec;
        /// <summary>每次开火消耗的点数。</summary>
        public readonly float WeaponDrainPerShot;
        /// <summary>
        /// 推进重启阈值（占容量的比例）。能源耗尽后推进被强制归零，
        /// 必须回充到这个比例以上才重新可用——滞回是为了避免「一有电就又熄火」的抖动。
        /// </summary>
        public readonly float RestartFraction;

        public PowerProfile(float capacity, float regenPerSec, float propulsionDrainPerSec,
            float turnDrainPerSec, float shieldDrainPerSec, float weaponDrainPerShot, float restartFraction)
        {
            Capacity = capacity > 1f ? capacity : 1f;
            RegenPerSec = regenPerSec > 0f ? regenPerSec : 0f;
            PropulsionDrainPerSec = propulsionDrainPerSec > 0f ? propulsionDrainPerSec : 0f;
            TurnDrainPerSec = turnDrainPerSec > 0f ? turnDrainPerSec : 0f;
            ShieldDrainPerSec = shieldDrainPerSec > 0f ? shieldDrainPerSec : 0f;
            WeaponDrainPerShot = weaponDrainPerShot > 0f ? weaponDrainPerShot : 0f;
            RestartFraction = restartFraction > 0f && restartFraction < 1f ? restartFraction : 0.2f;
        }

        /// <summary>缺省值：只在配置缺失时使用（假实现、单元测试、旧调用点）。</summary>
        public static PowerProfile Default =>
            new PowerProfile(120f, 3.4f, 2.2f, 0.8f, 1.2f, 5f, 0.2f);
    }

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
        /// <summary>偏航转速上限，等价于 Maneuver.YawRateDps，保留给旧调用点。</summary>
        public readonly float TurnRateDps;
        public readonly float GunDamage;
        public readonly float GunRangeKm;
        public readonly float ShellVelocityKps;
        public readonly float ReloadSeconds;
        public readonly float ShieldCapacity;
        public readonly float ShieldRegenPerSec;
        public readonly float Accuracy01;

        /// <summary>碰撞半径 km。舰体尺寸参数，不由血量推导（血量只是缺省值来源）。</summary>
        public readonly float HullRadiusKm;
        /// <summary>机动参数：推力加速度、三轴转速上限与角加速度。</summary>
        public readonly ManeuverProfile Maneuver;
        /// <summary>散布角上限（度）。精度 1 时无散布，精度 0 时达到该上限。</summary>
        public readonly float MaxSpreadDeg;
        /// <summary>能源参数：容量、再生与各路耗电率。</summary>
        public readonly PowerProfile Power;

        /// <summary>
        /// 配置缺省散布上限（度）。标定依据：在 60% 射程上，落点散布半径约等于 1.4 倍舰体半径，
        /// 保证命中率在三到五成之间。原计划的 8° 在 180 km 处横向 25 km，比舰体半径大两个量级，
        /// 会导致整场打不中——已作废。
        /// </summary>
        public const float DefaultMaxSpreadDeg = 1.0f;

        /// <summary>旧构造：缺省半径按血量三次方根推导，机动走 ManeuverProfile.Derive。</summary>
        public ResolvedShipStats(float maxHull, float maxSpeedKps, float turnRateDps,
            float gunDamage, float gunRangeKm, float shellVelocityKps, float reloadSeconds,
            float shieldCapacity, float shieldRegenPerSec, float accuracy01)
            : this(maxHull, maxSpeedKps, turnRateDps, gunDamage, gunRangeKm, shellVelocityKps,
                   reloadSeconds, shieldCapacity, shieldRegenPerSec, accuracy01,
                   HullRadiusKmFromHull(maxHull), ManeuverProfile.Derive(maxSpeedKps, turnRateDps),
                   DefaultMaxSpreadDeg, PowerProfile.Default)
        {
        }

        /// <summary>完整构造：真实配置走这条，机动与半径都显式给出，能源走缺省。</summary>
        public ResolvedShipStats(float maxHull, float maxSpeedKps, float turnRateDps,
            float gunDamage, float gunRangeKm, float shellVelocityKps, float reloadSeconds,
            float shieldCapacity, float shieldRegenPerSec, float accuracy01,
            float hullRadiusKm, ManeuverProfile maneuver, float maxSpreadDeg)
            : this(maxHull, maxSpeedKps, turnRateDps, gunDamage, gunRangeKm, shellVelocityKps,
                   reloadSeconds, shieldCapacity, shieldRegenPerSec, accuracy01,
                   hullRadiusKm, maneuver, maxSpreadDeg, PowerProfile.Default)
        {
        }

        /// <summary>全参构造：连能源一起给出。舰级差异体现在每一个字段上。</summary>
        public ResolvedShipStats(float maxHull, float maxSpeedKps, float turnRateDps,
            float gunDamage, float gunRangeKm, float shellVelocityKps, float reloadSeconds,
            float shieldCapacity, float shieldRegenPerSec, float accuracy01,
            float hullRadiusKm, ManeuverProfile maneuver, float maxSpreadDeg, PowerProfile power)
        {
            MaxHull = maxHull; MaxSpeedKps = maxSpeedKps; TurnRateDps = turnRateDps;
            GunDamage = gunDamage; GunRangeKm = gunRangeKm; ShellVelocityKps = shellVelocityKps;
            ReloadSeconds = reloadSeconds; ShieldCapacity = shieldCapacity;
            ShieldRegenPerSec = shieldRegenPerSec; Accuracy01 = accuracy01;
            HullRadiusKm = hullRadiusKm; Maneuver = maneuver;
            MaxSpreadDeg = maxSpreadDeg > 0f ? maxSpreadDeg : DefaultMaxSpreadDeg;
            Power = power;
        }

        /// <summary>缺省半径：0.04 × cbrt(MaxHull) → 120/300/800 血对应 0.20/0.27/0.37 km。</summary>
        public static float HullRadiusKmFromHull(float maxHull)
            => 0.04f * (float)Math.Pow(Math.Max(1f, maxHull), 1.0 / 3.0);

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

    /// <summary>出场位置与初始姿态（3D）。3 参构造保留给旧调用点，等价于 z=0、pitch=0、roll=0。</summary>
    public readonly struct SpawnPoint
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly float Yaw;
        public readonly float Pitch;
        public readonly float Roll;

        public SpawnPoint(float x, float y, float heading)
        {
            X = x; Y = y; Z = 0f; Yaw = heading; Pitch = 0f; Roll = 0f;
        }

        public SpawnPoint(float x, float y, float z, float yaw, float pitch, float roll)
        {
            X = x; Y = y; Z = z; Yaw = yaw; Pitch = pitch; Roll = roll;
        }
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

    /// <summary>
    /// 操舵命令。Thrust 是主推力的归一化输入（由输入层的档位决定，-0.5 为反推），
    /// Yaw/Pitch/Roll 是角加速度输入（-1..1），不是转速本身。
    /// FlightAssist 是 sticky 开关：仿真记住最近一次值，关掉后退化为纯牛顿。
    /// </summary>
    public sealed class HelmCommand : CombatCommand
    {
        public float Thrust;
        public float Yaw;
        public float Pitch;
        public float Roll;
        public bool FlightAssist = true;
    }

    /// <summary>瞄准点（世界坐标，km）。炮有射界锥，出锥由仿真钳到锥边。</summary>
    public sealed class AimCommand : CombatCommand
    {
        public float WorldX;
        public float WorldY;
        public float WorldZ;
    }

    /// <summary>
    /// 锁定目标。锁定后由仿真每 tick 用相对速度解提前量、写回快照的 AimPoint，
    /// 玩家不必自己算提前量；目标死亡或超出锁定距离会自动解除。
    /// TargetId 留空表示主动解除锁定。
    /// </summary>
    public sealed class LockTargetCommand : CombatCommand
    {
        public EntityId TargetId;
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

    /// <summary>单舰快照。位置/姿态/速度都是三维，HUD 靠 Vx/Vy/Vz 画速度矢量与提前量。</summary>
    public sealed class ShipSnapshot
    {
        public EntityId ShipId;
        public ContentId DefinitionId;
        public bool IsAlly;
        public SquadRole Role;
        public float X;
        public float Y;
        public float Z;
        /// <summary>偏航角（度，绕 +Y），右转为正。</summary>
        public float Yaw;
        /// <summary>俯仰角（度），抬头为正。</summary>
        public float Pitch;
        /// <summary>滚转角（度），右滚为正。</summary>
        public float Roll;
        public float Vx;
        public float Vy;
        public float Vz;
        /// <summary>本体轴角速度（度/秒）：x=俯仰 y=偏航 z=滚转。HUD 的转速表与输入层阻尼都要用。</summary>
        public float PitchRate;
        public float YawRate;
        public float RollRate;
        /// <summary>速度标量，HUD 显示用。</summary>
        public float Speed;
        public float Health01;
        public bool IsAlive;
        public float Reload01;
        public float Shield01;
        /// <summary>本舰当前的能量三路分配预设。能源是每舰一份，不是全队共用。</summary>
        public PowerPreset PowerMode;
        /// <summary>剩余能源比例 0..1。HUD 的能源条读它。</summary>
        public float Energy01;
        /// <summary>能源耗尽导致推进被锁（推力档位已被强制归零）。</summary>
        public bool PropulsionLocked;
        /// <summary>当前锁定目标；为空表示未锁定。</summary>
        public EntityId LockedTargetId;
        /// <summary>仿真为本舰解算出的开火瞄准点（含提前量）。HUD 画提前量准星读它。</summary>
        public float AimX;
        public float AimY;
        public float AimZ;
        public bool HasAim;
    }

    public sealed class ProjectileSnapshot
    {
        public EntityId ProjectileId;
        public float X;
        public float Y;
        public float Z;
    }

    /// <summary>有血量的静止掩体。无速度、无物理翻滚；炮弹命中后扣血，血量归零后不再挡弹。</summary>
    public sealed class ObstacleSnapshot
    {
        public EntityId ObstacleId;
        public float X;
        public float Y;
        public float Z;
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
        /// <summary>推力档位下限（反推）与上限（全速）。</summary>
        public const float MinThrust = -0.5f;
        public const float MaxThrust = 1f;
        /// <summary>炮的射界锥半角（度）。瞄准点出锥时钳到锥边。</summary>
        public const float GunArcHalfAngleDeg = 15f;
        /// <summary>
        /// 锁定距离 = 炮的射程 × 该系数。留一点余量，让玩家能在进入射程前先锁定、
        /// 观察提前量准星；再远锁定也没有意义（打不到）。
        /// </summary>
        public const float LockRangeFactor = 1.2f;

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
                var statCheck = ValidateStats(e.Stats);
                if (!statCheck.Success) return statCheck;
            }
            if (squads.Count > MaxSquads)
                return CommandResult.Fail("TOO_MANY_SQUADS", "分队数超过 " + MaxSquads);
            return CommandResult.Ok();
        }

        /// <summary>机动与尺寸参数必须是正数，否则仿真里会出现除零或负加速度。</summary>
        public static CommandResult ValidateStats(ResolvedShipStats stats)
        {
            if (stats == null) return CommandResult.Fail("MISSING_STATS", "缺少已解析的战斗属性");
            if (stats.MaxHull <= 0f) return CommandResult.Fail("BAD_STATS", "最大血量必须为正");
            if (stats.HullRadiusKm <= 0f) return CommandResult.Fail("BAD_STATS", "碰撞半径必须为正");
            var m = stats.Maneuver;
            if (m.MaxThrustKmS2 <= 0f) return CommandResult.Fail("BAD_STATS", "最大推力必须为正");
            for (int i = 0; i < 3; i++)
            {
                var axis = (ManeuverAxis)i;
                if (m.Rate(axis) <= 0f) return CommandResult.Fail("BAD_STATS", "转速上限必须为正: " + axis);
                if (m.Accel(axis) <= 0f) return CommandResult.Fail("BAD_STATS", "角加速度必须为正: " + axis);
            }
            if (stats.Power.Capacity <= 0f) return CommandResult.Fail("BAD_STATS", "能源容量必须为正");
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
            if (cmd.ShipId.IsEmpty) return CommandResult.Fail("BAD_SHIP_ID", "能量命令缺少舰船 ID");
            if (cmd.Propulsion < 0f || cmd.Shield < 0f || cmd.Weapons < 0f)
                return CommandResult.Fail("INVALID_POWER", "三路能量比例不能为负");
            float sum = cmd.Propulsion + cmd.Shield + cmd.Weapons;
            if (sum < 0.999f || sum > 1.001f)
                return CommandResult.Fail("INVALID_POWER", "三路能量比例之和必须为 1，当前 " + sum);
            return CommandResult.Ok();
        }

        /// <summary>操舵输入范围。超出范围直接拒绝，不静默截断——截断会让玩家以为是别的问题。</summary>
        public static CommandResult ValidateHelm(HelmCommand cmd)
        {
            if (cmd == null) return CommandResult.Fail("NULL_COMMAND", "操舵命令为空");
            if (cmd.ShipId.IsEmpty) return CommandResult.Fail("BAD_SHIP_ID", "操舵命令缺少舰船 ID");
            if (float.IsNaN(cmd.Thrust) || cmd.Thrust < MinThrust - 1e-4f || cmd.Thrust > MaxThrust + 1e-4f)
                return CommandResult.Fail("BAD_THRUST", "推力必须在 [-0.5, 1]");
            if (!InUnit(cmd.Yaw) || !InUnit(cmd.Pitch) || !InUnit(cmd.Roll))
                return CommandResult.Fail("BAD_ROTATION", "三轴输入必须在 [-1, 1]");
            return CommandResult.Ok();
        }

        private static bool InUnit(float v) => !float.IsNaN(v) && v >= -1.0001f && v <= 1.0001f;
    }

    /// <summary>
    /// 仿真与表现层共用的战斗数学。提前量公式放这里，HUD 的指示点才能和弹道一致。
    /// </summary>
    public static class CombatMath
    {
        /// <summary>
        /// 解算提前量：求炮弹该瞄准的世界坐标点。
        ///
        /// 关键点：炮弹世界速度 = 舰速 + 炮口方向 × 弹速，所以**必须用相对速度**算，
        /// 不能用目标的绝对速度——用绝对速度会漏掉自己这 30 km/s，150 km 上偏几度、几十公里，
        /// 结果就是全场打不中。
        ///
        /// 解 |rel + relVel·t| = 弹速·t 的二次方程（rel = 目标-本舰，relVel = 目标速度-本舰速度），
        /// 取最小正根；无正根表示目标跑得比炮弹快，追不上。
        /// </summary>
        public static bool TryLeadPoint(Vec3 shooterPos, Vec3 shooterVel,
            Vec3 targetPos, Vec3 targetVel, float shellSpeedKps, out Vec3 aimPoint)
        {
            aimPoint = targetPos;
            if (shellSpeedKps <= 0f) return false;

            Vec3 rel = targetPos - shooterPos;
            Vec3 relVel = targetVel - shooterVel;

            float a = Vec3.Dot(relVel, relVel) - shellSpeedKps * shellSpeedKps;
            float b = 2f * Vec3.Dot(rel, relVel);
            float c = Vec3.Dot(rel, rel);

            float t;
            if (Math.Abs(a) < 1e-4f)
            {
                if (Math.Abs(b) < 1e-6f) return false;
                t = -c / b;
                if (t <= 0f) return false;
            }
            else
            {
                float disc = b * b - 4f * a * c;
                if (disc < 0f) return false;
                float sqrt = (float)Math.Sqrt(disc);
                float t1 = (-b - sqrt) / (2f * a);
                float t2 = (-b + sqrt) / (2f * a);
                t = float.MaxValue;
                if (t1 > 0f && t1 < t) t = t1;
                if (t2 > 0f && t2 < t) t = t2;
                if (t == float.MaxValue) return false;
            }

            // 时间上限：超过 60 s 说明几乎追不上，按不可解处理，避免 AI 打一场空。
            if (t > 60f) return false;

            aimPoint = shooterPos + rel + relVel * t;
            return true;
        }

        /// <summary>把方向钳到射界锥内。返回 false 表示原始方向已出界（调用方可据此提示）。</summary>
        public static bool ClampToArc(Vec3 forward, Vec3 desiredDir, float halfAngleDeg, out Vec3 clampedDir)
        {
            Vec3 f = forward.Normalized;
            Vec3 d = desiredDir.Normalized;
            if (f.SqrLength < 1e-6f || d.SqrLength < 1e-6f) { clampedDir = f; return false; }

            float cosAngle = Vec3.Dot(f, d);
            if (cosAngle < -1f) cosAngle = -1f;
            if (cosAngle > 1f) cosAngle = 1f;
            float angle = (float)Math.Acos(cosAngle) * 57.29578f;
            if (angle <= halfAngleDeg) { clampedDir = d; return true; }

            // 斜边：把方向投影到锥面上（forward 与 desired 张成的平面内旋转 halfAngle）。
            Vec3 axis = Vec3.Cross(f, d);
            if (axis.SqrLength < 1e-8f)
            {
                // 正后方：任选一个垂直轴，避免叉积退化。
                axis = Vec3.Cross(f, Vec3.Up);
                if (axis.SqrLength < 1e-8f) axis = Vec3.Cross(f, new Vec3(1f, 0f, 0f));
            }
            axis = axis.Normalized;

            float rad = halfAngleDeg / 57.29578f;
            float cos = (float)Math.Cos(rad);
            float sin = (float)Math.Sin(rad);
            clampedDir = (f * cos + Vec3.Cross(axis, f) * sin).Normalized;
            return false;
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
