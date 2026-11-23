// 战斗假实现：给表现层、战略流程、集成联调用。
// 不依赖任何战略模块。真实战斗替换时保持 ICombatRuntime / IBattleSession 不变。

using System;
using System.Collections.Generic;
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
        private DeterministicRandom _rng;
        private readonly List<BattleEvent> _events = new List<BattleEvent>();
        private PowerAllocation _power = PowerAllocation.Balanced;

        private sealed class PowerAllocation
        {
            public float Propulsion, Shield, Weapons;
            public PowerPreset Preset;
            public static PowerAllocation Balanced => new PowerAllocation
            { Propulsion = 0.34f, Shield = 0.33f, Weapons = 0.33f, Preset = PowerPreset.Balanced };
        }

        public BattleStartSpec Spec => _spec;

        public void Initialize(BattleStartSpec spec)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            var check = BattleRules.ValidateSpec(spec);
            if (!check.Success) throw new ArgumentException(check.Error.ToString(), nameof(spec));
            _spec = spec;
            _started = true;
            _result = null;
            _events.Clear();
            _rng = new DeterministicRandom(spec.RandomSeed);

            var ships = new List<ShipSnapshot>();
            foreach (var e in spec.Allies) if (e.IsLaunchable) ships.Add(Make(e, true));
            foreach (var e in spec.Enemies) if (e.IsLaunchable) ships.Add(Make(e, false));
            _snapshot = new BattleSnapshot
            {
                Clock = new SimClock(0, spec.FixedStep),
                Ships = ships.ToArray(),
                Projectiles = new ProjectileSnapshot[0],
                Obstacles = GenerateObstacles(spec),
                Phase = BattlePhase.Running
            };
        }

        private static ShipSnapshot Make(FleetEntry e, bool ally)
        {
            // 战损继承：上一场的完整度直接成为本场初始血量，不再无条件满血。
            return new ShipSnapshot
            {
                ShipId = e.ShipId, DefinitionId = e.DefinitionId, IsAlly = ally, Role = e.Role,
                X = e.Spawn.X, Y = e.Spawn.Y, Heading = e.Spawn.Heading,
                Speed = 0f, Health01 = e.InheritedHull01, IsAlive = true, Reload01 = 1f,
                Shield01 = 1f, PowerMode = PowerPreset.Balanced
            };
        }

        /// <summary>陨石按 seed 生成，存档只存 seed。静止、有血量、可当掩体。</summary>
        private ObstacleSnapshot[] GenerateObstacles(BattleStartSpec spec)
        {
            bool hasBelt = spec.BattlefieldLayout != null && spec.BattlefieldLayout.IndexOf("asteroid", StringComparison.OrdinalIgnoreCase) >= 0;
            int count = hasBelt ? 12 : 0;
            var r = new ObstacleSnapshot[count];
            for (int i = 0; i < count; i++)
            {
                r[i] = new ObstacleSnapshot
                {
                    ObstacleId = new EntityId("obs." + spec.BattleId.ToString() + "." + i),
                    X = 120f + _rng.NextFloat() * 180f,
                    Y = -90f + _rng.NextFloat() * 180f,
                    RadiusKm = 4f + _rng.NextFloat() * 8f,
                    Health01 = 1f,
                    IsAlive = true
                };
            }
            return r;
        }

        public CommandResult Submit(CombatCommand command)
        {
            if (!_started) return CommandResult.Fail("NOT_INITIALIZED", "战斗未初始化");
            if (command is HelmCommand helm)
            {
                foreach (var s in _snapshot.Ships)
                    if (s.ShipId == command.ShipId)
                    { s.Speed = helm.Throttle; s.Heading += helm.Rudder; }
                return CommandResult.Ok();
            }
            if (command is PowerAllocationCommand power)
            {
                var check = BattleRules.ValidatePower(power);
                if (!check.Success) return check;
                _power = new PowerAllocation
                {
                    Propulsion = power.Propulsion,
                    Shield = power.Shield,
                    Weapons = power.Weapons,
                    Preset = power.Preset
                };
                foreach (var s in _snapshot.Ships) s.PowerMode = power.Preset;
                return CommandResult.Ok();
            }
            return CommandResult.Fail("UNSUPPORTED_COMMAND", "假实现不支持该命令: " + command.GetType().Name);
        }

        public void Step()
        {
            if (!_started || _result != null) return;
            _snapshot.Clock = _snapshot.Clock.Advance();

            // 护罩随能量投入恢复；交战档把更多能量给武器，护罩恢复慢。
            float regen = _power.Shield * 4f;
            foreach (var s in _snapshot.Ships)
                if (s.IsAlive) s.Shield01 = Math.Min(1f, s.Shield01 + regen * _spec.FixedStep);

            if (_snapshot.Clock.Tick < 5) return;

            bool allyWin = CountAlive(true) >= CountAlive(false);
            _snapshot.Phase = allyWin ? BattlePhase.Victory : BattlePhase.Defeat;

            var outcomes = new List<ShipBattleOutcome>();
            var lostAllies = new List<EntityId>();
            var destroyedEnemies = new List<EntityId>();
            foreach (var s in _snapshot.Ships)
            {
                bool lost = s.IsAlly != allyWin;
                if (!s.IsAlly && allyWin) destroyedEnemies.Add(s.ShipId);
                if (s.IsAlly && !allyWin) lostAllies.Add(s.ShipId);
                if (lost) { s.IsAlive = false; s.Health01 = 0f; }
                outcomes.Add(new ShipBattleOutcome
                {
                    ShipId = s.ShipId, IsLost = lost,
                    HullAfter01 = s.Health01, AmmoRemaining = 0
                });
            }

            _result = new BattleResult
            {
                BattleId = _spec.BattleId,
                Victory = allyWin,
                DestroyedEnemyIds = destroyedEnemies.ToArray(),
                LostAllyIds = lostAllies.ToArray(),
                ShipOutcomes = outcomes.ToArray(),
                ElapsedSeconds = _snapshot.Clock.Seconds,
                ObjectivesComplete = allyWin
            };
            _events.Add(new BattleEvent
            {
                Kind = allyWin ? "Victory" : "Defeat",
                At = _snapshot.Clock, Detail = allyWin ? "敌方编队被击溃" : "我方编队损失"
            });
        }

        private int CountAlive(bool ally)
        {
            int n = 0;
            foreach (var s in _snapshot.Ships) if (s.IsAlly == ally && s.IsAlive) n++;
            return n;
        }

        /// <summary>深拷贝。外部拿到的快照改了也不影响仿真内部状态。</summary>
        public BattleSnapshot ReadSnapshot()
        {
            if (_snapshot == null) return null;
            var ships = new ShipSnapshot[_snapshot.Ships.Length];
            for (int i = 0; i < ships.Length; i++)
            {
                var s = _snapshot.Ships[i];
                ships[i] = new ShipSnapshot
                {
                    ShipId = s.ShipId, DefinitionId = s.DefinitionId, IsAlly = s.IsAlly, Role = s.Role,
                    X = s.X, Y = s.Y, Heading = s.Heading, Speed = s.Speed,
                    Health01 = s.Health01, IsAlive = s.IsAlive, Reload01 = s.Reload01,
                    Shield01 = s.Shield01, PowerMode = s.PowerMode
                };
            }
            var proj = new ProjectileSnapshot[_snapshot.Projectiles.Length];
            for (int i = 0; i < proj.Length; i++)
                proj[i] = new ProjectileSnapshot
                { ProjectileId = _snapshot.Projectiles[i].ProjectileId,
                  X = _snapshot.Projectiles[i].X, Y = _snapshot.Projectiles[i].Y };
            var obs = new ObstacleSnapshot[_snapshot.Obstacles.Length];
            for (int i = 0; i < obs.Length; i++)
            {
                var o = _snapshot.Obstacles[i];
                obs[i] = new ObstacleSnapshot
                { ObstacleId = o.ObstacleId, X = o.X, Y = o.Y, RadiusKm = o.RadiusKm,
                  Health01 = o.Health01, IsAlive = o.IsAlive };
            }
            return new BattleSnapshot
            { Clock = _snapshot.Clock, Ships = ships, Projectiles = proj, Obstacles = obs, Phase = _snapshot.Phase };
        }

        public BattleEvent[] DrainEvents()
        {
            var r = _events.ToArray();
            _events.Clear();
            return r;
        }

        /// <summary>深拷贝。结果数组也是内部引用，不能让调用方改了再结算。</summary>
        public bool TryGetResult(out BattleResult result)
        {
            if (_result == null) { result = null; return false; }
            var outcomes = new ShipBattleOutcome[_result.ShipOutcomes.Length];
            for (int i = 0; i < outcomes.Length; i++)
                outcomes[i] = new ShipBattleOutcome
                {
                    ShipId = _result.ShipOutcomes[i].ShipId,
                    IsLost = _result.ShipOutcomes[i].IsLost,
                    HullAfter01 = _result.ShipOutcomes[i].HullAfter01,
                    AmmoRemaining = _result.ShipOutcomes[i].AmmoRemaining
                };
            result = new BattleResult
            {
                BattleId = _result.BattleId, Victory = _result.Victory,
                DestroyedEnemyIds = (EntityId[])_result.DestroyedEnemyIds.Clone(),
                LostAllyIds = (EntityId[])_result.LostAllyIds.Clone(),
                ShipOutcomes = outcomes,
                ElapsedSeconds = _result.ElapsedSeconds,
                ObjectivesComplete = _result.ObjectivesComplete
            };
            return true;
        }
    }
}
