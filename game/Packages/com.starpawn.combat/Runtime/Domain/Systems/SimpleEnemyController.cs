// 敌舰行为：面向玩家、保持射程、装填好就开火。
// 只产出命令（角速度输入/档位/瞄准/开火），不直接改位置或血量，绕不开装填与射程限制。

using System;
using StarPawn.Combat.Contracts;

namespace StarPawn.Combat.Domain.Systems
{
    public static class SimpleEnemyController
    {
        private const float Rad2Deg = 57.29578f;

        public static void Step(CombatWorld w)
        {
            for (int i = 0; i < w.Ships.Count; i++)
            {
                var s = w.Ships[i];
                if (s.IsAlly || !s.IsAlive) continue;
                Update(w, s);
            }
        }

        private static void Update(CombatWorld w, CombatShip s)
        {
            CombatShip target = NearestOpponent(w, s);
            if (target == null) return;

            var m = s.Stats.Maneuver;

            // 提前量：用和 HUD 同一个公式，避免「指示点准但打不中」。
            Vec3 aim;
            if (!CombatMath.TryLeadPoint(s.Pos, s.Velocity, target.Pos, target.Velocity,
                    s.Stats.ShellVelocityKps, out aim))
                aim = target.Pos;

            Vec3 toAim = aim - s.Pos;
            if (toAim.SqrLength < 1e-6f) return;

            // 把目标方向转到本体坐标：x 右、y 上、z 前。
            Vec3 local = s.Attitude.Conjugate.Rotate(toAim.Normalized);
            float yawErrDeg = (float)Math.Atan2(local.X, local.Z) * Rad2Deg;
            float pitchErrDeg = -(float)Math.Asin(Clamp(local.Y, -1f, 1f)) * Rad2Deg;

            // 角度误差 → 目标角速度 → 归一化输入，走和玩家同一条通道。
            float desiredYaw = Clamp(yawErrDeg * 2f, -m.YawRateDps, m.YawRateDps);
            float desiredPitch = Clamp(pitchErrDeg * 2f, -m.PitchRateDps, m.PitchRateDps);

            s.AngularInput = new Vec3(
                MovementSystem.InputForTargetRate(s.AngularVelocity.X, desiredPitch, m.PitchAccelDps2, w.Dt),
                MovementSystem.InputForTargetRate(s.AngularVelocity.Y, desiredYaw, m.YawAccelDps2, w.Dt),
                0f);
            s.FlightAssist = true;

            // 距离控制：远则满推，进入射程后回落到 70% 射程保持距离。
            float dist = Vec3.Distance(s.Pos, target.Pos);
            float preferred = s.Stats.GunRangeKm * 0.7f;
            float thrust = dist > s.Stats.GunRangeKm * 0.9f ? 1f : Clamp((dist - preferred) / 40f, -0.5f, 1f);
            s.ThrustSetpoint = thrust;

            s.AimPoint = aim;
            s.HasAim = true;

            // 开火只判「在射程内」。射界由炮座自己判——炮塔化之后一门炮能打 ±140°，
            // 还按舰首 ±15° 卡的话，AI 会白白浪费掉几乎所有炮座。
            bool inRange = dist <= s.Stats.GunRangeKm * 0.95f;
            if (inRange) s.FireRequested = true;
        }

        private static CombatShip NearestOpponent(CombatWorld w, CombatShip self)
        {
            CombatShip best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < w.Ships.Count; i++)
            {
                var other = w.Ships[i];
                if (other.IsAlly == self.IsAlly || !other.IsAlive) continue;
                float sqr = (other.Pos - self.Pos).SqrLength;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = other;
                }
            }
            return best;
        }

        private static float Clamp(float v, float min, float max)
            => v < min ? min : (v > max ? max : v);
    }
}
