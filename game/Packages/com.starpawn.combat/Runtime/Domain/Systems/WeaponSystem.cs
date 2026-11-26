// 武器：按**炮座**装填、判射界、出弹。炮弹真实飞行，不做瞬间命中。
//
// 炮塔化之后的四条硬约束：
//   1. 装填是每座独立的。一门炮打出去只让那一门进 CD，其余照常待发——
//      「齐射」只是同一 tick 里有多座恰好都好了，不是全舰共用一条 CD。
//   2. 炮塔**不建模转向速度**。判两件事就够：装填好了没、目标在不在射界里。
//      数十门炮各带转速会让「能不能打」取决于玩家读不出来的中间量。
//   3. 射界用「中心角 + 半张角」判，出界就是不打（不像旧实现钳到锥边硬开一炮）。
//      这一条同时解决了「炮弹不能打到自己舰体」：舰首炮半张角 140° 就伸不到舰尾。
//   4. 任何一步失败都「什么都不改」：不扣电、不进装填、不动随机序列。
//
// 投送方式：**只有实体投射体一种**。所有炮座都建 Projectile 真实飞行，
// 所以这里不按 Channel 分支——Beam 通道的炮座在配置层就被拒了（见 DamageChannel 注释），
// 编成校验还会再挡一次。等 Phase 1.5 做光束投送时，才需要在这里按投送方式分叉。
//
// 每 tick 只解一次瞄：RefreshAims 填好 TurretAims，开火判定、就位计数、对外快照
// 三处读同一份结果。分散着各算一遍，迟早会出现「HUD 说这门能打、按了却没出弹」。

using System;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain.Systems
{
    public static class WeaponSystem
    {
        private const float Deg2Rad = 0.017453292f;
        private const float Rad2Deg = 57.29578f;

        public static void Step(CombatWorld w, float dt)
        {
            for (int i = 0; i < w.Ships.Count; i++)
            {
                var s = w.Ships[i];
                if (!s.IsAlive || s.Stats == null) continue;

                var defs = s.Stats.Turrets;
                // 直接 new 出来的舰（测试、假实现）没走过 ResetTurrets，这里补一次。
                // 两个数组都要核：只补一个会让瞄准结果越界读写。
                if (s.TurretReload == null || s.TurretReload.Length != defs.Length ||
                    s.TurretAims == null || s.TurretAims.Length != defs.Length)
                    s.ResetTurrets();

                // 交战档把能量给武器：装填最快；航行档武器最慢。分配是每舰自己的。
                float reloadScale = 1.4f - 0.8f * Clamp01(s.Power.Weapons);
                s.ReloadScale = reloadScale;

                for (int t = 0; t < s.TurretReload.Length; t++)
                {
                    if (s.TurretReload[t] <= 0f) continue;
                    s.TurretReload[t] -= dt;
                    if (s.TurretReload[t] < 0f) s.TurretReload[t] = 0f;
                }

                // 解瞄 + 判界每 tick 都做，不等开火请求：HUD 的「3/4 就位」与逐座装填环
                // 要在玩家没按开火时也准确。
                RefreshAims(w, s, defs);
                Recount(s, defs);

                if (!s.FireRequested) continue;
                s.FireRequested = false;

                if (s.TurretsLoaded == 0)
                {
                    if (!s.FireRejected)
                        w.Emit("NotReady", s.Id, default(EntityId), "所有炮座都在装填");
                    s.FireRejected = true;
                    continue;
                }

                if (s.TurretsInArc == 0)
                {
                    // 装填好了却一门都对不上：这是玩家最需要知道的信息（该转船了）。
                    // 同一个「打不出去」状态连续出现时只报一次，否则 AI 每 tick 重提开火会把事件流刷满。
                    if (!s.FireRejected)
                        w.Emit("OutOfArc", s.Id, default(EntityId),
                            "炮座已装填但目标不在任何射界内（" + s.TurretsInArc + "/" + s.TurretsLoaded + "）");
                    s.FireRejected = true;
                    continue;
                }

                bool fired = false;
                bool powerLow = false;

                for (int t = 0; t < defs.Length; t++)
                {
                    if (s.TurretReload[t] > 0f) continue;

                    var turret = defs[t];
                    var aim = s.TurretAims[t];
                    if (!aim.HasAim || !aim.InArc) continue;

                    // 确认这一发真能打出去，再抽电。反过来的话，被拒的射击会白扣电。
                    if (!PowerSystem.TryConsumeShot(w, s))
                    {
                        powerLow = true;
                        break;
                    }

                    // 散布消费随机数，必须放在「确定打出去」之后，否则同 seed 会漂。
                    Vec3 dir = ApplySpread(w, s, aim.Dir);
                    Vec3 muzzle = MountWorld(s, turret);

                    var shell = new Projectile
                    {
                        Id = new EntityId(w.NextShellId()),
                        OwnerId = s.Id,
                        FromAlly = s.IsAlly,
                        Pos = muzzle,
                        PrevPos = muzzle,
                        Vel = s.Velocity + dir * turret.ShellVelocityKps,
                        Damage = turret.Damage,
                        Channel = turret.Channel,
                        MaxRangeKm = turret.MaxRangeKm,
                        Alive = true
                    };
                    w.Shells.Add(shell);

                    s.TurretReload[t] = turret.ReloadSeconds * reloadScale;
                    fired = true;
                    s.FireRejected = false;
                    // Detail 是给人看的一句话；表现层读 Shot 里的结构化字段。
                    w.Emit("Fire", s.Id, default(EntityId),
                        turret.Id + " 射程 " + aim.DistKm.ToString("0") + " km",
                        new TurretShot
                        {
                            TurretId = turret.Id,
                            ProjectileId = shell.Id,
                            Origin = muzzle,
                            Direction = dir
                        });
                }

                if (powerLow)
                {
                    if (!s.FireRejected)
                        w.Emit("PowerLow", s.Id, default(EntityId),
                            fired ? "能源不足，剩余炮座未能开火" : "能源不足，无法开火");
                    s.FireRejected = true;
                }

                // 刚打完的这一轮要把就位数重算一遍：否则快照会带着「4 门就位」多留一 tick，
                // HUD 上表现为开火后亮一下才灭。瞄准结果没变（姿态本 tick 不动），只重数装填。
                Recount(s, defs);
            }
        }

        /// <summary>炮座的世界位置。炮弹从这里出膛，而不是从舰体重心——近距离交火时差别肉眼可见。</summary>
        private static Vec3 MountWorld(CombatShip s, TurretProfile t)
            => s.Pos + s.Attitude.Rotate(t.MountLocal);

        /// <summary>
        /// 逐座解一次瞄，结果写进 s.TurretAims。开火判定、就位计数、对外快照都读这一份，
        /// 所以「快照里 CanFire」与「按开火真的出弹」不可能不一致。
        /// </summary>
        private static void RefreshAims(CombatWorld w, CombatShip s, TurretProfile[] defs)
        {
            for (int t = 0; t < defs.Length; t++)
            {
                var turret = defs[t];
                Vec3 mount = MountWorld(s, turret);
                Vec3 dir;
                float dist;
                bool hasAim = TryAim(w, s, turret, mount, out dir, out dist);

                s.TurretAims[t] = new TurretAim
                {
                    HasAim = hasAim,
                    Dir = dir,
                    DistKm = hasAim ? dist : 0f,
                    // 无解时 dir 是沿舰首的待机朝向，不能拿它去判射界。
                    InArc = hasAim && InArc(s, turret, dir, out _)
                };
            }
        }

        /// <summary>装填完 + 在射界内 = 就位。两种情况分开计数，HUD 才能区分「在冷却」和「该转向了」。</summary>
        private static void Recount(CombatShip s, TurretProfile[] defs)
        {
            int loaded = 0, inArc = 0;
            for (int t = 0; t < defs.Length; t++)
            {
                if (s.TurretReload[t] > 0f) continue;
                loaded++;
                if (s.TurretAims[t].InArc) inArc++;
            }
            s.TurretsLoaded = loaded;
            s.TurretsInArc = inArc;
        }

        /// <summary>
        /// 算出这一座炮该打的方向。有锁定就按**该炮自己的弹速**解提前量，
        /// 没有就用玩家给的瞄准点，再没有就沿舰首打——三种来源统一在这里收口。
        /// </summary>
        private static bool TryAim(CombatWorld w, CombatShip s, TurretProfile t,
            Vec3 mount, out Vec3 dir, out float dist)
        {
            dir = s.Attitude.Forward;
            dist = 0f;

            Vec3 aim;
            CombatShip target = s.HasLock ? w.FindShip(s.LockedTargetId) : null;
            if (target != null && target.IsAlive)
            {
                if (!CombatMath.TryLeadPoint(mount, s.Velocity, target.Pos, target.Velocity,
                        t.ShellVelocityKps, out aim))
                    aim = target.Pos;
            }
            else if (s.HasAim)
            {
                aim = s.AimPoint;
            }
            else
            {
                aim = mount + s.Attitude.Forward * t.MaxRangeKm;
            }

            Vec3 toAim = aim - mount;
            float d = toAim.Length;
            if (d < 1e-3f) return false;
            if (d > t.MaxRangeKm) return false;

            dir = toAim / d;
            dist = d;
            return true;
        }

        /// <summary>
        /// 是否落在该炮座的射界内。方位角绕 ±180° 归一后再比半张角，
        /// 所以舰尾炮写 center=180 不需要任何跨零特判。
        /// </summary>
        public static bool InArc(CombatShip s, TurretProfile t, Vec3 dirWorld, out float yawDeg)
        {
            Vec3 local = s.Attitude.Conjugate.Rotate(dirWorld);
            yawDeg = (float)Math.Atan2(local.X, local.Z) * Rad2Deg;
            float elev = (float)Math.Asin(Clamp(local.Y, -1f, 1f)) * Rad2Deg;

            if (elev < t.PitchMinDeg || elev > t.PitchMaxDeg) return false;

            float d = yawDeg - t.YawCenterDeg;
            d -= 360f * (float)Math.Round(d / 360.0);
            return Math.Abs(d) <= t.YawHalfArcDeg + 1e-3f;
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
        private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    }
}
