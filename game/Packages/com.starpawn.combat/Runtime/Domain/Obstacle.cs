// 有血量的静止掩体。无速度、无翻滚，血量按半径三次方缩放：大石头更耐打。

using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain
{
    public sealed class Obstacle
    {
        public EntityId Id;
        public Vec3 Pos;
        public float RadiusKm;
        /// <summary>当前血量（点）。</summary>
        public float Hull;
        /// <summary>初始血量，导出 0..1 时做分母。</summary>
        public float InitialHull;

        public bool IsAlive => Hull > 0f;

        /// <summary>血量基准：半径³ × 系数，保证大石头的「血/截面」比一致。</summary>
        public const float HullPerCubicKm = 40f;

        public static float HullForRadius(float radiusKm)
        {
            if (radiusKm <= 0f) return 1f;
            return HullPerCubicKm * radiusKm * radiusKm * radiusKm;
        }
    }
}
