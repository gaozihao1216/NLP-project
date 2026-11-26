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
        /// <summary>每次开火消耗的点数。</summary>
        public readonly float WeaponDrainPerShot;
        /// <summary>
        /// 推进重启阈值（占容量的比例）。能源耗尽后推进被强制归零，
        /// 必须回充到这个比例以上才重新可用——滞回是为了避免「一有电就又熄火」的抖动。
        /// </summary>
        public readonly float RestartFraction;

        public PowerProfile(float capacity, float regenPerSec, float propulsionDrainPerSec,
            float turnDrainPerSec, float weaponDrainPerShot, float restartFraction)
        {
            Capacity = capacity > 1f ? capacity : 1f;
            RegenPerSec = regenPerSec > 0f ? regenPerSec : 0f;
            PropulsionDrainPerSec = propulsionDrainPerSec > 0f ? propulsionDrainPerSec : 0f;
            TurnDrainPerSec = turnDrainPerSec > 0f ? turnDrainPerSec : 0f;
            WeaponDrainPerShot = weaponDrainPerShot > 0f ? weaponDrainPerShot : 0f;
            RestartFraction = restartFraction > 0f && restartFraction < 1f ? restartFraction : 0.2f;
        }

        /// <summary>缺省值：只在配置缺失时使用（假实现、单元测试、旧调用点）。</summary>
        public static PowerProfile Default =>
            new PowerProfile(120f, 3.4f, 2.2f, 0.8f, 5f, 0.2f);
    }

    /// <summary>
    /// 伤害通道。**只决定防御侧走哪条结算分支**（判面 → 护盾 DR → 过载 → 装甲 DR → 结构），
    /// **不决定投送方式**：有没有飞行时间、要不要算提前量，由武器实现决定，不由这个枚举决定。
    ///
    /// 当前运行时只有一种投送实现——实体投射体（WeaponSystem 建 Projectile，真实飞行）。
    /// 所以配置层与编成校验都只接受 Kinetic：把炮座配成 Beam 会得到一颗「有飞行时间、
    /// 却按光束装甲减伤结算的炮弹」，画面和规则两头都不对。光束投送（瞬时命中、无飞行时间）
    /// 排在 Phase 1.5，届时再放开这一层校验，而不是现在让它静默降级。
    /// </summary>
    public enum DamageChannel
    {
        /// <summary>光束结算分支：按装甲 BeamDr 减伤。**投送实现尚未落地**，配置层会拒绝。</summary>
        Beam = 0,
        /// <summary>投射体结算分支：按装甲 KineticDr 减伤。当前唯一可配置的通道。</summary>
        Kinetic = 1,
    }

    /// <summary>受击面。绑定舰体的方向盾只有这三档，没有独立朝向角。</summary>
    public enum ShieldFace { Front = 0, Side = 1, Rear = 2 }

    /// <summary>
    /// 装甲：按通道给固定减伤。`DamageReduction` 一律表示「被挡掉的比例」，
    /// 实际伤害 = 伤害 × (1 − DR)。不要把它读成「承受比例」。
    /// </summary>
    public readonly struct ArmorProfile
    {
        public readonly float BeamDr;
        public readonly float KineticDr;

        public ArmorProfile(float beamDr, float kineticDr)
        {
            BeamDr = Clamp01(beamDr);
            KineticDr = Clamp01(kineticDr);
        }

        public float DrFor(DamageChannel channel)
            => channel == DamageChannel.Beam ? BeamDr : KineticDr;

        /// <summary>缺省：光束穿薄甲、投射体吃厚甲，与护盾的强弱互补。</summary>
        public static ArmorProfile Default => new ArmorProfile(0.20f, 0.35f);

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }

    /// <summary>
    /// 护盾几何与成本。盾面强化方向恒为舰体局部 +Z（绑定舰体，见设计稿 6.1），
    /// 因此这里没有朝向角，只有三档 DR、转向损耗、回充与「每点吸收要付多少电」。
    /// </summary>
    public readonly struct ShieldProfile
    {
        /// <summary>正/侧/背三档减伤。θ ≤45° 正面、45–135° 侧面、>135° 背面。</summary>
        public readonly float FrontDr;
        public readonly float SideDr;
        public readonly float RearDr;
        /// <summary>满转时的 DR 衰减比例（0.25 = 掉四分之一）。可被 CombatWorld.TurnLossEnabled 整体关掉。</summary>
        public readonly float TurnLossFactor;
        /// <summary>受击后多久开始回充缓冲池（秒）。</summary>
        public readonly float RegenDelaySeconds;
        /// <summary>缓冲池回充速率（点/秒），按护盾能量份额与供电状态折算。</summary>
        public readonly float RegenPerSec;
        /// <summary>每点被挡掉的伤害要付多少电。付不起时 DR 按可用电比例变薄，不是免费扛。</summary>
        public readonly float DrainPerDamage;
        /// <summary>破盾失效结束后的缓冲池恢复比例（0.3 = 只回三成）。</summary>
        public readonly float RecoverFraction;

        public ShieldProfile(float frontDr, float sideDr, float rearDr,
            float turnLossFactor, float regenDelaySeconds, float regenPerSec,
            float drainPerDamage, float recoverFraction)
        {
            FrontDr = Clamp01(frontDr);
            SideDr = Clamp01(sideDr);
            RearDr = Clamp01(rearDr);
            TurnLossFactor = Clamp01(turnLossFactor);
            RegenDelaySeconds = regenDelaySeconds > 0f ? regenDelaySeconds : 0f;
            RegenPerSec = regenPerSec > 0f ? regenPerSec : 0f;
            DrainPerDamage = drainPerDamage > 0f ? drainPerDamage : 0f;
            RecoverFraction = recoverFraction > 0f && recoverFraction <= 1f ? recoverFraction : 0.3f;
        }

        public float FaceDr(ShieldFace face)
        {
            switch (face)
            {
                case ShieldFace.Front: return FrontDr;
                case ShieldFace.Side: return SideDr;
                default: return RearDr;
            }
        }

        /// <summary>缺省：三档 DR 与成本走设计稿基线，只把回充速率交给调用方。</summary>
        public static ShieldProfile DefaultFor(float regenPerSec)
            => new ShieldProfile(0.85f, 0.55f, 0.15f, 0.25f, 4f, regenPerSec, 0.12f, 0.3f);

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }

    /// <summary>
    /// 过载：短窗口内吸收量超阈值才扣缓冲池，池空则护盾失效。
    /// 阈值与池容量都是舰体参数——小舰的池小、阈值也小，否则一碰就碎。
    /// </summary>
    public readonly struct OverloadProfile
    {
        /// <summary>窗口内累计吸收量的阈值。低于它不消耗缓冲池。</summary>
        public readonly float Threshold;
        /// <summary>滑动窗口长度（秒）。</summary>
        public readonly float WindowSeconds;
        /// <summary>缓冲池容量。过载时只扣「超出阈值的增量」。</summary>
        public readonly float BufferCapacity;
        /// <summary>破盾后的失效时长（秒），期间 DR = 0 且一切回充暂停。</summary>
        public readonly float BreakSeconds;

        public OverloadProfile(float threshold, float windowSeconds, float bufferCapacity, float breakSeconds)
        {
            Threshold = threshold > 0f ? threshold : 1f;
            WindowSeconds = windowSeconds > 0f ? windowSeconds : 1.5f;
            BufferCapacity = bufferCapacity > 0f ? bufferCapacity : 1f;
            BreakSeconds = breakSeconds > 0f ? breakSeconds : 6f;
        }

        /// <summary>
        /// 缺省：阈值按缓冲池的 0.4 倍给（巡洋舰 300 → 120，与设计稿基线一致）。
        /// 小舰不按同一绝对阈值，否则 80 点的池会被两发齐射直接打穿。
        /// </summary>
        public static OverloadProfile DefaultFor(float bufferCapacity)
            => new OverloadProfile(bufferCapacity * 0.4f, 1.5f, bufferCapacity, 6f);
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
    /// <summary>
    /// 一个炮座。装填、装药、射界**每座独立**：四门炮不会因为一门开火就一起等 CD。
    ///
    /// 不建模炮塔转向速度——这是有意的。数十门炮各带转速会让「能不能打」取决于一个玩家读不出来的
    /// 中间量；本作只判断两件事：**装填好了没**、**目标在不在射界里**。转向交给舰体。
    ///
    /// 朝向用「中心角 + 半张角」而不是 min/max，是为了避开 180° 处的回绕：
    /// 舰尾炮写 center=180 / half=140 就是 40..320，不需要任何跨零特判。
    /// </summary>
    public sealed class TurretProfile
    {
        /// <summary>"bow.l" / "stern" 之类，只用于事件与调试，不参与运算。</summary>
        public readonly string Id;
        /// <summary>炮座在舰体坐标里的位置（km）：x 右、y 上、z 舰首。</summary>
        public readonly float MountRightKm;
        public readonly float MountUpKm;
        public readonly float MountForwardKm;
        /// <summary>射界方位中心，相对舰首，右为正。</summary>
        public readonly float YawCenterDeg;
        /// <summary>射界半张角 0..180。140 表示「左右各 140°，打不到自己舰体」。</summary>
        public readonly float YawHalfArcDeg;
        /// <summary>俯仰上下限（度），抬头为正。</summary>
        public readonly float PitchMinDeg;
        public readonly float PitchMaxDeg;
        public readonly float ReloadSeconds;
        public readonly float Damage;
        public readonly float ShellVelocityKps;
        public readonly float MaxRangeKm;
        /// <summary>
        /// 结算通道。见 DamageChannel 的说明：它只挑防御分支，不挑投送方式。
        /// 当前配置层只接受 Kinetic——投射体是唯一已实现的投送，Beam 会被解析器挡下。
        /// </summary>
        public readonly DamageChannel Channel;

        public TurretProfile(string id,
            float mountRightKm, float mountUpKm, float mountForwardKm,
            float yawCenterDeg, float yawHalfArcDeg, float pitchMinDeg, float pitchMaxDeg,
            float reloadSeconds, float damage, float shellVelocityKps, float maxRangeKm,
            DamageChannel channel)
        {
            Id = string.IsNullOrEmpty(id) ? "gun" : id;
            MountRightKm = mountRightKm;
            MountUpKm = mountUpKm;
            MountForwardKm = mountForwardKm;
            YawCenterDeg = yawCenterDeg;
            YawHalfArcDeg = yawHalfArcDeg < 0f ? 0f : (yawHalfArcDeg > 180f ? 180f : yawHalfArcDeg);
            PitchMinDeg = pitchMinDeg;
            PitchMaxDeg = pitchMaxDeg < pitchMinDeg ? pitchMinDeg : pitchMaxDeg;
            ReloadSeconds = reloadSeconds > 0f ? reloadSeconds : 0.01f;
            Damage = damage < 0f ? 0f : damage;
            ShellVelocityKps = shellVelocityKps > 0f ? shellVelocityKps : 0.01f;
            MaxRangeKm = maxRangeKm > 0f ? maxRangeKm : 0.01f;
            Channel = channel;
        }

        /// <summary>炮座位置（舰体坐标）。</summary>
        public Vec3 MountLocal => new Vec3(MountRightKm, MountUpKm, MountForwardKm);
    }

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
        public readonly float Accuracy01;

        /// <summary>碰撞半径 km。舰体尺寸参数，不由血量推导（血量只是缺省值来源）。</summary>
        public readonly float HullRadiusKm;
        /// <summary>机动参数：推力加速度、三轴转速上限与角加速度。</summary>
        public readonly ManeuverProfile Maneuver;
        /// <summary>散布角上限（度）。精度 1 时无散布，精度 0 时达到该上限。</summary>
        public readonly float MaxSpreadDeg;
        /// <summary>能源参数：容量、再生与各路耗电率。</summary>
        public readonly PowerProfile Power;
        /// <summary>装甲减伤，按通道给。</summary>
        public readonly ArmorProfile Armor;
        /// <summary>护盾几何、转向损耗与抗伤耗电。</summary>
        public readonly ShieldProfile Shield;
        /// <summary>过载缓冲池与破盾失效。</summary>
        public readonly OverloadProfile Overload;

        /// <summary>
        /// 炮座表，**武器参数的唯一真源**：射程/装药/装填/弹速都按座给。
        /// 上面的 GunDamage / GunRangeKm / ShellVelocityKps / ReloadSeconds 只是对外汇总口径
        /// （最大射程、最大单发、最快装填），给 AI 站位、HUD 与旧调用点用，不参与结算。
        /// </summary>
        public readonly TurretProfile[] Turrets;

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

        /// <summary>完整构造：机动与半径显式给出，能源/护盾/装甲走缺省（测试与旧调用点用）。</summary>
        public ResolvedShipStats(float maxHull, float maxSpeedKps, float turnRateDps,
            float gunDamage, float gunRangeKm, float shellVelocityKps, float reloadSeconds,
            float shieldCapacity, float shieldRegenPerSec, float accuracy01,
            float hullRadiusKm, ManeuverProfile maneuver, float maxSpreadDeg)
            : this(maxHull, maxSpeedKps, turnRateDps, gunDamage, gunRangeKm, shellVelocityKps,
                   reloadSeconds, shieldCapacity, shieldRegenPerSec, accuracy01,
                   hullRadiusKm, maneuver, maxSpreadDeg, PowerProfile.Default)
        {
        }

        /// <summary>全参构造：连能源一起给出，护盾/装甲/过载走缺省。</summary>
        public ResolvedShipStats(float maxHull, float maxSpeedKps, float turnRateDps,
            float gunDamage, float gunRangeKm, float shellVelocityKps, float reloadSeconds,
            float shieldCapacity, float shieldRegenPerSec, float accuracy01,
            float hullRadiusKm, ManeuverProfile maneuver, float maxSpreadDeg, PowerProfile power)
            : this(maxHull, maxSpeedKps, turnRateDps, gunDamage, gunRangeKm, shellVelocityKps,
                   reloadSeconds, shieldCapacity, shieldRegenPerSec, accuracy01,
                   hullRadiusKm, maneuver, maxSpreadDeg, power,
                   ArmorProfile.Default,
                   ShieldProfile.DefaultFor(shieldRegenPerSec),
                   OverloadProfile.DefaultFor(shieldCapacity))
        {
        }

        /// <summary>真实配置走这条：装甲、护盾、过载全部显式给出，战斗代码不写任何倍率。</summary>
        public ResolvedShipStats(float maxHull, float maxSpeedKps, float turnRateDps,
            float gunDamage, float gunRangeKm, float shellVelocityKps, float reloadSeconds,
            float shieldCapacity, float shieldRegenPerSec, float accuracy01,
            float hullRadiusKm, ManeuverProfile maneuver, float maxSpreadDeg, PowerProfile power,
            ArmorProfile armor, ShieldProfile shield, OverloadProfile overload,
            TurretProfile[] turrets = null)
        {
            MaxHull = maxHull; MaxSpeedKps = maxSpeedKps; TurnRateDps = turnRateDps;
            GunDamage = gunDamage; GunRangeKm = gunRangeKm; ShellVelocityKps = shellVelocityKps;
            ReloadSeconds = reloadSeconds; Accuracy01 = accuracy01;
            HullRadiusKm = hullRadiusKm; Maneuver = maneuver;
            MaxSpreadDeg = maxSpreadDeg > 0f ? maxSpreadDeg : DefaultMaxSpreadDeg;
            Power = power; Armor = armor; Shield = shield; Overload = overload;

            // 没给炮座表 = 旧的单炮舰：一门全向炮，参数取舰体标量。行为与炮塔化之前一致，
            // 差别只在于旧代码把出界方向钳到 ±15° 锥边再开火，现在做的是「射界外就不打」。
            Turrets = turrets != null && turrets.Length > 0
                ? turrets
                : new[]
                {
                    new TurretProfile("gun", 0f, 0f, 0f, 0f, 180f, -90f, 90f,
                        reloadSeconds, gunDamage, shellVelocityKps, gunRangeKm, DamageChannel.Kinetic)
                };
        }

        /// <summary>缺省半径：0.04 × cbrt(MaxHull) → 120/300/800 血对应 0.20/0.27/0.37 km。</summary>
        public static float HullRadiusKmFromHull(float maxHull)
            => 0.04f * (float)Math.Pow(Math.Max(1f, maxHull), 1.0 / 3.0);

        /// <summary>测试与假实现用的默认属性。</summary>
        public static ResolvedShipStats Default => new ResolvedShipStats(
            100f, 20f, 30f, 10f, 300f, 3f, 3f, 60f, 2f, 0.9f);
    }

    /// <summary>
    /// 按定义 ID 取已解析的战斗属性。应用层只认这个接口，
    /// 具体是读 content/ships 的 JSON、还是内存表，由 Bootstrap 装配时决定。
    /// </summary>
    public interface IShipStatsSource
    {
        bool TryGet(string definitionId, out ResolvedShipStats stats);
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
        /// <summary>炮座总数。HUD 的「X/Y 就位」右边那个 Y。</summary>
        public int TurretCount;
        /// <summary>已装填完的炮座数。</summary>
        public int TurretsLoaded;
        /// <summary>
        /// 具备射击条件的炮座数（装填完 + 目标在射界内）。能源不足时实际出弹会更少，
        /// 这个数是"有几门有资格"，不是"这一轮会打出几发"。
        /// </summary>
        public int TurretsInArc;
        /// <summary>过载缓冲池余量 0..1。护盾的「厚度」是三面 DR，不是这条进度条。</summary>
        public float Buffer01;
        /// <summary>护盾是否处于破盾失效（DR = 0、缓冲池锁定 0），失效期结束会自动恢复三成。</summary>
        public bool ShieldBroken;
        /// <summary>三面当前有效 DR，已含转向损耗。HUD 据此决定扇面亮度。</summary>
        public float FaceDrFront;
        public float FaceDrSide;
        public float FaceDrRear;
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
        /// <summary>
        /// 逐座炮的明细状态。上面 TurretCount/TurretsLoaded/TurretsInArc 是这三个数字的汇总，
        /// 表现层要转视觉炮塔、画逐座装填环、解释「为什么这一门打不出去」时读这里，
        /// 不要去解析 BattleEvent.Detail 的文案。
        /// 与开火判定读的是同一份数据，所以「快照说能打」与「按开火真的出弹」不会打架。
        /// </summary>
        public TurretSnapshot[] Turrets;
    }

    /// <summary>
    /// 单座炮的运行时状态。为表现层准备，规则层不读它。
    ///
    /// 方向一律给**舰体坐标**：视觉炮塔是舰体子物体，拿到就能直接设 localRotation，
    /// 舰体姿态转头时炮塔自动跟着转，不需要表现层每帧做世界方向反解。
    /// </summary>
    public sealed class TurretSnapshot
    {
        /// <summary>炮座 ID（来自配置，如 "bow.l"）。表现层按它匹配视觉炮塔。</summary>
        public string TurretId;
        /// <summary>炮座安装点（舰体坐标 km）：x 右、y 上、z 舰首。</summary>
        public Vec3 MountLocal;
        /// <summary>
        /// 装填进度 0..1，1 = 装填完毕。分母含本 tick 的能源档位系数——
        /// 交战档装填更快，条走完的时刻和真正能打的时刻是同一个。
        /// </summary>
        public float Reload01;
        /// <summary>装填完毕（等价于 Reload01 到 1，但免去浮点比较）。</summary>
        public bool Loaded;
        /// <summary>本座是否解出了有效射击解（射程内 + 有目标或瞄准点）。false 时 AimLocalDir 是待机朝向。</summary>
        public bool HasAim;
        /// <summary>本座的瞄准方向（**舰体坐标**单位向量）。装填中也有值，视觉炮塔照它转。</summary>
        public Vec3 AimLocalDir;
        /// <summary>瞄准距离 km。HasAim 为 false 时是 0。</summary>
        public float AimDistanceKm;
        /// <summary>是否落在本座射界内（已含方位角与俯仰限位）。</summary>
        public bool InArc;
        /// <summary>
        /// 具备射击条件：装填完 + 有射击解 + 在界内。**不含能源**——多门炮共享一套储能，
        /// 轮到这一门时可能已经扣不出电了。所以 CanFire 只说明"现在按开火这一门**有资格**出弹"，
        /// 不说明它出了弹；是否真开火一律看 Fire 事件。详见 docs/combat-open-items.md 第 1 节。
        /// </summary>
        public bool CanFire;
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
        // 旧的全舰统一射界（±15°）已删除：射界现在是**每个炮座**自己的属性
        // （TurretProfile.YawCenterDeg / YawHalfArcDeg），舰首炮与舰尾炮本来就该不一样。
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

            // 投送方式目前只有投射体一种。含 Beam 炮座的编成一律不许入战：
            // 配置层已经拒绝了这种写法，这里再挡一道手搭属性对象（测试、假实现、存档迁移）。
            if (stats.Turrets != null)
            {
                for (int i = 0; i < stats.Turrets.Length; i++)
                {
                    var t = stats.Turrets[i];
                    if (t.Channel != DamageChannel.Kinetic)
                        return CommandResult.Fail("UNSUPPORTED_CHANNEL",
                            "炮座 " + t.Id + " 使用 " + t.Channel +
                            " 通道：光束投送尚未实现，当前只支持 kinetic");
                }
            }
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
        /// <summary>
        /// 给人看的一句话（副官提示、调试日志直接用）。**表现层不要解析它**——
        /// 文案随时会改，需要机器可读的数据时读结构化字段。
        /// </summary>
        public string Detail;
        /// <summary>
        /// Fire 事件的结构化载荷：哪座炮、打出哪颗弹、从哪出膛、朝哪飞。
        /// 表现层靠它把炮口火焰与具体弹丸直接对上，不需要解析 Detail、也不需要自己重算提前量。
        /// 其他事件为 null。
        /// </summary>
        public TurretShot Shot;
    }

    /// <summary>
    /// 一次炮座开火的事实。
    ///
    /// 注意 Direction 是**炮口朝向**，不是弹丸的世界运动方向：当前弹丸初速 = 舰速 + Direction × 弹速，
    /// 所以舰体横移时两者会差出几度到十几度。要画长曳光或推弹道，请跟快照里弹丸的实际位置，
    /// 不要拿 Direction × 弹速 × t 外推。这条口径是否改成"世界初速方向"待决，
    /// 见 docs/combat-open-items.md 第 3 节。
    /// </summary>
    public sealed class TurretShot
    {
        /// <summary>开火炮座 ID，与 ShipSnapshot.Turrets[i].TurretId 对得上。</summary>
        public string TurretId;
        /// <summary>打出的那颗弹丸 ID，与同一帧快照里 Projectiles 的 ProjectileId 对得上。</summary>
        public EntityId ProjectileId;
        /// <summary>出膛点（世界坐标 km）。</summary>
        public Vec3 Origin;
        /// <summary>
        /// 炮口朝向（世界坐标单位向量，**已含散布**）。它只描述炮管指向与散布，不含舰速。
        /// 弹丸真正的世界初速度目前没有单独字段，表现层需要时读快照跟踪弹丸。
        /// </summary>
        public Vec3 Direction;
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
