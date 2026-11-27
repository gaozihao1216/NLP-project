// 仿真坐标 → Unity 世界坐标的换算。
//
// 口径（2026-11-26 定）：1 Unity 单位 = 1 km。战场尺度为几百 km（敌舰 260 km 外出场），
// float 在这个量级下精度足够；以后改比例尺只改 KmPerUnit 与相机参数，不碰 View 逻辑。
//
// 姿态不换算：快照的 Yaw/Pitch/Roll 与 Unity 的 Quaternion.Euler(pitch, yaw, roll) 同约定
//（见 docs/architecture.md 第 2 节），直接透传。

using UnityEngine;
using StarPawn.Combat.Contracts;

namespace StarPawn.Presentation
{
    public static class SimMath
    {
        /// <summary>1 个 Unity 单位代表多少 km。当前 1:1，只在此处定义。</summary>
        public const float KmPerUnit = 1f;

        public static Vector3 ToWorld(float xKm, float yKm, float zKm)
        {
            return new Vector3(xKm / KmPerUnit, yKm / KmPerUnit, zKm / KmPerUnit);
        }

        public static Vector3 ToWorld(Vec3 vKm)
        {
            return ToWorld(vKm.X, vKm.Y, vKm.Z);
        }

        public static Vector3 ToWorldDir(Vec3 dir)
        {
            // 方向不做除法，只负责把仿真轴向映射到 Unity 轴向（当前两套约定一致）。
            return new Vector3(dir.X, dir.Y, dir.Z);
        }
    }
}
