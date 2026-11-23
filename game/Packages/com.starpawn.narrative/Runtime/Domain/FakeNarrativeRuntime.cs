using System;
using System.Collections.Generic;
using StarPawn.Foundation;
using StarPawn.Narrative.Contracts;

namespace StarPawn.Narrative.Domain
{
    /// <summary>
    /// 最小剧情引擎：两个节点、有后果的选择。
    /// Choose 只校验并生成决策，不改节点；CommitChoice 才推进并记录已用选项。
    /// </summary>
    public sealed class FakeNarrativeRuntime : INarrativeRuntime, IStateCheckpoint
    {
        private string _nodeId = "ch1.intro";
        private readonly HashSet<string> _usedChoices = new HashSet<string>();

        private static readonly Dictionary<string, string[]> ChoicesByNode = new Dictionary<string, string[]>
        {
            ["ch1.intro"] = new[] { "obey", "resist" },
            ["ch1.after_battle"] = new[] { "report_merit", "court_faction" }
        };

        public NarrativeUpdate Evaluate(NarrativeFacts facts)
        {
            if (_nodeId == "ch1.intro" && facts.SubmittedBattles != null && facts.SubmittedBattles.Length > 0)
                _nodeId = "ch1.after_battle";
            return new NarrativeUpdate
            { HasNewNode = true, View = ReadCurrentView(), RequestedEffects = new StoryEffect[0] };
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
            var id = choiceId.ToString();

            // 1) 未知选项直接拒绝，不再一律 IsLegal=true
            string[] allowed;
            if (!ChoicesByNode.TryGetValue(_nodeId, out allowed) || Array.IndexOf(allowed, id) < 0)
                return new StoryDecision { IsLegal = false, DenyReason = "该选项不属于当前节点: " + id };

            // 2) 同一选项不能重复提交
            if (_usedChoices.Contains(_nodeId + ":" + id))
                return new StoryDecision { IsLegal = false, DenyReason = "该选项已选择过" };

            // 3) resist 需要忠诚不足，用事实里的军功做简化门槛
            if (id == "resist" && facts.Merit >= 20)
                return new StoryDecision { IsLegal = false, DenyReason = "忠诚充足，无法触发冲突" };

            // 4) 只生成效果请求，不改节点、不记录已用
            if (id == "report_merit") return new StoryDecision
            { IsLegal = true, RequestedEffects = new[] { new StoryEffect { Kind = "GrantMerit", Amount = 20 } } };
            if (id == "court_faction") return new StoryDecision
            { IsLegal = true, RequestedEffects = new[] { new StoryEffect { Kind = "AdjustRelation", TargetId = "npc.chenyu", Amount = 10 } } };

            // obey / resist 只推进节点，不产生奖励；节点迁移放到 CommitChoice
            return new StoryDecision { IsLegal = true, RequestedEffects = new StoryEffect[0] };
        }

        public CommandResult CommitChoice(StoryChoiceId choiceId)
        {
            var id = choiceId.ToString();
            string[] allowed;
            if (!ChoicesByNode.TryGetValue(_nodeId, out allowed) || Array.IndexOf(allowed, id) < 0)
                return CommandResult.Fail("CHOICE_NOT_IN_NODE", "选项不属于当前节点");
            var record = _nodeId + ":" + id;
            if (_usedChoices.Contains(record))
                return CommandResult.Fail("CHOICE_ALREADY_USED", "该选项已提交过");

            _usedChoices.Add(record);
            if (id == "obey" || id == "resist") _nodeId = "ch1.after_battle";
            return CommandResult.Ok();
        }

        public object CaptureState()
        {
            return new StoryCheckpoint { NodeId = _nodeId, Used = new HashSet<string>(_usedChoices) };
        }

        public void RestoreState(object state)
        {
            var cp = state as StoryCheckpoint;
            if (cp == null) return;
            _nodeId = cp.NodeId;
            _usedChoices.Clear();
            foreach (var u in cp.Used) _usedChoices.Add(u);
        }

        private sealed class StoryCheckpoint
        {
            public string NodeId;
            public HashSet<string> Used;
        }
    }
}
