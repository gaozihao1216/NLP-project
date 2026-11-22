// 战斗假实现：给表现层、战略流程、集成联调用。
// 输入固定 BattleStartSpec，立即产出胜负，不依赖任何战略模块。

using System;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain
{
    public sealed class FakeCombatRuntime : ICombatRuntime
    {
        private BattleStartSpec _spec;
        private BattleSnapshot _snapshot;
        private BattleResult _result;
        private bool _started;

        public void Initialize(BattleStartSpec spec)
        {
            _spec = spec;
            _started = true;
            _result = null;
            var ships = new ShipSnapshot[spec.Allies.Length + spec.Enemies.Length];
            int i = 0;
            foreach (var e in spec.Allies) ships[i++] = Make(e, true);
            foreach (var e in spec.Enemies) ships[i++] = Make(e, false);
            _snapshot = new BattleSnapshot
            {
                Clock = new SimClock(0, spec.FixedStep),
                Ships = ships,
                Projectiles = new ProjectileSnapshot[0],
                Phase = BattlePhase.Running
            };
        }

        private static ShipSnapshot Make(FleetEntry e, bool ally) => new ShipSnapshot
        {
            ShipId = e.ShipId, DefinitionId = e.DefinitionId, IsAlly = ally, Role = e.Role,
            X = e.Spawn.X, Y = e.Spawn.Y, Heading = e.Spawn.Heading,
            Speed = 0f, Health01 = 1f, IsAlive = true, Reload01 = 1f
        };

        public CommandResult Submit(CombatCommand command)
        {
            if (!_started) return CommandResult.Fail("NOT_INITIALIZED", "战斗未初始化");
            if (command is HelmCommand helm)
            {
                foreach (var s in _snapshot.Ships)
                    if (s.ShipId == command.ShipId) { s.Speed = helm.Throttle; s.Heading += helm.Rudder; }
            }
            return CommandResult.Ok();
        }

        public void Step()
        {
            if (!_started || _result != null) return;
            _snapshot.Clock = _snapshot.Clock.Advance();
            // 假实现：推进固定步数后按兵力多寡判定胜负。
            if (_snapshot.Clock.Tick >= 5)
            {
                bool allyWin = _spec.Allies.Length >= _spec.Enemies.Length;
                _snapshot.Phase = allyWin ? BattlePhase.Victory : BattlePhase.Defeat;
                foreach (var s in _snapshot.Ships) if (s.IsAlly != allyWin) { s.IsAlive = false; s.Health01 = 0f; }
                _result = new BattleResult
                {
                    BattleId = _spec.BattleId,
                    Victory = allyWin,
                    DestroyedEnemyIds = allyWin ? Ids(_spec.Enemies) : new EntityId[0],
                    LostAllyIds = allyWin ? new EntityId[0] : Ids(_spec.Allies),
                    ElapsedSeconds = _snapshot.Clock.Seconds,
                    ObjectivesComplete = allyWin
                };
            }
        }

        private static EntityId[] Ids(FleetEntry[] entries)
        {
            var r = new EntityId[entries.Length];
            for (int i = 0; i < entries.Length; i++) r[i] = entries[i].ShipId;
            return r;
        }

        public BattleSnapshot ReadSnapshot() => _snapshot;
        public BattleEvent[] DrainEvents() => new BattleEvent[0];
        public bool TryGetResult(out BattleResult result) { result = _result; return _result != null; }
    }
}
