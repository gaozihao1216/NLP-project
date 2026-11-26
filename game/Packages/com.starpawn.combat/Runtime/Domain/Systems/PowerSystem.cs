// 能源：推进、转向、开火三路共用储能池，反应堆缓慢回充。
// 护盾不在这里扣电——它按「被挡掉的伤害」计费，落在 DamageResolver 里；
// 缓冲池的回充也归 ShieldSystem。PowerSystem 只管持续负载与储能状态。
//
// 为什么把耗电放在 Movement 之前：这一 tick 的推力档位与操舵输入已经由 ApplyPendingCommands
// 写好了，Power 先按「这一 tick 打算怎么飞、打几发」扣电，再决定推力是否被锁。
// 于是 Movement 读到的 ThrustSetpoint 一定是已经过能源约束的值，物理侧不需要知道能源存在。
//
// 为什么用滞回：能源归零瞬间把推进锁死，若一有电就解锁，会出现「每 tick 抖一下」的断续推力。
// 必须回充到 RestartFraction 以上才恢复，玩家看到的是「熄火 → 滑行 → 重新点火」。
//
// 一条不变式（本条改动的原因）：**扣了电的推力，一定是这一 tick 真会执行的推力**。
// 旧实现先按 ThrustSetpoint 计费、最后才因锁定把档位清零，于是熄火后继续请求满推的舰
// 会一直为「不会发生的加速」付电，能源停在 0 上再也回不到重启阈值——AI 每 tick 重写档位，
// 会稳定命中这个死循环。所以现在先定「实际推力」，再计费。

using System;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain.Systems
{
    public static class PowerSystem
    {
        /// <summary>推进效率：分配越高，每单位推力耗电越少。均衡档（0.34）恰好约等于 1.0。</summary>
        public static float PropulsionEfficiency(float propulsionShare)
            => 0.55f + 1.35f * Clamp01(propulsionShare);

        public static void Step(CombatWorld w, float dt)
        {
            for (int i = 0; i < w.Ships.Count; i++)
            {
                var s = w.Ships[i];
                if (!s.IsAlive) continue;

                var prof = s.Stats.Power;
                float eff = PropulsionEfficiency(s.Power.Propulsion);

                float turnUse = (Math.Abs(s.AngularInput.X) +
                                 Math.Abs(s.AngularInput.Y) +
                                 Math.Abs(s.AngularInput.Z)) / 3f;
                // 飞控反喷的电费忽略：RCS 单次脉冲的能耗比主推进小一个量级，
                // 且松键后的残余减速本来就不该让玩家觉得「停下来也要花电」。
                float turnDrain = prof.TurnDrainPerSec * turnUse / eff;

                // 滞回解锁放在计费之前：这一 tick 才用得上推进。
                if (s.PropulsionLocked && s.Energy >= prof.Capacity * prof.RestartFraction)
                {
                    s.PropulsionLocked = false;
                    w.Emit("PowerRestored", s.Id, default(EntityId),
                        "能源回到 " + (s.Energy / prof.Capacity * 100f).ToString("0") + "%，推进恢复");
                }

                // 锁定时推进不工作，也就没有主推进负载——只按转向收费。
                float thrustUse = 0f;
                if (!s.PropulsionLocked)
                {
                    thrustUse = Math.Abs(s.ThrustSetpoint);
                    // 反推的喷口效率低：同样推力要多花一点电。
                    if (s.ThrustSetpoint < 0f) thrustUse *= 1.4f;
                }
                float thrustDrain = prof.PropulsionDrainPerSec * thrustUse / eff;

                // 这一档推力会把电打穿：本 tick 就先熄火再计费，不是先扣电、事后才把推力清零。
                // 差别就是「扣了电却没加速」——玩家感觉得到，而且熄火后持续请求满推的舰
                // 会一直付这笔钱，能源死在 0 上永远回不到重启阈值。
                if (!s.PropulsionLocked && s.Energy + (prof.RegenPerSec - thrustDrain - turnDrain) * dt <= 0f)
                {
                    s.PropulsionLocked = true;
                    thrustDrain = 0f;
                    w.Emit("PowerEmpty", s.Id, default(EntityId), "能源耗尽，推进停摆");
                }

                s.Energy += (prof.RegenPerSec - thrustDrain - turnDrain) * dt;
                if (s.Energy > prof.Capacity) s.Energy = prof.Capacity;
                if (s.Energy < 0f) s.Energy = 0f;

                // 锁定的舰不再保留推力请求：Movement 读到 0，看到的是「熄火」而不是「档位还在却没加速度」。
                if (s.PropulsionLocked) s.ThrustSetpoint = 0f;
            }
        }

        /// <summary>开火前检查电量。返回 false 表示这一发打不出去。</summary>
        public static bool TryConsumeShot(CombatWorld w, CombatShip ship)
        {
            float cost = ship.Stats.Power.WeaponDrainPerShot;
            if (ship.Energy < cost) return false;
            ship.Energy -= cost;
            return true;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
