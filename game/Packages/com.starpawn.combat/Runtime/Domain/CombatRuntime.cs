// 真实战斗仿真入口。对外只暴露 ICombatRuntime / IBattleSession，
// 内部按固定顺序驱动一组无状态系统；FakeCombatRuntime 保留给战略流程与契约测试。

using System;
using System.Collections.Generic;
using StarPawn.Combat.Contracts;
using StarPawn.Combat.Domain.Systems;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain
{
    public sealed class CombatRuntime : ICombatRuntime
    {
        private CombatWorld _w;
        private BattleResult _result;

        /// <summary>待落地的命令。Submit 只入队，Step 开头统一应用——避免「命令中途改状态」破坏确定性。</summary>
        private readonly List<CombatCommand> _pending = new List<CombatCommand>();

        public BattleStartSpec Spec => _w?.Spec;

        public void Initialize(BattleStartSpec spec)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            var check = BattleRules.ValidateSpec(spec);
            if (!check.Success) throw new ArgumentException(check.Error.ToString(), nameof(spec));

            var w = new CombatWorld
            {
                Spec = spec,
                Clock = new SimClock(0, spec.FixedStep),
                Rng = new DeterministicRandom(spec.RandomSeed)
            };

            foreach (var e in spec.Allies) if (e.IsLaunchable) w.Ships.Add(MakeShip(e, true));
            foreach (var e in spec.Enemies) if (e.IsLaunchable) w.Ships.Add(MakeShip(e, false));

            SpawnAsteroids(w, spec);

            _w = w;
            _result = null;
            _pending.Clear();
        }

        private static CombatShip MakeShip(FleetEntry e, bool ally)
        {
            var s = new CombatShip
            {
                Id = e.ShipId,
                DefinitionId = e.DefinitionId,
                IsAlly = ally,
                Role = e.Role,
                SquadIndex = e.SquadIndex,
                Stats = e.Stats,
                Pos = new Vec3(e.Spawn.X, e.Spawn.Y, e.Spawn.Z),
                Attitude = Quat.FromEulerDeg(e.Spawn.Yaw, e.Spawn.Pitch, e.Spawn.Roll),
                // 战损继承就在这里落地：上一场的完整度直接决定本场初始血量。
                Hull = e.Stats.MaxHull * e.InheritedHull01,
                Shield = e.Stats.ShieldCapacity,
                ReloadTimer = 0f
            };
            s.PrevPos = s.Pos;
            return s;
        }

        /// <summary>陨石只在有小行星带的地图生成；位置由 seed 决定，存档只存 seed。</summary>
        private static void SpawnAsteroids(CombatWorld w, BattleStartSpec spec)
        {
            bool hasBelt = spec.BattlefieldLayout != null &&
                spec.BattlefieldLayout.IndexOf("asteroid", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasBelt) return;

            for (int i = 0; i < 12; i++)
            {
                float radius = 2f + w.Rng.NextFloat() * 4f;
                var rock = new Obstacle
                {
                    Id = new EntityId(w.NextObstacleId()),
                    Pos = new Vec3(
                        120f + w.Rng.NextFloat() * 180f,
                        -90f + w.Rng.NextFloat() * 180f,
                        -90f + w.Rng.NextFloat() * 180f),
                    RadiusKm = radius,
                    InitialHull = Obstacle.HullForRadius(radius)
                };
                rock.Hull = rock.InitialHull;
                w.Rocks.Add(rock);
            }
        }

        public CommandResult Submit(CombatCommand command)
        {
            if (_w == null) return CommandResult.Fail("NOT_INITIALIZED", "战斗未初始化");
            if (_result != null) return CommandResult.Fail("BATTLE_FINISHED", "战斗已结束");
            if (command == null) return CommandResult.Fail("NULL_COMMAND", "命令为空");

            if (command is HelmCommand helm)
            {
                var check = BattleRules.ValidateHelm(helm);
                if (!check.Success) return check;
                if (_w.FindShip(helm.ShipId) == null)
                    return CommandResult.Fail("NO_SUCH_SHIP", "没有该舰: " + helm.ShipId);
            }
            else if (command is PowerAllocationCommand power)
            {
                var check = BattleRules.ValidatePower(power);
                if (!check.Success) return check;
            }
            else if (command is FireCommand || command is AimCommand)
            {
                if (_w.FindShip(command.ShipId) == null)
                    return CommandResult.Fail("NO_SUCH_SHIP", "没有该舰: " + command.ShipId);
            }
            else
            {
                return CommandResult.Fail("UNSUPPORTED_COMMAND", "暂不支持该命令: " + command.GetType().Name);
            }

            _pending.Add(command);
            return CommandResult.Ok();
        }

        public void Step()
        {
            if (_w == null || _result != null) return;
            float dt = _w.Dt;
            if (dt <= 0f) return;

            // 1. 角速度输入按 tick 归零：操舵是「按住期间有效」，不是粘滞状态。
            //    档位（ThrustSetpoint）与飞控开关（FlightAssist）反过来是粘滞的，不清。
            for (int i = 0; i < _w.Ships.Count; i++)
                _w.Ships[i].AngularInput = Vec3.Zero;

            ApplyPendingCommands();

            // 2. 敌舰 AI 读上一 tick 末状态生成意图。
            SimpleEnemyController.Step(_w);

            // 3. 物理与武器，顺序固定。
            MovementSystem.Step(_w, dt);
            WeaponSystem.Step(_w, dt);
            ProjectileSystem.Step(_w, dt);
            DamageSystem.Step(_w, dt);
            ObjectiveSystem.Step(_w, dt);

            _w.Clock = _w.Clock.Advance();

            if (_w.Phase != BattlePhase.Running && _result == null)
                _result = ObjectiveSystem.BuildResult(_w);
        }

        private void ApplyPendingCommands()
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                var cmd = _pending[i];
                var ship = _w.FindShip(cmd.ShipId);
                if (ship == null || !ship.IsAlive) continue;

                if (cmd is HelmCommand helm)
                {
                    ship.ThrustSetpoint = Clamp(helm.Thrust, BattleRules.MinThrust, BattleRules.MaxThrust);
                    ship.AngularInput = new Vec3(
                        Clamp(helm.Pitch, -1f, 1f),
                        Clamp(helm.Yaw, -1f, 1f),
                        Clamp(helm.Roll, -1f, 1f));
                    ship.FlightAssist = helm.FlightAssist;
                }
                else if (cmd is AimCommand aim)
                {
                    ship.AimPoint = new Vec3(aim.WorldX, aim.WorldY, aim.WorldZ);
                    ship.HasAim = true;
                }
                else if (cmd is FireCommand)
                {
                    ship.FireRequested = true;
                }
                else if (cmd is PowerAllocationCommand power)
                {
                    _w.Power = new PowerAllocation
                    {
                        Propulsion = power.Propulsion,
                        Shield = power.Shield,
                        Weapons = power.Weapons,
                        Preset = power.Preset
                    };
                }
            }
            _pending.Clear();
        }

        private static float Clamp(float v, float min, float max)
            => v < min ? min : (v > max ? max : v);

        public BattleSnapshot ReadSnapshot()
        {
            if (_w == null) return null;
            var ships = new ShipSnapshot[_w.Ships.Count];
            for (int i = 0; i < ships.Length; i++)
            {
                var s = _w.Ships[i];
                float yaw, pitch, roll;
                s.Attitude.ToEulerDeg(out yaw, out pitch, out roll);
                ships[i] = new ShipSnapshot
                {
                    ShipId = s.Id,
                    DefinitionId = s.DefinitionId,
                    IsAlly = s.IsAlly,
                    Role = s.Role,
                    X = s.Pos.X, Y = s.Pos.Y, Z = s.Pos.Z,
                    Yaw = yaw, Pitch = pitch, Roll = roll,
                    Vx = s.Velocity.X, Vy = s.Velocity.Y, Vz = s.Velocity.Z,
                    PitchRate = s.AngularVelocity.X,
                    YawRate = s.AngularVelocity.Y,
                    RollRate = s.AngularVelocity.Z,
                    Speed = s.Velocity.Length,
                    Health01 = s.Health01,
                    IsAlive = s.IsAlive,
                    Reload01 = s.Reload01,
                    Shield01 = s.Shield01,
                    PowerMode = _w.Power.Preset
                };
            }

            int shellCount = 0;
            for (int i = 0; i < _w.Shells.Count; i++) if (_w.Shells[i].Alive) shellCount++;
            var shells = new ProjectileSnapshot[shellCount];
            int k = 0;
            for (int i = 0; i < _w.Shells.Count; i++)
            {
                var p = _w.Shells[i];
                if (!p.Alive) continue;
                shells[k++] = new ProjectileSnapshot { ProjectileId = p.Id, X = p.Pos.X, Y = p.Pos.Y, Z = p.Pos.Z };
            }

            var rocks = new ObstacleSnapshot[_w.Rocks.Count];
            for (int i = 0; i < rocks.Length; i++)
            {
                var o = _w.Rocks[i];
                rocks[i] = new ObstacleSnapshot
                {
                    ObstacleId = o.Id,
                    X = o.Pos.X, Y = o.Pos.Y, Z = o.Pos.Z,
                    RadiusKm = o.RadiusKm,
                    Health01 = o.InitialHull > 0f ? o.Hull / o.InitialHull : 0f,
                    IsAlive = o.IsAlive
                };
            }

            return new BattleSnapshot
            {
                Clock = _w.Clock,
                Ships = ships,
                Projectiles = shells,
                Obstacles = rocks,
                Phase = _w.Phase
            };
        }

        public BattleEvent[] DrainEvents()
        {
            if (_w == null) return new BattleEvent[0];
            var r = _w.Events.ToArray();
            _w.Events.Clear();
            return r;
        }

        /// <summary>深拷贝，避免调用方改了结果数组再拿去结算。</summary>
        public bool TryGetResult(out BattleResult result)
        {
            if (_result == null) { result = null; return false; }
            result = ObjectiveSystem.CloneResult(_result);
            return true;
        }
    }
}
