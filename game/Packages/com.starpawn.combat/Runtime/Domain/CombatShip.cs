// 单舰运行时状态。存绝对点数（血量/护盾），不存 0..1 百分比：
// 伤害单位是点，每帧反算百分比会累积误差；只在导出快照时做一次除法。
// Stats 是只读引用，不复制、不改写——同一份属性对象会被多艘同型舰共用。

using System.Collections.Generic;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain
{
    /// <summary>过载滑动窗口的一笔记录：时刻 + 这一击护盾实际吸收掉的伤害。</summary>
    public struct OverloadLoad
    {
        public float Time;
        public float Absorbed;
    }

    /// <summary>
    /// 单座炮在本 tick 解出的射击状态。WeaponSystem 每 tick 刷新一次，
    /// 开火判定、就位计数、快照三处读的都是这一份——**只解一次**，就不会出现
    /// 「HUD 说这门能打、按开火却没出弹」这种自相矛盾。
    /// </summary>
    public struct TurretAim
    {
        /// <summary>是否解出了有效射击解（射程内 + 有锁定或瞄准点）。</summary>
        public bool HasAim;
        /// <summary>瞄准方向，**世界坐标**单位向量。无解时是沿舰首的待机朝向。</summary>
        public Vec3 Dir;
        /// <summary>瞄准距离 km。无解时为 0。</summary>
        public float DistKm;
        /// <summary>是否落在本座射界内（已含俯仰限位）。无解时恒为 false。</summary>
        public bool InArc;
    }

    /// <summary>
    /// 能量三路分配。**每舰一份**：不同舰可以各自处在导航/交战档，不存在「全队共用一套」。
    /// 原作里靠一句口令切换，所以这里仍是整体预设，不是实时滑条。
    /// </summary>
    public struct PowerAllocation
    {
        public float Propulsion;
        public float Shield;
        public float Weapons;
        public PowerPreset Preset;

        public static PowerAllocation Balanced => ForPreset(PowerPreset.Balanced);

        /// <summary>三档预设。和必须为 1，由 BattleRules.ValidatePower 把关。</summary>
        public static PowerAllocation ForPreset(PowerPreset preset)
        {
            switch (preset)
            {
                case PowerPreset.Navigation:
                    return new PowerAllocation { Propulsion = 0.5f, Shield = 0.2f, Weapons = 0.3f, Preset = preset };
                case PowerPreset.Engagement:
                    return new PowerAllocation { Propulsion = 0.2f, Shield = 0.3f, Weapons = 0.5f, Preset = preset };
                case PowerPreset.Custom:
                case PowerPreset.Balanced:
                default:
                    return new PowerAllocation { Propulsion = 0.34f, Shield = 0.33f, Weapons = 0.33f, Preset = preset };
            }
        }
    }

    public sealed class CombatShip
    {
        // 身份
        public EntityId Id;
        public ContentId DefinitionId;
        public bool IsAlly;
        public SquadRole Role;
        public int SquadIndex;

        /// <summary>只读属性引用，来自配置解析，不在这里改。</summary>
        public ResolvedShipStats Stats;

        // 位置与姿态
        public Vec3 Pos;
        /// <summary>上一 tick 位置，供扫掠碰撞与被弹判定用。</summary>
        public Vec3 PrevPos;
        public Quat Attitude = Quat.Identity;

        // 平移：真空无阻力，速度矢量独立于朝向
        public Vec3 Velocity;
        /// <summary>主推力归一化输入：档位值，-0.5 反推，1 全速。</summary>
        public float ThrustSetpoint;

        // 转动：本体轴角速度（x=俯仰 y=偏航 z=滚转，度/秒）与归一化输入
        public Vec3 AngularVelocity;
        public Vec3 AngularInput;
        /// <summary>飞控开关。关掉后退化为纯牛顿：角速度不再被反喷衰减。</summary>
        public bool FlightAssist = true;

        // 生存
        public float Hull;

        // 护盾：三面 DR 是常驻几何状态（不消耗资源），会掉、会回的是过载缓冲池。
        // 缓冲池只在短窗口内吸收量超阈值时被扣，所以它不会退化成第二条血条。
        public float ShieldBuffer;
        /// <summary>破盾失效中：DR = 0、缓冲池锁定 0、回充与窗口累计全部暂停。</summary>
        public bool ShieldBroken;
        /// <summary>失效结束时刻（秒，绝对仿真时间）。到时只恢复 RecoverFraction，不是回满。</summary>
        public float ShieldBrokenUntil;
        /// <summary>最近一次被命中的仿真时间，用于「4 秒未受击才回充」。初值给负，开战即可回充。</summary>
        public float LastHitTime = -999f;
        /// <summary>本窗口已经扣过的超额。不记它，同一笔超额会被重复扣，缓冲池两倍速漏。</summary>
        public float OverloadDeducted;
        /// <summary>最近 WindowSeconds 内的 {时刻, 吸收量} 记录，按插入序，超出窗口在 ShieldSystem 里剪掉。</summary>
        public readonly List<OverloadLoad> OverloadWindow = new List<OverloadLoad>();

        // 能源：每舰独立，**全舰记账**——护航舰与敌舰一样扣电、一样会熄火。
        // 曾经有过「只算主舰」的简化（SimulatesEnergy 开关），已按 T06 删除：
        // 敌舰无限电会让「玩家被压制」的结论带假象，也会让饱和战术的收益算错。
        public PowerAllocation Power = PowerAllocation.Balanced;
        /// <summary>当前储能点数（0..Stats.Power.Capacity），不是百分比。</summary>
        public float Energy;
        /// <summary>能源见底导致推进被锁：推力档位已被强制归零，回充到 RestartFraction 才解锁。</summary>
        public bool PropulsionLocked;

        // 锁定
        public EntityId LockedTargetId;
        public bool HasLock => !LockedTargetId.IsEmpty;

        // 武器：装填是**每座炮**独立的，所以这里是一个和 Stats.Turrets 等长的数组（0 = 已装填）。
        // 一门炮打出去只让那一门进 CD，其余照常待发。
        public float[] TurretReload;
        /// <summary>逐座的瞄准结果，与 TurretReload 等长，由 WeaponSystem 每 tick 刷新。</summary>
        public TurretAim[] TurretAims;
        /// <summary>
        /// 本 tick 的装填时间倍率（交战档最短、航行档最长）。装填进度的分母要乘它，
        /// 否则导航档下进度条会先走满、还要再等一截才真能打。
        /// </summary>
        public float ReloadScale = 1f;
        /// <summary>本 tick 已装填完的炮座数（HUD 的「X/Y 就位」左边那个 X）。</summary>
        public int TurretsLoaded;
        /// <summary>既装填完、目标又落在射界内的炮座数。这才是「现在按开火能打出去几发」。</summary>
        public int TurretsInArc;
        public Vec3 AimPoint;
        public bool HasAim;
        /// <summary>本 tick 是否请求开火，由 ApplyCommands 置位，WeaponSystem 消费后清掉。</summary>
        public bool FireRequested;
        /// <summary>
        /// 上一 tick 的「按了开火但一门都对不上」是否已经报过事件。
        /// 不解这个：AI 每 tick 都会重提开火，事件流会被同一句 OutOfArc 刷屏。
        /// </summary>
        public bool FireRejected;

        public bool IsAlive => Hull > 0f;
        public float RadiusKm => Stats != null ? Stats.HullRadiusKm : 0f;

        public Vec3 Forward => Attitude.Forward;
        public float Health01 => Stats != null && Stats.MaxHull > 0f ? Hull / Stats.MaxHull : 0f;
        /// <summary>过载缓冲池余量。0 且未破盾说明刚从零回充；破盾期间恒为 0。</summary>
        public float Buffer01 => Stats != null && Stats.Overload.BufferCapacity > 0f
            ? ShieldBuffer / Stats.Overload.BufferCapacity : 0f;
        public float Energy01 => Stats != null && Stats.Power.Capacity > 0f ? Energy / Stats.Power.Capacity : 0f;
        public float EnergyMax => Stats != null ? Stats.Power.Capacity : 0f;
        /// <summary>
        /// 装填进度 0..1，口径是「最快的那门炮」——HUD 只有一条装填条，
        /// 让它显示「下一发什么时候能出去」比显示平均进度有用。
        /// </summary>
        public float Reload01
        {
            get
            {
                var ts = TurretReload;
                if (Stats == null || ts == null || ts.Length == 0) return 1f;
                var defs = Stats.Turrets;
                float best = 0f;
                for (int i = 0; i < ts.Length && i < defs.Length; i++)
                {
                    float denom = defs[i].ReloadSeconds * ReloadScale;
                    float p = denom > 1e-6f ? 1f - ts[i] / denom : 1f;
                    if (p > best) best = p;
                }
                return best < 0f ? 0f : (best > 1f ? 1f : best);
            }
        }

        /// <summary>
        /// 按当前属性重建炮座状态：开局全部装填完毕（0 = 可立刻开火）。
        /// 瞄准数组一起建，长度与装填数组对齐——两者少一个都会让 WeaponSystem 每 tick 重建。
        /// </summary>
        public void ResetTurrets()
        {
            int n = Stats != null && Stats.Turrets != null ? Stats.Turrets.Length : 0;
            TurretReload = new float[n];
            TurretAims = new TurretAim[n];
            ReloadScale = 1f;
            TurretsLoaded = n;
            TurretsInArc = 0;
        }

        /// <summary>结算后的完整度必须保留小数，不能把「擦伤」写成满血或击沉。</summary>
        public float HullAfter01
        {
            get
            {
                if (Hull <= 0f) return 0f;
                return Health01;
            }
        }
    }
}
