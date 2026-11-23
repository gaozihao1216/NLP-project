using System;
using System.Collections.Generic;
using StarPawn.Foundation;
using StarPawn.Navigation.Contracts;

namespace StarPawn.Navigation.Domain
{
    public sealed class FakeNavigationService : INavigationService, IStateCheckpoint
    {
        private EntityId _current;
        private List<SystemView> _known;

        public FakeNavigationService(EntityId start)
        {
            _current = start;
            _known = new List<SystemView>
            {
                new SystemView { SystemId = start, Name = "凯拉星系", IsVisited = true,
                                 Neighbors = new[] { new EntityId("sys.zhongzhou") } },
                new SystemView { SystemId = new EntityId("sys.zhongzhou"), Name = "中州星", IsVisited = false,
                                 Neighbors = new[] { start } }
            };
        }

        private static SystemView[] CopyKnown(List<SystemView> src)
        {
            var r = new SystemView[src.Count];
            for (int i = 0; i < src.Count; i++)
            {
                var s = src[i];
                r[i] = new SystemView { SystemId = s.SystemId, Name = s.Name, IsVisited = s.IsVisited,
                    Neighbors = s.Neighbors == null ? new EntityId[0] : (EntityId[])s.Neighbors.Clone() };
            }
            return r;
        }

        public NavigationSnapshot Read() => new NavigationSnapshot
        {
            CurrentSystemId = _current, IsTravelling = false, Progress01 = 0f, KnownSystems = CopyKnown(_known)
        };

        public CommandResult TravelTo(EntityId destination)
        {
            _current = destination;
            ClaimSystem(destination);
            return CommandResult.Ok();
        }

        public CommandResult AbortTravel() => CommandResult.Ok();

        public CommandResult ClaimSystem(EntityId systemId)
        {
            if (systemId.IsEmpty) return CommandResult.Fail("BAD_SYSTEM_ID", "星系 ID 为空");
            foreach (var s in _known)
                if (s.SystemId == systemId) { s.IsVisited = true; return CommandResult.Ok(); }
            _known.Add(new SystemView { SystemId = systemId, Name = systemId.ToString(),
                IsVisited = true, Neighbors = new[] { _current } });
            return CommandResult.Ok();
        }

        public object CaptureState()
        {
            return new NavCheckpoint { Current = _current.ToString(), Known = CopyKnown(_known) };
        }

        public void RestoreState(object state)
        {
            var cp = state as NavCheckpoint;
            if (cp == null) return;
            _current = new EntityId(cp.Current);
            _known = new List<SystemView>(cp.Known);
        }

        private sealed class NavCheckpoint
        {
            public string Current;
            public SystemView[] Known;
        }
    }
}
