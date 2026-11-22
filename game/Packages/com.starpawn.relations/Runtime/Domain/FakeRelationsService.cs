using System;
using System.Collections.Generic;
using StarPawn.Foundation;
using StarPawn.Relations.Contracts;

namespace StarPawn.Relations.Domain
{
    public sealed class FakeRelationsService : IRelationsService
    {
        private readonly Dictionary<string, int> _weights = new Dictionary<string, int>
        {
            ["chenyu|suspicion"] = -70, ["aomusi|loyalty"] = 60
        };
        public RelationsSnapshot Read() => new RelationsSnapshot
        {
            PersonIds = new[] { new EntityId("npc.chenyu"), new EntityId("npc.aomusi") },
            Edges = new RelationEdge[0]
        };
        public int GetWeight(EntityId from, EntityId to, RelationKind kind)
        {
            var key = from.ToString().Replace("npc.", "") + "|" + kind.ToString().ToLower();
            return _weights.TryGetValue(key, out var v) ? v : 0;
        }
        public CommandResult CanPerform(EntityId actor, string actionId, out string denyReason)
        {
            if (actionId == "report" && GetWeight(actor, new EntityId("npc.chenyu"), RelationKind.Suspicion) < -50)
            { denyReason = "猜疑过高，举报可能反噬"; return CommandResult.Fail("RELATION_BLOCK", denyReason); }
            denyReason = null; return CommandResult.Ok();
        }
        public CommandResult ApplyEffect(EntityId from, EntityId to, RelationKind kind, int delta)
        {
            var key = to.ToString().Replace("npc.", "") + "|" + kind.ToString().ToLower();
            _weights[key] = GetWeight(from, to, kind) + delta; return CommandResult.Ok();
        }
    }
}
