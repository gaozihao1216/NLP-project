// 炮弹。留 PrevPos 是为了做扫掠碰撞：弹速 220 km/s × 1/30 s = 7.3 km/tick，
// 而舰体半径只有 0.2–0.37 km，点检测必然穿透。

using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain
{
    public sealed class Projectile
    {
        public EntityId Id;
        public EntityId OwnerId;
        public bool FromAlly;

        public Vec3 Pos;
        public Vec3 PrevPos;
        /// <summary>世界系速度 = 发射舰速度 + 炮口方向 × 弹速。追击更快、迎头更慢。</summary>
        public Vec3 Vel;

        public float Damage;
        public float MaxRangeKm;
        public float TravelledKm;
        public bool Alive = true;
    }
}
