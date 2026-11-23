using System;
using System.Collections.Generic;
using StarPawn.Foundation;
using StarPawn.Navigation.Contracts;

namespace StarPawn.Navigation.Domain
{
    public sealed class FakeNavigationService : INavigationService, IStateCheckpoint
    {
        private EntityId _current;
        public FakeNavigationService(EntityId start) { _current = start; }

        private SystemView[] BuildSystems()
        {
            return new[] {
                new SystemView { SystemId = _current, Name = "凯拉星系", IsVisited = true,
                                 Neighbors = new[] { new EntityId("sys.zhongzhou") } },
                new SystemView { SystemId = new EntityId("sys.zhongzhou"), Name = "中州星", IsVisited = false,
                                 Neighbors = new[] { _current } }
            };
        }

        public NavigationSnapshot Read() => new NavigationSnapshot
        {
            CurrentSystemId = _current, IsTravelling = false, Progress01 = 0f, KnownSystems = BuildSystems()
        };
        public CommandResult TravelTo(EntityId destination)
        {
            _current = destination;
            return CommandResult.Ok();
        }
        public CommandResult AbortTravel() => CommandResult.Ok();

        public object CaptureState() => _current.ToString();
        public void RestoreState(object state)
        {
            if (state is string s && !string.IsNullOrEmpty(s)) _current = new EntityId(s);
        }
    }
}
