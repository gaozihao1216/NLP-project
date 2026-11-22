// 星零契约：三种能力分离，大模型对话异步且不进入战斗推进链路。
using System;
using System.Threading.Tasks;
using StarPawn.Foundation;

namespace StarPawn.Officer.Contracts
{
    public sealed class OfficerAdvice { public string Text; public string[] Reasons; }
    public sealed class TacticalHint { public string Text; public float Priority; }

    /// <summary>副官只能读玩家可见情报，不能读未发现敌舰或隐藏剧情。</summary>
    public sealed class VisibleIntel
    {
        public EntityId[] KnownEnemyIds;
        public string CurrentSystemId;
        public string ObjectiveText;
    }

    public interface IStrategicAdvisor
    {
        OfficerAdvice Analyze(VisibleIntel intel);
    }

    public interface ITacticalHintProvider
    {
        TacticalHint NextHint(BattleSnapshotView view);
    }

    public sealed class BattleSnapshotView
    {
        public int AliveAllies;
        public int AliveEnemies;
        public string NearestThreatId;
        public float PlayerHealth01;
    }

    public interface IOfficerDialogue
    {
        Task<string> AnswerAsync(string question, VisibleIntel intel);
    }
}
