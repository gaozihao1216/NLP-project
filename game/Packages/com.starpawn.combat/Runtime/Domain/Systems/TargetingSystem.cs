// 目标锁定：把「玩家手动给一个世界坐标」升级成「锁住一条船，仿真替你算提前量」。
//
// 锁定只是把目标 ID 记在舰上，真正的解算每 tick 做一次，写在 AimPoint 里：
//   - 用相对速度解二次方程（和 HUD 指示点、AI 瞄准共用 CombatMath.TryLeadPoint），
//     保证「准星指哪、弹就打哪」；
//   - 目标死亡、跨阵营、或超出锁定距离就自动解锁并发事件，避免玩家对着空气开火。
//
// 解算放在 Movement 之后、Weapon 之前：位置与速度都是本 tick 最新的，
// 且 Weapon 读到的是本 tick 刚算好的瞄准点。

using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain.Systems
{
    public static class TargetingSystem
    {
        public static void Step(CombatWorld w)
        {
            for (int i = 0; i < w.Ships.Count; i++)
            {
                var s = w.Ships[i];
                if (!s.IsAlive || !s.HasLock) continue;

                var target = w.FindShip(s.LockedTargetId);

                // 目标没了 / 是友军 / 跑出锁定距离 → 解锁。锁定不该「粘着」一个已经打不到的目标。
                if (target == null || !target.IsAlive || target.IsAlly == s.IsAlly)
                {
                    s.LockedTargetId = default(EntityId);
                    s.HasAim = false;
                    w.Emit("LockLost", s.Id, default(EntityId), "目标已不可锁定");
                    continue;
                }

                float dist = Vec3.Distance(s.Pos, target.Pos);
                if (dist > s.Stats.GunRangeKm * BattleRules.LockRangeFactor)
                {
                    s.LockedTargetId = default(EntityId);
                    s.HasAim = false;
                    w.Emit("LockLost", s.Id, target.Id, "超出锁定距离 " + dist.ToString("0") + " km");
                    continue;
                }

                // 提前量。解不出来（目标比炮弹快）就打当前位置——至少准星还落在目标上。
                Vec3 aim;
                if (!CombatMath.TryLeadPoint(s.Pos, s.Velocity, target.Pos, target.Velocity,
                        s.Stats.ShellVelocityKps, out aim))
                    aim = target.Pos;

                s.AimPoint = aim;
                s.HasAim = true;
            }
        }
    }
}
