// 单舰运行时状态。存绝对点数（血量/护盾），不存 0..1 百分比：
// 伤害单位是点，每帧反算百分比会累积误差；只在导出快照时做一次除法。
// Stats 是只读引用，不复制、不改写——同一份属性对象会被多艘同型舰共用。

using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain
{
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
