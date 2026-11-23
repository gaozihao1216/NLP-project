// 应用层：按用户行为组织工作流，避免把所有逻辑塞进巨大的 GameManager。
// 只依赖各模块 Contracts，不引用任何 Domain 实现——具体实现由 Bootstrap 注入。

using System;
using System.Collections.Generic;
using StarPawn.Combat.Contracts;
using StarPawn.Economy.Contracts;
using StarPawn.Fleet.Contracts;
using StarPawn.Foundation;
using StarPawn.Narrative.Contracts;
using StarPawn.Navigation.Contracts;
using StarPawn.Officer.Contracts;
using StarPawn.Progression.Contracts;
using StarPawn.Relations.Contracts;
using CombatSquadRole = StarPawn.Combat.Contracts.SquadRole;
using FleetSquadRole = StarPawn.Fleet.Contracts.SquadRole;

namespace StarPawn.Application
{
    /// <summary>各模块实例的集合。Bootstrap 负责装配，工作流只认接口。</summary>
    public sealed class GameServices
    {
        public INavigationService Navigation;
        public IFleetService Fleet;
        public IEconomyService Economy;
        public IProgressionService Progression;
        public IRelationsService Relations;
        public INarrativeRuntime Narrative;
        public ICombatRuntime Combat;
        public IStrategicAdvisor Advisor;
        public ITacticalHintProvider TacticalHints;
        /// <summary>任务奖励来源。为空时用代码里的安全默认值。</summary>
        public IMissionRewardSource Rewards;
    }

    /// <summary>应用层门面，供表现层调用。表现层不接触任何 Domain。</summary>
    public sealed class CampaignFacade
    {
        private readonly GameServices _s;
        private readonly HashSet<EntityId> _settledBattleIds = new HashSet<EntityId>();
        private readonly List<BattleSummary> _submittedBattles = new List<BattleSummary>();
        private EntityId _activeBattleId;
        private string _activeMissionId = "mission.first_contact";
        private readonly EntityId _playerFlagshipId = new EntityId("ship.flag.001");

        public CampaignFacade(GameServices services) { _s = services; }

        public CommandResult StartCampaign() => _s.Narrative != null ? CommandResult.Ok()
            : CommandResult.Fail("NO_NARRATIVE", "剧情模块未装配");

        // 两个模块各自保留 SquadRole，应用层显式映射，业务边界清楚。
        private static CombatSquadRole ToCombatRole(FleetSquadRole role)
        {
            switch (role)
            {
                case FleetSquadRole.Flagship: return CombatSquadRole.Flagship;
                case FleetSquadRole.Escort: return CombatSquadRole.Escort;
                case FleetSquadRole.Scout: return CombatSquadRole.Scout;
                case FleetSquadRole.Carrier: return CombatSquadRole.Carrier;
                default: return CombatSquadRole.Escort;
            }
        }

        /// <summary>
        /// 跨模块原子事务。单机同步执行，先给所有可回滚服务打检查点，
        /// 任一步失败就把全部状态还原到操作前。失败不留半份状态。
        /// </summary>
        private CommandResult Atomic(string opId, Func<CommandResult> work)
        {
            var checkpoints = new List<KeyValuePair<IStateCheckpoint, object>>();
            if (_s.Navigation is IStateCheckpoint nav) checkpoints.Add(new KeyValuePair<IStateCheckpoint, object>(nav, nav.CaptureState()));
            if (_s.Fleet is IStateCheckpoint fleet) checkpoints.Add(new KeyValuePair<IStateCheckpoint, object>(fleet, fleet.CaptureState()));
            if (_s.Economy is IStateCheckpoint eco) checkpoints.Add(new KeyValuePair<IStateCheckpoint, object>(eco, eco.CaptureState()));
            if (_s.Progression is IStateCheckpoint prog) checkpoints.Add(new KeyValuePair<IStateCheckpoint, object>(prog, prog.CaptureState()));
            if (_s.Relations is IStateCheckpoint rel) checkpoints.Add(new KeyValuePair<IStateCheckpoint, object>(rel, rel.CaptureState()));
            if (_s.Narrative is IStateCheckpoint nar) checkpoints.Add(new KeyValuePair<IStateCheckpoint, object>(nar, nar.CaptureState()));

            bool settledMarked = _settledBattleIds.Contains(_activeBattleId);
            int submittedCount = _submittedBattles.Count;

            var result = work();
            if (!result.Success)
            {
                for (int i = checkpoints.Count - 1; i >= 0; i--) checkpoints[i].Key.RestoreState(checkpoints[i].Value);
                // 回滚门面自己的记账
                if (!settledMarked) _settledBattleIds.Remove(_activeBattleId);
                while (_submittedBattles.Count > submittedCount) _submittedBattles.RemoveAt(_submittedBattles.Count - 1);
                return result;
            }
            return CommandResult.Ok();
        }

        public CommandResult TravelToSystem(EntityId destination)
        {
            // 补给与移动在同一事务里：移动失败就把扣的补给退回去。
            return Atomic("travel:" + destination, () =>
            {
                bool paidByHighCommand;
                var supply = _s.Economy.SettleSupply(20, out paidByHighCommand);
                if (!supply.Success) return supply;
                return _s.Navigation.TravelTo(destination);
            });
        }

        /// <summary>启动战斗。出战舰按战备状态过滤，并把长期损伤继承进战斗属性。</summary>
        public BattleStartSpec LaunchBattle(EntityId[] enemyShipIds)
        {
            var fleet = _s.Fleet.Read();
            var allies = new List<FleetEntry>();
            int i = 0;
            foreach (var ship in fleet.Ships)
            {
                if (!ship.IsInFleet || !_s.Fleet.IsLaunchable(ship.ShipId)) continue;
                float heading = i * 45f;
                allies.Add(new FleetEntry
                {
                    ShipId = ship.ShipId, DefinitionId = ship.DefinitionId,
                    Role = ToCombatRole(ship.AssignedRole),
                    Spawn = new SpawnPoint(i * 30f, 0f, heading),
                    Stats = ResolveStats(ship),
                    InheritedHull01 = ship.HullIntegrity01
                });
                i++;
            }
            var enemies = new List<FleetEntry>();
            for (int j = 0; j < enemyShipIds.Length; j++)
                enemies.Add(new FleetEntry
                {
                    ShipId = enemyShipIds[j], DefinitionId = new ContentId("ship.a_patrol"),
                    Role = CombatSquadRole.Escort,
                    Spawn = new SpawnPoint(300f + j * 25f, 120f, 180f),
                    Stats = ResolvedShipStats.Default,
                    InheritedHull01 = 1f
                });

            var spec = new BattleStartSpec
            {
                BattleId = new EntityId("battle." + Guid.NewGuid().ToString("N").Substring(0, 8)),
                Allies = allies.ToArray(), Enemies = enemies.ToArray(),
                BattlefieldLayout = "asteroid_belt_01",
                Objective = new BattleObjective { Kind = "DestroyAll", TimeLimitSeconds = 300f },
                RandomSeed = 20261122, FixedStep = 1f / 30f
            };
            _activeBattleId = spec.BattleId;
            _s.Combat.Initialize(spec);
            return spec;
        }

        /// <summary>
        /// 由持久状态解析战斗属性。当前用固定基线，接内容加载后从 content/ships/ 读并按立方律换算。
        /// </summary>
        private static ResolvedShipStats ResolveStats(OwnedShipState ship)
        {
            bool flagship = ship.AssignedRole == FleetSquadRole.Flagship;
            return new ResolvedShipStats(
                maxHull: flagship ? 800f : 200f,
                maxSpeedKps: flagship ? 20f : 32f,
                turnRateDps: flagship ? 20f : 45f,
                gunDamage: flagship ? 100f : 20f,
                gunRangeKm: flagship ? 320f : 180f,
                shellVelocityKps: flagship ? 300f : 220f,
                reloadSeconds: flagship ? 6f : 3.2f,
                shieldCapacity: flagship ? 300f : 80f,
                shieldRegenPerSec: flagship ? 8f : 3f,
                accuracy01: flagship ? 0.92f : 0.85f);
        }

        // ---- 战斗会话控制：表现层只通过门面推进，不直接持有 Combat ----
        public EntityId ActiveBattleId => _activeBattleId;
        public CommandResult SubmitCombatCommand(CombatCommand command) => _s.Combat.Submit(command);
        public void StepBattle() => _s.Combat.Step();
        public BattleSnapshot ReadBattleSnapshot() => _s.Combat.ReadSnapshot();
        public BattleEvent[] DrainBattleEvents() => _s.Combat.DrainEvents();
        public bool TryGetBattleResult(out BattleResult result) => _s.Combat.TryGetResult(out result);

        /// <summary>
        /// 战斗结算。约束：BattleId 只结算一次；整个工作流在同一事务里，
        /// 任一步失败全部回滚，包括已结算标记与剧情事实，重试不会重复发奖。
        /// </summary>
        public CommandResult SettleBattle(EntityId battleId)
        {
            BattleResult result;
            if (!_s.Combat.TryGetResult(out result)) return CommandResult.Fail("BATTLE_NOT_FINISHED", "战斗尚未结束");
            if (result.BattleId != battleId) return CommandResult.Fail("BATTLE_ID_MISMATCH", "战斗 ID 不匹配");
            if (_settledBattleIds.Contains(battleId)) return CommandResult.Fail("ALREADY_SETTLED", "该战斗已结算");
            if (_s.Fleet.Read() == null || _s.Progression.Read() == null)
                return CommandResult.Fail("INVALID_STATE", "战略状态不可用");

            // 奖励优先从内容配置读；没接内容源时退回安全默认，保证假实现独立可用。
            var missionReward = _s.Rewards != null ? _s.Rewards.Get(_activeMissionId) : null;
            int winMerit = missionReward != null ? missionReward.Merit : 35;
            int winCredits = missionReward != null ? missionReward.Credits : 200;
            int meritDelta = result.Victory ? winMerit : Math.Max(1, winMerit / 7);
            int creditDelta = result.Victory ? winCredits : Math.Max(1, winCredits / 5);

            var settled = Atomic("settle:" + battleId, () =>
            {
                // 舰队：逐舰回写长期损伤，击沉舰退出舰队；不再用「损失即零血」的粗糙写法。
                var reports = new List<ShipDamageReport>();
                if (result.ShipOutcomes != null)
                {
                    foreach (var o in result.ShipOutcomes)
                        reports.Add(new ShipDamageReport
                        { ShipId = o.ShipId, IsLost = o.IsLost, HullAfter01 = o.HullAfter01 });
                }
                else
                {
                    foreach (var id in result.LostAllyIds ?? new EntityId[0])
                        reports.Add(new ShipDamageReport { ShipId = id, IsLost = true, HullAfter01 = 0f });
                }
                var step = _s.Fleet.ApplyBattleDamage(reports.ToArray());
                if (!step.Success) return step;

                step = _s.Economy.Grant(new ResourceAmount { Kind = ResourceKind.Credits, Amount = creditDelta }, "战斗奖励");
                if (!step.Success) return step;
                step = _s.Progression.AwardMerit(meritDelta, result.Victory ? "战斗胜利" : "战斗参与");
                if (!step.Success) return step;
                // 晋升纳入结算，不再漏掉。
                step = _s.Progression.EvaluatePromotion();
                if (!step.Success) return step;
                step = _s.Relations.ApplyEffect(new EntityId("player"), new EntityId("npc.aomusi"),
                    RelationKind.Loyalty, result.Victory ? 5 : -5);
                if (!step.Success) return step;

                // 结算标记与事实记账也在事务内；失败时 Atomic 会一并撤销。
                _settledBattleIds.Add(battleId);
                _submittedBattles.Add(new BattleSummary
                {
                    BattleId = battleId, Victory = result.Victory,
                    DestroyedCount = result.DestroyedEnemyIds != null ? result.DestroyedEnemyIds.Length : 0
                });
                return CommandResult.Ok();
            });

            // 提交后让剧情读取已落定的事实推进节点。事务失败时这行不会执行。
            if (settled.Success) _s.Narrative.Evaluate(BuildFacts());
            return settled;
        }

        public CommandResult ChooseStoryOption(StoryChoiceId choiceId)
        {
            var facts = BuildFacts();
            var decision = _s.Narrative.Choose(choiceId, facts);
            if (!decision.IsLegal) return CommandResult.Fail("CHOICE_ILLEGAL", decision.DenyReason);

            // 效果执行 + 节点迁移必须在同一事务里。CommitChoice 只在全部效果成功后调用。
            return Atomic("choice:" + choiceId, () =>
            {
                foreach (var effect in decision.RequestedEffects ?? new StoryEffect[0])
                {
                    CommandResult r;
                    switch (effect.Kind)
                    {
                        case "GrantMerit": r = _s.Progression.AwardMerit(effect.Amount, "剧情选项"); break;
                        case "GrantResource": r = _s.Economy.Grant(new ResourceAmount { Kind = ResourceKind.Credits, Amount = effect.Amount }, "剧情选项"); break;
                        case "AdjustRelation": r = _s.Relations.ApplyEffect(new EntityId("player"), new EntityId(effect.TargetId), RelationKind.Suspicion, effect.Amount); break;
                        default: r = CommandResult.Fail("UNKNOWN_EFFECT", "未知效果: " + effect.Kind); break;
                    }
                    if (!r.Success) return r;
                }
                return _s.Narrative.CommitChoice(choiceId);
            });
        }

        public CommandResult PerformPoliticalAction(EntityId actor, string actionId)
        {
            string reason;
            var check = _s.Relations.CanPerform(actor, actionId, out reason);
            if (!check.Success) return CommandResult.Fail("POLITICAL_BLOCKED", reason);
            // 真正执行：举报成功提升对举报者的忠诚、降低对目标的猜疑。
            return Atomic("politics:" + actionId, () =>
                _s.Relations.ApplyEffect(actor, new EntityId("npc.aomusi"), RelationKind.Loyalty, 5));
        }

        public StoryView ReadStory() => _s.Narrative.ReadCurrentView();
        public NavigationSnapshot ReadNavigation() => _s.Navigation.Read();
        public FleetSnapshot ReadFleet() => _s.Fleet.Read();
        public EconomySnapshot ReadEconomy() => _s.Economy.Read();
        public ProgressionSnapshot ReadProgression() => _s.Progression.Read();
        public RelationsSnapshot ReadRelations() => _s.Relations.Read();
        public OfficerAdvice ReadAdvice()
            => _s.Advisor.Analyze(new VisibleIntel { KnownEnemyIds = new[] { new EntityId("enemy.001") },
                CurrentSystemId = _s.Navigation.Read().CurrentSystemId.ToString(), ObjectiveText = "歼灭巡逻编队" });

        private NarrativeFacts BuildFacts()
        {
            var eco = _s.Economy.Read();
            var prog = _s.Progression.Read();
            int credits = 0;
            foreach (var b in eco.Balances) if (b.Kind == ResourceKind.Credits) credits = b.Amount;
            return new NarrativeFacts
            {
                CurrentSystemId = _s.Navigation.Read().CurrentSystemId,
                RankId = prog.RankId, Credits = credits, Merit = prog.Merit,
                ActiveMissionId = _activeMissionId,
                MissionCompleted = new[] { _submittedBattles.Count > 0 },
                SubmittedBattles = _submittedBattles.ToArray()
            };
        }
    }
}
