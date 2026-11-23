using System;
using System.Collections.Generic;
using StarPawn.Foundation;
using StarPawn.Relations.Contracts;

namespace StarPawn.Relations.Domain
{
    public sealed class FakeRelationsService : IRelationsService, IStateCheckpoint
    {
        private readonly Dictionary<string, int> _weights = new Dictionary<string, int>
        {
            ["player|npc.chenyu|suspicion"] = -70, ["player|npc.aomusi|loyalty"] = 60
        };

        // 统一键：读写都走这里，不再一个按 from 一个按 to。
        private static string Key(EntityId from, EntityId to, RelationKind kind)
            => from.ToString() + "|" + to.ToString() + "|" + kind.ToString().ToLower();

        public RelationsSnapshot Read()
        {
            var edges = new List<RelationEdge>();
            foreach (var kv in _weights)
            {
                var parts = kv.Key.Split('|');
                if (parts.Length != 3) continue;
                edges.Add(new RelationEdge
                {
                    FromId = new EntityId(parts[0]), ToId = new EntityId(parts[1]),
                    Kind = parts[2] == "suspicion" ? RelationKind.Suspicion :
                           parts[2] == "interest" ? RelationKind.Interest : RelationKind.Loyalty,
                    Weight = kv.Value
                });
            }
            return new RelationsSnapshot
            {
                PersonIds = new[] { new EntityId("npc.chenyu"), new EntityId("npc.aomusi") },
                Edges = edges.ToArray()
            };
        }

        public int GetWeight(EntityId from, EntityId to, RelationKind kind)
        {
            int v; return _weights.TryGetValue(Key(from, to, kind), out v) ? v : 0;
        }

        public CommandResult CanPerform(EntityId actor, string actionId, out string denyReason)
        {
            if (actionId == "report" && GetWeight(actor, new EntityId("npc.chenyu"), RelationKind.Suspicion) < -50)
            { denyReason = "猜疑过高，举报可能反噬"; return CommandResult.Fail("RELATION_BLOCK", denyReason); }
            denyReason = null; return CommandResult.Ok();
        }

        /// <summary>带强制失败开关，供测试验证事务中途失败能全量回滚。</summary>
        public static bool ForceFailNextApplyEffect { get; set; }

        public CommandResult ApplyEffect(EntityId from, EntityId to, RelationKind kind, int delta)
        {
            if (ForceFailNextApplyEffect) { ForceFailNextApplyEffect = false; return CommandResult.Fail("FORCED", "测试注入的关系写入失败"); }
            var key = Key(from, to, kind);
            int next = GetWeight(from, to, kind) + delta;
            if (next > int.MaxValue || next < int.MinValue) return CommandResult.Fail("OVERFLOW", "关系值溢出");
            _weights[key] = next;
            return CommandResult.Ok();
        }

        public object CaptureState() => new Dictionary<string, int>(_weights);
        public void RestoreState(object state)
        {
            var snap = state as Dictionary<string, int>;
            if (snap == null) return;
            _weights.Clear();
            foreach (var kv in snap) _weights[kv.Key] = kv.Value;
        }
    }
}
