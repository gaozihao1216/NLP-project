// 武器：装填、射程、射界锥、散布。炮弹真实飞行，不做瞬间命中。
// 散布角与装填时间都来自舰体配置，代码里没有「8 度」「3.2 秒」这类常数。

using System;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain.Systems
{
    public static class WeaponSystem
    {
        private const float Deg2Rad = 0.017453292f;

        public static void Step(CombatWorld w, float dt)
        {
            for (int i = 0; i < w.Ships.Count; i++)
            {
                var s = w.Ships[i];
                if (!s.IsAlive) continue;

                // 交战档把能量给武器：装填最快；航行档武器最慢。分配是每舰自己的。
                float reloadScale = 1.4f - 0.8f * Clamp01(s.Power.Weapons);

                if (s.ReloadTimer > 0f) s.ReloadTimer -= dt;

                if (!s.FireRequested) continue;
                s.FireRequested = false;

                if (s.ReloadTimer > 0f)
                {
                    w.Emit("NotReady", s.Id, default(EntityId), "装填未完成");
                    continue;
                }

                // 开火要从储能池抽一笔电。抽不出来就不打——能源见底时武器和推进一起哑火。
                if (!PowerSystem.TryConsumeShot(w, s))
                {
                    w.Emit("PowerLow", s.Id, default(EntityId), "能源不足，无法开火");
                    continue;
                }

                // 没给瞄准点时沿舰首方向打，射程按炮的最大射程算。
                Vec3 aim = s.HasAim
                    ? s.AimPoint
                    : s.Pos + s.Attitude.Forward * s.Stats.GunRangeKm;

                Vec3 toAim = aim - s.Pos;
                float dist = toAim.Length;
                if (dist < 1e-3f)
                {
                    w.Emit("OutOfRange", s.Id, default(EntityId), "瞄准点与舰体重合");
                    continue;
                }

                if (dist > s.Stats.GunRangeKm)
                {
                    w.Emit("OutOfRange", s.Id, default(EntityId),
                        "目标 " + dist.ToString("0") + " km，超出射程 " + s.Stats.GunRangeKm.ToString("0"));
                    continue;
                }

                // 固定炮有射界锥：出锥时钳到锥边，仍然开火，但事件里标记，HUD 可提示。
                Vec3 dir = toAim / dist;
                bool inArc = CombatMath.ClampToArc(
                    s.Attitude.Forward, dir, BattleRules.GunArcHalfAngleDeg, out Vec3 aimedDir);
                if (!inArc)
                    w.Emit("OutOfArc", s.Id, default(EntityId), "目标超出射界，已钳到锥边");

                dir = ApplySpread(w, s, aimedDir);

                // 炮弹世界速度 = 舰速 + 炮口方向 × 弹速。
                var shell = new Projectile
                {
                    Id = new EntityId(w.NextShellId()),
                    OwnerId = s.Id,
                    FromAlly = s.IsAlly,
                    Pos = s.Pos,
                    PrevPos = s.Pos,
                    Vel = s.Velocity + dir * s.Stats.ShellVelocityKps,
                    Damage = s.Stats.GunDamage,
                    Channel = DamageChannel.Kinetic,
                    MaxRangeKm = s.Stats.GunRangeKm,
                    Alive = true
                };
                w.Shells.Add(shell);

                s.ReloadTimer = s.Stats.ReloadSeconds * reloadScale;
                w.Emit("Fire", s.Id, default(EntityId), "射程 " + dist.ToString("0") + " km");
            }
        }

        /// <summary>
        /// 散布：精度 1 时零散布，精度 0 时达到 maxSpreadDeg。绕理想方向做锥形偏移，
        /// 每次开火固定消费两个随机数（方位角 + 偏角），保证同 seed 可复现。
        /// </summary>
        private static Vec3 ApplySpread(CombatWorld w, CombatShip s, Vec3 dir)
        {
            float maxSpread = (1f - s.Stats.Accuracy01) * s.Stats.MaxSpreadDeg;
            if (maxSpread <= 0f) return dir;

            float offsetDeg = (w.Rng.NextFloat() * 2f - 1f) * maxSpread;
            float azimuth = w.Rng.NextFloat() * 6.2831853f;

            Vec3 helper = Math.Abs(dir.Y) < 0.9f ? Vec3.Up : new Vec3(1f, 0f, 0f);
            Vec3 u = Vec3.Cross(dir, helper).Normalized;
            Vec3 v = Vec3.Cross(dir, u).Normalized;
            Vec3 perp = u * (float)Math.Cos(azimuth) + v * (float)Math.Sin(azimuth);

            float rad = offsetDeg * Deg2Rad;
            return (dir * (float)Math.Cos(rad) + perp * (float)Math.Sin(rad)).Normalized;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
