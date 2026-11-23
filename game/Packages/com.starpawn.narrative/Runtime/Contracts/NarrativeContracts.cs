// 剧情契约：只接收精选事实，输出待执行效果；不直接修改经济或关系。
using System;
using StarPawn.Foundation;

namespace StarPawn.Narrative.Contracts
{
    /// <summary>剧情可见事实。字段由应用层显式挑选，禁止把全局状态整个塞进来。</summary>
    public sealed class NarrativeFacts
    {
        public EntityId CurrentSystemId;
        public string RankId;
        public int Credits;
        public int Merit;
        public string ActiveMissionId;
        public bool[] MissionCompleted;
        public BattleSummary[] SubmittedBattles;
    }

    public sealed class BattleSummary
    {
        public EntityId BattleId;
        public bool Victory;
        public int DestroyedCount;
    }

    public readonly struct StoryChoiceId
    {
        private readonly string _value;
        public StoryChoiceId(string value) { _value = value; }
        public override string ToString() => _value ?? string.Empty;
    }

    /// <summary>剧情请求的效果。应用层交给对应模块校验执行，剧情无权自己改。</summary>
    public sealed class StoryEffect
    {
        public string Kind;           // "GrantMerit" / "AdjustRelation" / "GrantResource" / "Unlock"
        public string TargetId;
        public int Amount;
    }

    public sealed class StoryView
    {
        public string NodeId;
        public string Speaker;
        public string Text;
        public StoryChoiceView[] Choices;
    }

    public sealed class StoryChoiceView
    {
        public StoryChoiceId Id;
        public string Text;
        public string RequirementHint;
    }

    public sealed class NarrativeUpdate
    {
        public bool HasNewNode;
        public StoryView View;
        public StoryEffect[] RequestedEffects;
    }

    public sealed class StoryDecision
    {
        public bool IsLegal;
        public string DenyReason;
        public StoryEffect[] RequestedEffects;
    }

    /// <summary>
    /// 剧情选择生命周期。Choose 只生成决策不改节点；
    /// CommitChoice 由应用层在效果全部执行成功后调用，才真正推进节点并记录已用选项。
    /// </summary>
    public interface INarrativeRuntime
    {
        /// <summary>
        /// 按事实推进剧情。必须返回 CommandResult：结算工作流把它纳入同一事务，
        /// 剧情推进失败时资源、军功、关系的写入要能一起回滚。
        /// </summary>
        CommandResult Evaluate(NarrativeFacts facts, out NarrativeUpdate update);
        StoryView ReadCurrentView();
        StoryDecision Choose(StoryChoiceId choiceId, NarrativeFacts facts);
        CommandResult CommitChoice(StoryChoiceId choiceId);
    }
}
