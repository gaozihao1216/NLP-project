// 成长契约：军功、军衔、解锁。不参与命中和伤害计算。
using System;
using StarPawn.Foundation;

namespace StarPawn.Progression.Contracts
{
    public sealed class ProgressionSnapshot
    {
        public int Merit;
        public string RankId;
        public int RankLevel;
        public ContentId[] UnlockedContents;
    }

    public interface IProgressionService
    {
        ProgressionSnapshot Read();
        CommandResult AwardMerit(int amount, string reason);
        bool IsUnlocked(ContentId contentId);
        // 晋升由应用层在结算时统一调用，不在战斗中实时升官。
        CommandResult EvaluatePromotion();
    }
}
