// 能源：推进、转向、护盾、开火四路共用一个储能池，反应堆缓慢回充。
//
// 为什么把耗电放在 Movement 之前：这一 tick 的推力档位与操舵输入已经由 ApplyPendingCommands
// 写好了，Power 先按「这一 tick 打算怎么飞、怎么充盾」扣电，再决定推力是否被锁。
// 于是 Movement 读到的 ThrustSetpoint 一定是已经过能源约束的值，物理侧不需要知道能源存在。
//
// 为什么用滞回：能源归零瞬间把推进锁死，若一有电就解锁，会出现「每 tick 抖一下」的断续推力。
// 必须回充到 RestartFraction 以上才恢复，玩家看到的是「熄火 → 滑行 → 重新点火」。

using System;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain.Systems
{
    public static class PowerSystem
    {
        /// <summary>护盾供电的软下限：能量低于容量的这个比例后，回充速度线性衰减到 0。</summary>
        public const float ShieldBrownoutFraction = 0.05f;

        /// <summary>推进效率：分配越高，每单位推力耗电越少。均衡档（0.34）恰好约等于 1.0。</summary>
        public static float PropulsionEfficiency(float propulsionShare)
            => 0.55f + 1.35f * Clamp01(propulsionShare);

        /// <summary>
        /// 本 tick 的护盾回充速率。三路分配里护盾份额为 1/3 时等于标称速率，全给护盾是三倍。
        /// 能量见底时按 ShieldBrownoutFraction 线性掉到 0——没电就别想充盾。
        /// </summary>
        public static float ShieldRegenRate(CombatShip ship)
        {
            if (ship.Stats == null) return 0f;
            float share = Clamp01(ship.Power.Shield) * 3f;
            float supply = ship.SimulatesEnergy
                ? Clamp01(ship.Energy / (ship.EnergyMax * ShieldBrownoutFraction))
                : 1f;
            return ship.Stats.ShieldRegenPerSec * share * supply;
        }

        public static void Step(CombatWorld w, float dt)
        {
            for (int i = 0; i < w.Ships.Count; i++)
            {
                var s = w.Ships[i];
                if (!s.IsAlive) continue;

                var prof = s.Stats.Power;
                bool simulated = s.SimulatesEnergy;

                // 阶段一：只有主舰参与能源模拟，其余舰按满电处理（不扣电、不锁推进）。
                // 注意不能 continue——护盾回充对全队都要生效，只是不记账。
                if (!simulated)
                {
                    s.Energy = prof.Capacity;
                    s.PropulsionLocked = false;
                }

                float drain = 0f;
                if (simulated)
                {
                    float thrustUse = Math.Abs(s.ThrustSetpoint);
                    // 反推的喷口效率低：同样推力要多花一点电。
                    if (s.ThrustSetpoint < 0f) thrustUse *= 1.4f;
                    float turnUse = (Math.Abs(s.AngularInput.X) +
                                     Math.Abs(s.AngularInput.Y) +
                                     Math.Abs(s.AngularInput.Z)) / 3f;
                    // 飞控反喷的电费忽略：RCS 单次脉冲的能耗比主推进小一个量级，
                    // 且松键后的残余减速本来就不该让玩家觉得「停下来也要花电」。

                    float eff = PropulsionEfficiency(s.Power.Propulsion);
                    drain = prof.PropulsionDrainPerSec * thrustUse / eff
                          + prof.TurnDrainPerSec * turnUse / eff;

                    // 护盾回充要占电，按当前分配折算；盾满则不占。
                    float shieldShare = Clamp01(s.Power.Shield) * 3f;
                    if (s.Shield < s.Stats.ShieldCapacity - 1e-4f) drain += prof.ShieldDrainPerSec * shieldShare;

                    s.Energy += (prof.RegenPerSec - drain) * dt;
                    if (s.Energy > prof.Capacity) s.Energy = prof.Capacity;
                    if (s.Energy < 0f) s.Energy = 0f;
                }

                // 护盾回充由能源系统统一执行：它是一条耗电通路，不该散落在伤害判定里。
                float regen = ShieldRegenRate(s);
                if (regen > 0f && s.Shield < s.Stats.ShieldCapacity)
                {
                    s.Shield += regen * dt;
                    if (s.Shield > s.Stats.ShieldCapacity) s.Shield = s.Stats.ShieldCapacity;
                }

                if (!simulated) continue;

                if (s.Energy <= 0f)
                {
                    // 能源见底：推进强制归到无推力档，直到回充过阈值。
                    if (!s.PropulsionLocked)
                    {
                        s.PropulsionLocked = true;
                        w.Emit("PowerEmpty", s.Id, default(EntityId), "能源耗尽，推进停摆");
                    }
                }
                else if (s.PropulsionLocked && s.Energy >= prof.Capacity * prof.RestartFraction)
                {
                    s.PropulsionLocked = false;
                    w.Emit("PowerRestored", s.Id, default(EntityId),
                        "能源回到 " + (s.Energy / prof.Capacity * 100f).ToString("0") + "%，推进恢复");
                }

                if (s.PropulsionLocked) s.ThrustSetpoint = 0f;
            }
        }

        /// <summary>开火前检查电量。返回 false 表示这一发打不出去。</summary>
        public static bool TryConsumeShot(CombatWorld w, CombatShip ship)
        {
            if (!ship.SimulatesEnergy) return true;
            float cost = ship.Stats.Power.WeaponDrainPerShot;
            if (ship.Energy < cost) return false;
            ship.Energy -= cost;
            return true;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
