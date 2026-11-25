// 弹道：逐 tick 积分，累计路程，超过炮的最大射程就销毁。
// 保留 PrevPos 给 DamageSystem 做扫掠——7.3 km/tick 的位移不可能靠点检测命中。

using StarPawn.Combat.Contracts;

namespace StarPawn.Combat.Domain.Systems
{
    public static class ProjectileSystem
    {
        public static void Step(CombatWorld w, float dt)
        {
            for (int i = 0; i < w.Shells.Count; i++)
            {
                var p = w.Shells[i];
                if (!p.Alive) continue;

                p.PrevPos = p.Pos;
                Vec3 delta = p.Vel * dt;
                p.Pos = p.Pos + delta;
                p.TravelledKm += delta.Length;

                if (p.TravelledKm > p.MaxRangeKm)
                    p.Alive = false;
            }
        }
    }
}
