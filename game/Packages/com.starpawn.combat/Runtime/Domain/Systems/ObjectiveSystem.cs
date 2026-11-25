// 胜负与战斗事实。Combat 只产出事实（谁沉了、剩多少血），不决定军功与剧情。

using System.Collections.Generic;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain.Systems
{
    public static class ObjectiveSystem
    {
        public static void Step(CombatWorld w, float dt)
        {
            if (w.Phase != BattlePhase.Running) return;

            int allies = 0, enemies = 0;
            for (int i = 0; i < w.Ships.Count; i++)
            {
                var s = w.Ships[i];
                if (!s.IsAlive) continue;
                if (s.IsAlly) allies++; else enemies++;
            }

            var obj = w.Spec.Objective;
            string kind = obj != null && !string.IsNullOrEmpty(obj.Kind) ? obj.Kind : "DestroyAll";

            if (enemies == 0)
            {
                w.Phase = BattlePhase.Victory;
                w.Emit("Victory", default(EntityId), default(EntityId), "敌方编队被击溃");
                return;
            }
            if (allies == 0)
            {
                w.Phase = BattlePhase.Defeat;
                w.Emit("Defeat", default(EntityId), default(EntityId), "我方编队全灭");
                return;
            }

            if (obj != null && !obj.TargetId.IsEmpty)
            {
                var target = w.FindShip(obj.TargetId);
                if (target != null && !target.IsAlive)
                {
                    w.Phase = BattlePhase.Defeat;
                    w.Emit("Defeat", default(EntityId), target.Id, "护卫目标被击沉");
                    return;
                }
            }

            if (obj != null && obj.TimeLimitSeconds > 0f && w.Clock.Seconds >= obj.TimeLimitSeconds)
            {
                // 限时任务超时：Survive 判达成，其余判失败。
                w.Phase = kind == "Survive" ? BattlePhase.Victory : BattlePhase.Defeat;
                w.Emit(w.Phase == BattlePhase.Victory ? "Victory" : "Defeat",
                    default(EntityId), default(EntityId), "到达时限");
            }
        }

        public static BattleResult BuildResult(CombatWorld w)
        {
            var outcomes = new List<ShipBattleOutcome>();
            var destroyedEnemies = new List<EntityId>();
            var lostAllies = new List<EntityId>();

            for (int i = 0; i < w.Ships.Count; i++)
            {
                var s = w.Ships[i];
                bool lost = !s.IsAlive;
                if (lost)
                {
                    if (s.IsAlly) lostAllies.Add(s.Id);
                    else destroyedEnemies.Add(s.Id);
                }
                outcomes.Add(new ShipBattleOutcome
                {
                    ShipId = s.Id,
                    IsLost = lost,
                    // 存活的舰必须带回真实损伤，否则战略侧无法回写长期战损。
                    HullAfter01 = s.HullAfter01,
                    AmmoRemaining = 0
                });
            }

            return new BattleResult
            {
                BattleId = w.Spec.BattleId,
                Victory = w.Phase == BattlePhase.Victory,
                DestroyedEnemyIds = destroyedEnemies.ToArray(),
                LostAllyIds = lostAllies.ToArray(),
                ShipOutcomes = outcomes.ToArray(),
                ElapsedSeconds = w.Clock.Seconds,
                ObjectivesComplete = w.Phase == BattlePhase.Victory
            };
        }

        public static BattleResult CloneResult(BattleResult src)
        {
            if (src == null) return null;
            var outcomes = new ShipBattleOutcome[src.ShipOutcomes.Length];
            for (int i = 0; i < outcomes.Length; i++)
            {
                var o = src.ShipOutcomes[i];
                outcomes[i] = new ShipBattleOutcome
                {
                    ShipId = o.ShipId, IsLost = o.IsLost,
                    HullAfter01 = o.HullAfter01, AmmoRemaining = o.AmmoRemaining
                };
            }
            return new BattleResult
            {
                BattleId = src.BattleId,
                Victory = src.Victory,
                DestroyedEnemyIds = (EntityId[])src.DestroyedEnemyIds.Clone(),
                LostAllyIds = (EntityId[])src.LostAllyIds.Clone(),
                ShipOutcomes = outcomes,
                ElapsedSeconds = src.ElapsedSeconds,
                ObjectivesComplete = src.ObjectivesComplete
            };
        }
    }
}
