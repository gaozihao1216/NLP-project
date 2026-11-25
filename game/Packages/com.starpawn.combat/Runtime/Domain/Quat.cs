// 姿态用四元数，不用欧拉角：三维里俯仰越 90° 时欧拉角会万向锁，
// 表现层会看到舰体「抽搐」。Domain 是 noEngineReferences，所以自己写一份。
//
// 欧拉角约定与 Unity 的 Quaternion.Euler(pitch, yaw, roll) 一致：
//   q = Ry(yaw) · Rx(pitch) · Rz(roll)
// 这样表现层可以直接把 ShipSnapshot 的三个角塞进 transform.rotation，不用换算法。

using System;
using StarPawn.Combat.Contracts;

namespace StarPawn.Combat.Domain
{
    public readonly struct Quat
    {
        private const float Deg2Rad = 0.017453292f;
        private const float Rad2Deg = 57.29578f;

        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly float W;

        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Quat Identity = new Quat(0f, 0f, 0f, 1f);

        /// <summary>Hamilton 积：a ⊗ b 表示先转 b 再转 a（与 Unity 一致）。</summary>
        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public float SqrLength => X * X + Y * Y + Z * Z + W * W;

        public Quat Normalized
        {
            get
            {
                float len = (float)Math.Sqrt(SqrLength);
                if (len < 1e-8f) return Identity;
                return new Quat(X / len, Y / len, Z / len, W / len);
            }
        }

        public Quat Conjugate => new Quat(-X, -Y, -Z, W);

        /// <summary>把向量按本四元数旋转。q·v·q⁻¹ 的展开式，省两次乘法。</summary>
        public Vec3 Rotate(Vec3 v)
        {
            Vec3 q = new Vec3(X, Y, Z);
            Vec3 t = Vec3.Cross(q, v) * 2f;
            return v + t * W + Vec3.Cross(q, t);
        }

        public Vec3 Forward => Rotate(Vec3.Forward);
        public Vec3 Up => Rotate(Vec3.Up);
        public Vec3 Right => Rotate(new Vec3(1f, 0f, 0f));

        public static Quat FromAxisAngle(Vec3 axis, float degrees)
        {
            Vec3 n = axis.Normalized;
            if (n.SqrLength < 1e-8f) return Identity;
            float half = degrees * 0.5f * Deg2Rad;
            float s = (float)Math.Sin(half);
            return new Quat(n.X * s, n.Y * s, n.Z * s, (float)Math.Cos(half));
        }

        public static Quat FromEulerDeg(float yawDeg, float pitchDeg, float rollDeg)
        {
            float hy = yawDeg * 0.5f * Deg2Rad;
            float hp = pitchDeg * 0.5f * Deg2Rad;
            float hr = rollDeg * 0.5f * Deg2Rad;
            var qy = new Quat(0f, (float)Math.Sin(hy), 0f, (float)Math.Cos(hy));
            var qx = new Quat((float)Math.Sin(hp), 0f, 0f, (float)Math.Cos(hp));
            var qz = new Quat(0f, 0f, (float)Math.Sin(hr), (float)Math.Cos(hr));
            return (qy * qx * qz).Normalized;
        }

        /// <summary>反解欧拉角（度）。俯仰接近 ±90° 时 yaw/roll 退化，按滚转 0 处理。</summary>
        public void ToEulerDeg(out float yawDeg, out float pitchDeg, out float rollDeg)
        {
            // R = Ry(yaw)·Rx(pitch)·Rz(roll)
            float m02 = 2f * (X * Z + W * Y);           // R[0][2]
            float m12 = 2f * (Y * Z - W * X);           // R[1][2]
            float m22 = 1f - 2f * (X * X + Y * Y);      // R[2][2]
            float m00 = 1f - 2f * (Y * Y + Z * Z);      // R[0][0]
            float m01 = 2f * (X * Y - W * Z);           // R[0][1]
            float m10 = 2f * (X * Y + W * Z);           // R[1][0]
            float m11 = 1f - 2f * (X * X + Z * Z);      // R[1][1]

            float sinPitch = -m12;
            if (sinPitch > 1f) sinPitch = 1f;
            if (sinPitch < -1f) sinPitch = -1f;
            pitchDeg = (float)Math.Asin(sinPitch) * Rad2Deg;

            if (Math.Abs(sinPitch) > 0.9999f)
            {
                // 视线竖直：yaw 与 roll 合成一个自由度，固定 roll = 0。
                rollDeg = 0f;
                float yawRad = sinPitch > 0f
                    ? (float)Math.Atan2(m01, m00)
                    : (float)Math.Atan2(-m01, m00);
                yawDeg = yawRad * Rad2Deg;
                return;
            }

            yawDeg = (float)Math.Atan2(m02, m22) * Rad2Deg;
            rollDeg = (float)Math.Atan2(m10, m11) * Rad2Deg;
        }

        /// <summary>
        /// 用本体坐标系角速度积分一帧。角速度单位度/秒，顺序为本体 pitch(x)/yaw(y)/roll(z)。
        /// 采用右乘：dq = 0.5 · q ⊗ ω_body · dt，积分后归一化。
        /// </summary>
        public static Quat IntegrateBody(Quat q, Vec3 bodyAngularVelocityDps, float dt)
        {
            float rx = bodyAngularVelocityDps.X * Deg2Rad;
            float ry = bodyAngularVelocityDps.Y * Deg2Rad;
            float rz = bodyAngularVelocityDps.Z * Deg2Rad;
            var omega = new Quat(rx, ry, rz, 0f);
            Quat dq = q * omega;
            var next = new Quat(
                q.X + 0.5f * dq.X * dt,
                q.Y + 0.5f * dq.Y * dt,
                q.Z + 0.5f * dq.Z * dt,
                q.W + 0.5f * dq.W * dt);
            return next.Normalized;
        }

        public override string ToString()
        {
            float yaw, pitch, roll;
            ToEulerDeg(out yaw, out pitch, out roll);
            return "yaw=" + yaw.ToString("0.#") + " pitch=" + pitch.ToString("0.#") + " roll=" + roll.ToString("0.#");
        }
    }
}
