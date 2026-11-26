// 护盾：绑定舰体的方向减伤（DR）+ 过载缓冲池。
//
// 三条不可混的语义，改代码前先读：
//   1. DR 是常驻的几何状态，按「来袭方向落在舰体哪个扇区」给，不消耗任何资源。
//      护盾「厚不厚」看的是三个扇面，不是一条进度条。
//   2. 会掉、会回的是过载缓冲池，它只在「1.5 s 内吸收量超阈值」时被扣。
//      所以它不会退化成第二条血条——平砍永远打不动池，只有饱和才能。
//   3. 破盾不是「池空即穿透下一击」。打空池的那一击仍按已算出的 DR 结算，
//      失效从下一击开始，持续 6 s，结束时只回三成。否则同一次齐射会出现
//      「前三发被挡、第四发全吃」的锯齿，玩家读不出因果。
//
// 抗伤要付电：每点被挡掉的伤害扣 DrainPerDamage 点能源。电不够时先削 DR 再算吸收，
// 顺序不能反——反过来会算出「付不起的电」这种自相矛盾的结果。

using System;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain.Systems
{
    public static class ShieldSystem
    {
        /// <summary>护盾回充的软下限：能源低于容量的这个比例后，回充速度线性衰减到 0。</summary>
        public const float BrownoutFraction = 0.05f;

        /// <summary>cos 45°，正面与侧面的分界。</summary>
        private const float Cos45 = 0.70710678f;

        /// <summary>
        /// 判面：在舰体坐标系里看「威胁来向」落在哪个扇区。
        /// incomingDir 必须是**从目标指向威胁来源**的单位向量；传反了正面与背面会互换，
        /// 而且只有贴近时才看得出来——这是本模块最容易犯、最难发现的错。
        /// </summary>
        public static ShieldFace FaceOf(CombatShip ship, Vec3 incomingDir)
        {
            Vec3 d = incomingDir.Normalized;
            if (d.SqrLength < 1e-8f) return ShieldFace.Front;   // 退化方向按正面算：宁可多挡，不白送背击
            float cos = Vec3.Dot(d, ship.Forward);
            if (cos >= Cos45) return ShieldFace.Front;
            if (cos > -Cos45) return ShieldFace.Side;
            return ShieldFace.Rear;
        }

        /// <summary>转向负荷 0..1：三轴角速度各自按本舰转速上限归一化，取最大者。</summary>
        public static float TurnLoad(CombatShip ship)
        {
            if (ship.Stats == null) return 0f;
            var m = ship.Stats.Maneuver;
            var w = ship.AngularVelocity;

            float yaw = Math.Abs(w.Y) / (m.YawRateDps > 1e-3f ? m.YawRateDps : 1e-3f);
            float pitch = Math.Abs(w.X) / (m.PitchRateDps > 1e-3f ? m.PitchRateDps : 1e-3f);
            float roll = Math.Abs(w.Z) / (m.RollRateDps > 1e-3f ? m.RollRateDps : 1e-3f);

            float load = yaw > pitch ? yaw : pitch;
            if (roll > load) load = roll;
            return load > 1f ? 1f : load;
        }

        /// <summary>某面的有效 DR：破盾时为 0，否则按转向负荷打折（可被 TurnLossEnabled 整体关掉）。</summary>
        public static float EffectiveDr(CombatWorld w, CombatShip ship, ShieldFace face)
        {
            if (ship == null || ship.Stats == null) return 0f;
            if (ship.ShieldBroken) return 0f;

            float dr = ship.Stats.Shield.FaceDr(face);
            if (w != null && w.TurnLossEnabled)
                dr *= 1f - ship.Stats.Shield.TurnLossFactor * TurnLoad(ship);
            return dr < 0f ? 0f : dr;
        }

        public static float EffectiveDr(CombatWorld w, CombatShip ship, Vec3 incomingDir)
            => EffectiveDr(w, ship, FaceOf(ship, incomingDir));

        /// <summary>本 tick 的回充速率：按护盾能量份额与供电状态折算，没电就别想回盾。</summary>
        public static float RegenRate(CombatShip ship)
        {
            if (ship == null || ship.Stats == null) return 0f;
            float share = Clamp01(ship.Power.Shield) * 3f;
            // 全舰记账后储能恒为真值，不再有「该舰不参与模拟」的分支。
            float supply = Clamp01(ship.Energy / (ship.EnergyMax * BrownoutFraction));
            return ship.Stats.Shield.RegenPerSec * share * supply;
        }

        /// <summary>
        /// 每 tick：剪窗口、处理破盾恢复、延迟回充。
        /// 必须在伤害结算之前跑——否则同一 tick 内先回充再被打，玩家会看到「刚回的那点又没了」。
        /// </summary>
        public static void Step(CombatWorld w, float dt)
        {
            float now = w.Clock.Seconds;

            for (int i = 0; i < w.Ships.Count; i++)
            {
                var s = w.Ships[i];
                if (!s.IsAlive || s.Stats == null) continue;

                if (s.ShieldBroken)
                {
                    if (now >= s.ShieldBrokenUntil)
                    {
                        s.ShieldBroken = false;
                        s.ShieldBuffer = s.Stats.Overload.BufferCapacity * s.Stats.Shield.RecoverFraction;
                        s.OverloadWindow.Clear();
                        s.OverloadDeducted = 0f;
                        w.Emit("ShieldRestored", s.Id, default(EntityId),
                            "护盾场重建，缓冲池恢复到 " + (s.Stats.Shield.RecoverFraction * 100f).ToString("0") + "%");
                    }
                    // 失效期既不回充也不累计：窗口停止累计是设计值，不是遗漏。
                    continue;
                }

                PruneWindow(s, now);

                float cap = s.Stats.Overload.BufferCapacity;
                if (s.ShieldBuffer >= cap) continue;

                // 受击后延迟启动：这条是「破防窗口」能存在的原因，破了立刻回等于没破。
                if (now - s.LastHitTime < s.Stats.Shield.RegenDelaySeconds) continue;

                float regen = RegenRate(s);
                if (regen <= 0f) continue;

                s.ShieldBuffer += regen * dt;
                if (s.ShieldBuffer > cap) s.ShieldBuffer = cap;
            }
        }

        /// <summary>
        /// 记一笔吸收量，并在窗口超额时扣缓冲池。返回 true 表示这一击把池打空、护盾进入失效。
        /// 注意本函数不改 DR——调用方已经用它算完这一击的吸收，破盾从下一击开始生效。
        /// </summary>
        public static bool AccumulateOverload(CombatWorld w, CombatShip ship, float absorbed, float now)
        {
            if (absorbed <= 0f || ship.ShieldBroken || ship.Stats == null) return false;

            var ov = ship.Stats.Overload;
            // 先剪窗口再加这一笔：Step 里已经剪过一次，这里再剪一次是为了让本函数自成一体——
            // 直接调 ResolveHit 的测试与工具不需要记得先跑 Step。
            PruneWindow(ship, now);
            ship.OverloadWindow.Add(new OverloadLoad { Time = now, Absorbed = absorbed });

            float sum = 0f;
            var win = ship.OverloadWindow;
            for (int i = 0; i < win.Count; i++) sum += win[i].Absorbed;

            float excess = sum - ov.Threshold;
            if (excess < 0f) excess = 0f;

            // 只扣「比上次多出来的那部分」：窗口里旧记录还没过期时，sum 与 excess 会一起涨，
            // 不减去已扣量就会把同一笔超额重复扣，池会以两倍速度漏。
            float delta = excess - ship.OverloadDeducted;
            if (delta <= 0f) return false;

            ship.ShieldBuffer -= delta;
            ship.OverloadDeducted += delta;

            if (ship.ShieldBuffer > 0f) return false;

            ship.ShieldBuffer = 0f;
            ship.ShieldBroken = true;
            ship.ShieldBrokenUntil = now + ov.BreakSeconds;
            ship.OverloadWindow.Clear();
            ship.OverloadDeducted = 0f;

            w.Emit("ShieldBroken", default(EntityId), ship.Id,
                "护盾过载，失效 " + ov.BreakSeconds.ToString("0") + " s");
            return true;
        }

        /// <summary>剪掉超出窗口的记录，并把已扣量的上限压回当前超额（窗口回退时不退还，只是不许再扣）。</summary>
        public static void PruneWindow(CombatShip ship, float now)
        {
            if (ship.Stats == null) return;
            var ov = ship.Stats.Overload;
            var win = ship.OverloadWindow;

            float cutoff = now - ov.WindowSeconds;
            int drop = 0;
            while (drop < win.Count && win[drop].Time < cutoff) drop++;
            if (drop > 0) win.RemoveRange(0, drop);

            float sum = 0f;
            for (int i = 0; i < win.Count; i++) sum += win[i].Absorbed;

            float excess = sum - ov.Threshold;
            if (excess < 0f) excess = 0f;
            if (ship.OverloadDeducted > excess) ship.OverloadDeducted = excess;
        }

        public static string FaceName(ShieldFace face)
        {
            switch (face)
            {
                case ShieldFace.Front: return "正面";
                case ShieldFace.Side: return "侧面";
                default: return "背面";
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
