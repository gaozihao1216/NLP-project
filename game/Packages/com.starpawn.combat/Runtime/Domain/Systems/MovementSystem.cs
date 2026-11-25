// 运动：真空无阻力，推力只改速度矢量；转向靠 RCS 力矩，按键给的是角加速度。
//
// 自动回正不是「把角速度设成 0」，而是飞控给出一个反向角加速度，让 ω 按同一条积分公式
// 线性减到 0。玩家输入和飞控输出因此共用一条通道（StepAxis），只有一个写 ω 的地方。
//
// 两条必须守住的前提：
//   1) 推力方向恒为舰体主轴（局部 +Z → 世界 Forward）。推力与速度方向无关，
//      侧滑/倒飞时舰照样沿舰首加速，这正是「无阻力空间 + 主推进」该有的行为。
//   2) 速度是矢量、姿态是四元数，两者完全解耦：转向不改变速度，只改变下一 tick 的推力方向。

using System;
using StarPawn.Combat.Contracts;

namespace StarPawn.Combat.Domain.Systems
{
    public static class MovementSystem
    {
        /// <summary>死区：低于这个角速度且无输入就视为 0，避免姿态小数位在 30 Hz 下微抖。</summary>
        public const float DeadZoneDps = 0.5f;

        private const float Rad2Deg = 57.29578f;
        private const float Epsilon = 1e-4f;

        public static void Step(CombatWorld w, float dt)
        {
            for (int i = 0; i < w.Ships.Count; i++)
            {
                var s = w.Ships[i];
                if (!s.IsAlive) continue;

                s.PrevPos = s.Pos;
                var m = s.Stats.Maneuver;

                // 主推力沿舰首：Forward = 姿态四元数旋转 (0,0,1)，任何时候都不改方向。
                // 档位值直接当 setpoint，不做速度闭环——真空里档位控的是加速度。
                float thrust = s.PropulsionLocked ? 0f : s.ThrustSetpoint;
                Vec3 accel = s.Attitude.Forward * (m.MaxThrustKmS2 * thrust * dt);

                // 战术航速上限（游戏性让步，不是物理）：已到顶且推力还在往同方向推，就不再施力。
                // 否则玩家会看到「推力满档但速度纹丝不动」，还会在钳速时突然掉速。
                if (thrust > 0f && s.Velocity.Length >= s.Stats.MaxSpeedKps &&
                    Vec3.Dot(accel, s.Velocity) > 0f)
                {
                    accel = Vec3.Zero;
                }

                s.Velocity = s.Velocity + accel;

                // 超限只削大小、不改方向：转向后横向分量可能与原有速度叠加突破上限。
                float speed = s.Velocity.Length;
                if (speed > s.Stats.MaxSpeedKps && speed > 1e-5f)
                    s.Velocity = s.Velocity * (s.Stats.MaxSpeedKps / speed);

                // 三轴独立：按住某轴时其它轴的残余转速仍要继续反喷。
                float pitchRate = StepAxis(s.AngularVelocity.X, s.AngularInput.X,
                    m.PitchRateDps, m.PitchAccelDps2, s.FlightAssist, dt);
                float yawRate = StepAxis(s.AngularVelocity.Y, s.AngularInput.Y,
                    m.YawRateDps, m.YawAccelDps2, s.FlightAssist, dt);

                float rollInput = s.AngularInput.Z;
                if (w.RollLeveling && s.FlightAssist && Math.Abs(rollInput) < Epsilon)
                    rollInput = RollLevelingInput(s, m, dt);
                float rollRate = StepAxis(s.AngularVelocity.Z, rollInput,
                    m.RollRateDps, m.RollAccelDps2, s.FlightAssist, dt);

                s.AngularVelocity = new Vec3(pitchRate, yawRate, rollRate);
                s.Attitude = Quat.IntegrateBody(s.Attitude, s.AngularVelocity, dt);
                s.Pos = s.Pos + s.Velocity * dt;
            }
        }

        /// <summary>
        /// 单轴积分。输入优先；无输入且开着飞控时，输出 u = -ω/(α·dt)：
        /// |ω| 小于一帧能改变的量时 u 不饱和，代进去正好落到 0 且不越过；
        /// 否则 u 被截到 ±1，满反喷线性减速。减速 → 收力 → 停住，没有阶跃。
        /// </summary>
        public static float StepAxis(float omega, float input, float maxRate, float accel, bool assist, float dt)
        {
            float u;
            if (Math.Abs(input) > Epsilon)
            {
                u = input;
            }
            else if (assist)
            {
                float maxDelta = accel * dt;
                u = maxDelta > 1e-9f ? -omega / maxDelta : 0f;
            }
            else
            {
                // 关掉飞控 = 纯牛顿：没有反喷，角速度永远保持。
                u = 0f;
            }

            if (u > 1f) u = 1f;
            else if (u < -1f) u = -1f;

            omega += u * accel * dt;

            if (omega > maxRate) omega = maxRate;
            else if (omega < -maxRate) omega = -maxRate;

            if (assist && Math.Abs(input) <= Epsilon && Math.Abs(omega) < DeadZoneDps)
                omega = 0f;

            return omega;
        }

        /// <summary>把「目标角速度」折算成归一化输入，AI 与滚转回平共用。</summary>
        public static float InputForTargetRate(float current, float target, float accel, float dt)
        {
            float maxDelta = accel * dt;
            if (maxDelta <= 1e-9f) return 0f;
            float u = (target - current) / maxDelta;
            if (u > 1f) return 1f;
            if (u < -1f) return -1f;
            return u;
        }

        /// <summary>
        /// 滚转姿态回平：把机翼拉回世界 +Y。默认关闭——太空没有黄道面这个「上」。
        /// 俯仰接近 ±90° 时机体 up 与舰首平行、参考退化，用 wt 淡出，否则会自己乱滚。
        /// </summary>
        private static float RollLevelingInput(CombatShip s, ManeuverProfile m, float dt)
        {
            Vec3 forward = s.Attitude.Forward;
            Vec3 up = s.Attitude.Up;

            float wt = 1f - Math.Abs(Vec3.Dot(forward, Vec3.Up));
            if (wt < 0.05f) return 0f;

            float sin = Vec3.Dot(Vec3.Cross(up, Vec3.Up), forward);
            float cos = Vec3.Dot(up, Vec3.Up);
            float errDeg = (float)Math.Atan2(sin, cos) * Rad2Deg;

            float target = errDeg * 2f * wt;
            if (target > m.RollRateDps) target = m.RollRateDps;
            else if (target < -m.RollRateDps) target = -m.RollRateDps;

            return InputForTargetRate(s.AngularVelocity.Z, target, m.RollAccelDps2, dt);
        }
    }
}
