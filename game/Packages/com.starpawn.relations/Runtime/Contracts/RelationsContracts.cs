// 关系契约：人物/派系关系与政治行为校验。不负责剧情节点，也不碰资源余额。
using System;
using StarPawn.Foundation;

namespace StarPawn.Relations.Contracts
{
    public enum RelationKind { Loyalty, Suspicion, Interest }

    public sealed class RelationEdge
    {
        public EntityId FromId;
        public EntityId ToId;
        public RelationKind Kind;
        public int Weight;
    }

    public sealed class RelationsSnapshot
    {
        public EntityId[] PersonIds;
        public RelationEdge[] Edges;
    }

    public interface IRelationsService
    {
        RelationsSnapshot Read();
        int GetWeight(EntityId from, EntityId to, RelationKind kind);
        CommandResult CanPerform(EntityId actor, string actionId, out string denyReason);
        // 只改关系值，不直接推进剧情。
        CommandResult ApplyEffect(EntityId from, EntityId to, RelationKind kind, int delta);
    }
}
