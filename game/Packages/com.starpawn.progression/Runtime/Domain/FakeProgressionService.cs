using System;
using StarPawn.Foundation;
using StarPawn.Progression.Contracts;

namespace StarPawn.Progression.Domain
{
    public sealed class FakeProgressionService : IProgressionService
    {
        private int _merit;
        private int _level = 1;
        public ProgressionSnapshot Read() => new ProgressionSnapshot
        {
            Merit = _merit, RankId = "rank.ensign", RankLevel = _level, UnlockedContents = new ContentId[0]
        };
        public CommandResult AwardMerit(int amount, string reason)
        {
            if (amount < 0) return CommandResult.Fail("INVALID_MERIT", "军功不能为负");
            _merit += amount; return CommandResult.Ok();
        }
        public bool IsUnlocked(ContentId contentId) => _level >= 2;
        public CommandResult EvaluatePromotion()
        {
            if (_merit >= 100 && _level == 1) { _level = 2; return CommandResult.Ok(); }
            return CommandResult.Ok();
        }
    }
}
