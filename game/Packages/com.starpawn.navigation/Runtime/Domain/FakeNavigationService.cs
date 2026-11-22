using System;
using StarPawn.Foundation;
using StarPawn.Navigation.Contracts;

namespace StarPawn.Navigation.Domain
{
    public sealed class FakeNavigationService : INavigationService
    {
        private EntityId _current;
        private NavigationSnapshot _snap;
        public FakeNavigationService(EntityId start) { _current = start; Rebuild(); }

        private void Rebuild()
        {
            _snap = new NavigationSnapshot
            {
                CurrentSystemId = _current,
                IsTravelling = false,
                Progress01 = 0f,
                KnownSystems = new[] {
                    new SystemView { SystemId = _current, Name = "凯拉星系", IsVisited = true,
                                     Neighbors = new[] { new EntityId("sys.zhongzhou") } },
                    new SystemView { SystemId = new EntityId("sys.zhongzhou"), Name = "中州星", IsVisited = false,
                                     Neighbors = new[] { _current } }
                }
            };
        }

        public NavigationSnapshot Read() => _snap;
        public CommandResult TravelTo(EntityId destination)
        {
            _current = destination; Rebuild();
            return CommandResult.Ok();
        }
        public CommandResult AbortTravel() => CommandResult.Ok();
    }
}
