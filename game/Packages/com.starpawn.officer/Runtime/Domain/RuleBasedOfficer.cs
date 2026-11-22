using System;
using System.Threading.Tasks;
using StarPawn.Foundation;
using StarPawn.Officer.Contracts;

namespace StarPawn.Officer.Domain
{
    /// <summary>规则式副官。LLM 对话用 NullDialogueProvider 降级，不进入战斗链路。</summary>
    public sealed class RuleBasedOfficer : IStrategicAdvisor, ITacticalHintProvider, IOfficerDialogue
    {
        public OfficerAdvice Analyze(VisibleIntel intel) => new OfficerAdvice
        {
            Text = intel.KnownEnemyIds != null && intel.KnownEnemyIds.Length > 0
                ? "侦测到敌方目标 " + intel.KnownEnemyIds.Length + " 个，建议编成巡洋舰核心、巡逻舰前出。"
                : "当前星域无已知威胁，可执行补给与侦察。",
            Reasons = new[] { "基于玩家可见情报" }
        };

        public TacticalHint NextHint(BattleSnapshotView view)
        {
            if (view.PlayerHealth01 < 0.3f) return new TacticalHint { Text = "舰体受损严重，建议脱离交火距离。", Priority = 1f };
            if (view.AliveEnemies > view.AliveAllies) return new TacticalHint { Text = "敌方数量占优，建议集中火力逐个击破。", Priority = 0.6f };
            return new TacticalHint { Text = "目标进入射程，注意提前量。", Priority = 0.2f };
        }

        public Task<string> AnswerAsync(string question, VisibleIntel intel)
            => Task.FromResult("（离线降级）星零知识库未连接，问题已记录：" + question);
    }
}
