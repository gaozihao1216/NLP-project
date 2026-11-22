using System;
using StarPawn.Foundation;
using StarPawn.Narrative.Contracts;

namespace StarPawn.Narrative.Domain
{
    /// <summary>最小剧情引擎：两个节点、一个有后果的选择。只输出效果请求，不改其他模块。</summary>
    public sealed class FakeNarrativeRuntime : INarrativeRuntime
    {
        private string _nodeId = "ch1.intro";
        private StoryChoiceId _lastChoice;

        public NarrativeUpdate Evaluate(NarrativeFacts facts)
        {
            if (_nodeId == "ch1.intro" && facts.SubmittedBattles != null && facts.SubmittedBattles.Length > 0)
                _nodeId = "ch1.after_battle";
            return new NarrativeUpdate { HasNewNode = true, View = ReadCurrentView(), RequestedEffects = new StoryEffect[0] };
        }

        public StoryView ReadCurrentView()
        {
            if (_nodeId == "ch1.after_battle") return new StoryView
            {
                NodeId = _nodeId, Speaker = "奥姆斯特元帅",
                Text = "这一仗打得不错。有人开始注意你了——想好站哪边了吗？",
                Choices = new[] {
                    new StoryChoiceView { Id = new StoryChoiceId("report_merit"), Text = "上报战功，低调行事", RequirementHint = "军功 +20" },
                    new StoryChoiceView { Id = new StoryChoiceId("court_faction"), Text = "接近派系，谋求支持", RequirementHint = "猜疑 +10" }
                }
            };
            return new StoryView
            {
                NodeId = _nodeId, Speaker = "机器人教官",
                Text = "你为什么想当兵？——等级不足，你只能进入最后一支步兵训练营。",
                Choices = new[] {
                    new StoryChoiceView { Id = new StoryChoiceId("obey"), Text = "服从训练，争取军功", RequirementHint = "无" },
                    new StoryChoiceView { Id = new StoryChoiceId("resist"), Text = "拒绝服从，触发冲突", RequirementHint = "忠诚不足时可用" }
                }
            };
        }

        public StoryDecision Choose(StoryChoiceId choiceId, NarrativeFacts facts)
        {
            _lastChoice = choiceId;
            var id = choiceId.ToString();
            if (id == "report_merit") return new StoryDecision
            {
                IsLegal = true, RequestedEffects = new[] { new StoryEffect { Kind = "GrantMerit", Amount = 20 } }
            };
            if (id == "court_faction") return new StoryDecision
            {
                IsLegal = true, RequestedEffects = new[] { new StoryEffect { Kind = "AdjustRelation", TargetId = "npc.chenyu", Amount = 10 } }
            };
            if (id == "obey") { _nodeId = "ch1.after_battle"; return new StoryDecision { IsLegal = true, RequestedEffects = new StoryEffect[0] }; }
            return new StoryDecision { IsLegal = true, DenyReason = null, RequestedEffects = new StoryEffect[0] };
        }
    }
}
