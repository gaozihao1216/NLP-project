// 战略移动契约：只管位置与移动进度，不管资源扣除和战场内移动。
using System;
using StarPawn.Foundation;

namespace StarPawn.Navigation.Contracts
{
    public sealed class SystemView
    {
        public EntityId SystemId;
        public string Name;
        public bool IsVisited;
        public EntityId[] Neighbors;
    }

    public sealed class NavigationSnapshot
    {
        public EntityId CurrentSystemId;
        public bool IsTravelling;
        public float Progress01;
        public SystemView[] KnownSystems;
    }

    public interface INavigationService
    {
        NavigationSnapshot Read();
        CommandResult TravelTo(EntityId destination);
        CommandResult AbortTravel();
        /// <summary>战后主张管辖权：已存在则标记已访问，不存在则加入已知星图。</summary>
        CommandResult ClaimSystem(EntityId systemId);
    }
}
