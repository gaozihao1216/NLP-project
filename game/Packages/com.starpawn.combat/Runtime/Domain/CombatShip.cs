// 单舰运行时状态。存绝对点数（血量/护盾），不存 0..1 百分比：
// 伤害单位是点，每帧反算百分比会累积误差；只在导出快照时做一次除法。
// Stats 是只读引用，不复制、不改写——同一份属性对象会被多艘同型舰共用。

using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain
{
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
        public float Shield;

        // 能源：每舰独立。主舰真正消耗，其它舰按「满电」处理（见 SimulatesEnergy）。
        public PowerAllocation Power = PowerAllocation.Balanced;
        /// <summary>当前储能点数（0..Stats.Power.Capacity），不是百分比。</summary>
        public float Energy;
        /// <summary>
        /// 是否参与能源消耗模拟。阶段一是「只算主舰」：玩家旗舰扣电，护航舰与敌舰视为反应堆余量充足。
        /// 放开成全队模拟只需把这个标记在 MakeShip 里改成 true。
        /// </summary>
        public bool SimulatesEnergy;
        /// <summary>能源见底导致推进被锁：推力档位已被强制归零，回充到 RestartFraction 才解锁。</summary>
        public bool PropulsionLocked;

        // 锁定
        public EntityId LockedTargetId;
        public bool HasLock => !LockedTargetId.IsEmpty;

        // 武器
        public float ReloadTimer;
        public Vec3 AimPoint;
        public bool HasAim;
        /// <summary>本 tick 是否请求开火，由 ApplyCommands 置位，WeaponSystem 消费后清掉。</summary>
        public bool FireRequested;

        public bool IsAlive => Hull > 0f;
        public float RadiusKm => Stats != null ? Stats.HullRadiusKm : 0f;

        public Vec3 Forward => Attitude.Forward;
        public float Health01 => Stats != null && Stats.MaxHull > 0f ? Hull / Stats.MaxHull : 0f;
        public float Shield01 => Stats != null && Stats.ShieldCapacity > 0f ? Shield / Stats.ShieldCapacity : 0f;
        public float Energy01 => Stats != null && Stats.Power.Capacity > 0f ? Energy / Stats.Power.Capacity : 0f;
        public float EnergyMax => Stats != null ? Stats.Power.Capacity : 0f;
        public float Reload01
        {
            get
            {
                if (Stats == null || Stats.ReloadSeconds <= 0f) return 1f;
                float v = 1f - ReloadTimer / Stats.ReloadSeconds;
                return v < 0f ? 0f : (v > 1f ? 1f : v);
            }
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
