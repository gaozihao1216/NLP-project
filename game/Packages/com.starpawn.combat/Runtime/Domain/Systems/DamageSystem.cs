// 命中判定：3D 线段 vs 球，用两体的相对位移做扫掠。
//
// 为什么必须扫掠：弹速 220 km/s × 1/30 s = 7.3 km/tick，舰体半径只有 0.2–0.37 km，
// 点-球检测 100% 穿透；舰速 32 km/s 也有 1.07 km/tick，同样会漏判。
// 用相对位移（炮弹与舰同 tick 都在动）比用炮弹绝对线段再放大半径更准。

using System;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain.Systems
{
    public static class DamageSystem
    {
        public static void Step(CombatWorld w, float dt)
        {
            // 这里只做「谁打中了谁」的几何判定。扣多少血、护盾挡几成、过载怎么算，
            // 一律交给 DamageResolver——命中判定与伤害结算分开，才能在不碰扫掠代码的前提下改防御。

            for (int i = 0; i < w.Shells.Count; i++)
            {
                var shell = w.Shells[i];
                if (!shell.Alive) continue;

                Obstacle hitRock = null;
                CombatShip hitShip = null;
                float bestT = float.MaxValue;

                // 先陨石：石头挡弹。
                for (int r = 0; r < w.Rocks.Count; r++)
                {
                    var rock = w.Rocks[r];
                    if (!rock.IsAlive) continue;
                    Vec3 a = shell.PrevPos - rock.Pos;
                    Vec3 b = shell.Pos - rock.Pos;
                    if (SegmentSphere(a, b, rock.RadiusKm, out float t) && t < bestT)
                    {
                        bestT = t;
                        hitRock = rock;
                        hitShip = null;
                    }
                }

                // 再舰船：只打敌对方。
                for (int s = 0; s < w.Ships.Count; s++)
                {
                    var ship = w.Ships[s];
                    if (!ship.IsAlive || ship.IsAlly == shell.FromAlly) continue;
                    Vec3 a = shell.PrevPos - ship.PrevPos;
                    Vec3 b = shell.Pos - ship.Pos;
                    if (SegmentSphere(a, b, ship.RadiusKm, out float t) && t < bestT)
                    {
                        bestT = t;
                        hitShip = ship;
                        hitRock = null;
                    }
                }

                if (hitRock != null)
                {
                    shell.Alive = false;
                    hitRock.Hull -= shell.Damage;
                    if (hitRock.Hull <= 0f) hitRock.Hull = 0f;
                    w.Emit("ObstacleHit", shell.OwnerId, hitRock.Id, "陨石被削去 " + shell.Damage.ToString("0"));
                }
                else if (hitShip != null)
                {
                    shell.Alive = false;

                    // 威胁来向 = 弹丸相对该舰的飞行方向取反。用相对速度而不是绝对速度：
                    // 舰速 30 km/s、弹速 220–300 km/s，忽略舰速会让判面偏出几度，
                    // 恰好落在扇区边界上时正面会被算成侧面。
                    Vec3 relVel = shell.Vel - hitShip.Velocity;
                    Vec3 incoming = relVel.SqrLength > 1e-8f
                        ? -relVel.Normalized
                        : (shell.PrevPos - hitShip.Pos).Normalized;

                    DamageResolver.ResolveHit(w, hitShip, shell.OwnerId,
                        shell.Channel, shell.Damage, incoming);
                }
            }

            // 回收已命中的炮弹，避免列表无限增长（同时保持插入序，不影响确定性）。
            int write = 0;
            for (int i = 0; i < w.Shells.Count; i++)
                if (w.Shells[i].Alive) w.Shells[write++] = w.Shells[i];
            if (write < w.Shells.Count) w.Shells.RemoveRange(write, w.Shells.Count - write);
        }

        /// <summary>
        /// 线段 a→b 与以原点为心、半径 r 的球是否相交，返回最早的进入参数 t∈[0,1]。
        /// 起点已在球内按 t=0 处理。
        /// </summary>
        public static bool SegmentSphere(Vec3 a, Vec3 b, float r, out float t)
        {
            t = 0f;
            Vec3 d = b - a;
            float A = Vec3.Dot(d, d);
            float C = Vec3.Dot(a, a) - r * r;

            if (A < 1e-12f)
            {
                if (C > 0f) return false;
                t = 0f;
                return true;
            }

            float B = 2f * Vec3.Dot(a, d);
            float disc = B * B - 4f * A * C;
            if (disc < 0f) return false;

            float sqrt = (float)Math.Sqrt(disc);
            float t1 = (-B - sqrt) / (2f * A);
            if (t1 >= 0f && t1 <= 1f) { t = t1; return true; }

            float t2 = (-B + sqrt) / (2f * A);
            if (t2 >= 0f && t2 <= 1f) { t = 0f; return true; }   // 起点在球内

            return false;
        }
    }
}
