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

        private bool _inAtomic;

        /// <summary>门面自己的记账也要进检查点，否则回滚只还原服务、不还原记账。</summary>
        private sealed class FacadeCheckpoint
        {
            public HashSet<EntityId> Settled;
            public List<BattleSummary> Submitted;
            public EntityId ActiveBattleId;
            public string ActiveMissionId;
        }

        private FacadeCheckpoint CaptureFacade()
        {
            return new FacadeCheckpoint
            {
                Settled = new HashSet<EntityId>(_settledBattleIds),
                Submitted = new List<BattleSummary>(_submittedBattles),
                ActiveBattleId = _activeBattleId,
                ActiveMissionId = _activeMissionId
            };
        }

        private static void AddCheckpoint(List<KeyValuePair<IStateCheckpoint, object>> list, object service)
        {
            var cp = service as IStateCheckpoint;
            if (cp != null) list.Add(new KeyValuePair<IStateCheckpoint, object>(cp, cp.CaptureState()));
        }

        private List<KeyValuePair<IStateCheckpoint, object>> CaptureServices()
        {
            var list = new List<KeyValuePair<IStateCheckpoint, object>>();
            AddCheckpoint(list, _s.Navigation);
            AddCheckpoint(list, _s.Fleet);
            AddCheckpoint(list, _s.Economy);
            AddCheckpoint(list, _s.Progression);
            AddCheckpoint(list, _s.Relations);
            AddCheckpoint(list, _s.Narrative);
            return list;
        }

        /// <summary>
        /// 跨模块原子事务。单机同步执行：先给所有可回滚服务与门面打检查点，
        /// 任一步失败就全量还原，失败不留半份状态。
        /// 嵌套调用由最外层统一回滚，内层直接执行，避免重复检查点互相覆盖。
        /// </summary>
        private CommandResult Atomic(Func<CommandResult> work)
        {
            if (_inAtomic) return work();
            _inAtomic = true;
            try
            {
                var checkpoints = CaptureServices();
                var facade = CaptureFacade();
                var result = work();
                if (result.Success) return result;

                for (int i = checkpoints.Count - 1; i >= 0; i--)
                    checkpoints[i].Key.RestoreState(checkpoints[i].Value);
                _settledBattleIds.Clear();
                foreach (var id in facade.Settled) _settledBattleIds.Add(id);
                _submittedBattles.Clear();
                _submittedBattles.AddRange(facade.Submitted);
                _activeBattleId = facade.ActiveBattleId;
                _activeMissionId = facade.ActiveMissionId;
                return result;
            }
            finally { _inAtomic = false; }
        }

        public CommandResult TravelToSystem(EntityId destination)
        {
            // 补给与移动在同一事务里：移动失败就把扣的补给退回去。
            return Atomic(() =>
            {
                bool paidByHighCommand;
                var supply = _s.Economy.SettleSupply(20, out paidByHighCommand);
                if (!supply.Success) return supply;
                return _s.Navigation.TravelTo(destination);
            });
        }

        /// <summary>启动战斗。出战舰按战备状态过滤，并把长期损伤继承进战斗属性。</summary>
        public BattleStartSpec LaunchBattle(EntityId[] enemyShipIds, EntityId? targetSystemId = null)
        {
            var fleet = _s.Fleet.Read();
            var currentSystem = _s.Navigation.Read().CurrentSystemId;
            var allies = new List<FleetEntry>();
            int i = 0;
            foreach (var ship in fleet.Ships)
            {
                if (!ship.IsInFleet || !_s.Fleet.IsLaunchable(ship.ShipId)) continue;
                // 三维出场：全队朝 +Z 列阵，横向按序错开。二维时代的「各自偏转 heading」在 3D 里
                // 会让舰首指向上级方向，已改为统一朝敌。
                float lateral = i * 30f - 15f;
                allies.Add(new FleetEntry
                {
                    ShipId = ship.ShipId, DefinitionId = ship.DefinitionId,
                    Role = ToCombatRole(ship.AssignedRole),
                    SquadIndex = ToSquadIndex(ship.AssignedRole),
                    Spawn = new SpawnPoint(lateral, 0f, 0f, 0f, 0f, 0f),
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
                    SquadIndex = j % BattleRules.MaxSquads,
                    // 敌舰摆在 +Z 前方 260 km、横向散开，正面朝向玩家；不再用旧的 (300,120) 斜上方点位。
                    Spawn = new SpawnPoint(-60f + j * 60f, 0f, 260f, 180f, 0f, 0f),
                    Stats = ResolvedShipStats.Default,
                    InheritedHull01 = 1f
                });

            var spec = new BattleStartSpec
            {
                BattleId = new EntityId("battle." + Guid.NewGuid().ToString("N").Substring(0, 8)),
                MissionId = _activeMissionId,
                OriginSystemId = currentSystem,
                TargetSystemId = targetSystemId ?? currentSystem,
                Allies = allies.ToArray(), Enemies = enemies.ToArray(),
                BattlefieldLayout = "asteroid_belt_01",
                Objective = new BattleObjective { Kind = "DestroyAll", TimeLimitSeconds = 300f },
                RandomSeed = 20261122, FixedStep = 1f / 30f
            };
            // 同一个校验器也被 Combat 用，非法输入在进战斗前就拦下。
            var check = BattleRules.ValidateSpec(spec);
            if (!check.Success) throw new InvalidOperationException("生成了非法战斗输入: " + check.Error);
            _activeBattleId = spec.BattleId;
            _s.Combat.Initialize(spec);
            return spec;
        }

        private static int ToSquadIndex(FleetSquadRole role)
        {
            switch (role)
            {
                case FleetSquadRole.Flagship: return 0;
                case FleetSquadRole.Escort: return 1;
                case FleetSquadRole.Scout: return 2;
                case FleetSquadRole.Carrier: return 3;
                default: return 1;
            }
        }

        // 机动参数的临时数值同步：与 content/ships 的 combat.maneuver 保持一致。
        // 真正的配置加载器是 B 的第二周任务，落地后这两行与本方法一起删掉。
        private static readonly ManeuverProfile CruiserManeuver =
            new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f);

        private static readonly ManeuverProfile CorvetteManeuver =
            new ManeuverProfile(4.0f, 45f, 27f, 81f, 180f, 108f, 324f);

        /// <summary>
        /// 由持久状态解析战斗属性。当前用固定基线，接内容加载后从 content/ships/ 读并按立方律换算。
        /// 半径与机动参数必须显式给：它们决定舰级手感，不能靠血量或转速反推。
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
                accuracy01: flagship ? 0.92f : 0.85f,
                hullRadiusKm: flagship ? 0.37f : 0.20f,
                maneuver: flagship ? CruiserManeuver : CorvetteManeuver,
                maxSpreadDeg: ResolvedShipStats.DefaultMaxSpreadDeg);
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

            // 任务 ID 来自 battle spec，不是门面当时的可变字段；中途切任务不会串奖励。
            var spec = _s.Combat.Spec;
            string missionId = spec != null && !string.IsNullOrEmpty(spec.MissionId)
                ? spec.MissionId : _activeMissionId;
            var missionReward = _s.Rewards != null ? _s.Rewards.Get(missionId) : null;
            int winMerit = missionReward != null ? missionReward.Merit : 35;
            int winCredits = missionReward != null ? missionReward.Credits : 200;
            int meritDelta = result.Victory ? winMerit : Math.Max(1, winMerit / 7);
            int creditDelta = result.Victory ? winCredits : Math.Max(1, winCredits / 5);

            var settled = Atomic(() =>
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

                // 胜利的领域后果：主张交战星系管辖权 + 缴获残骸。都在同一事务内。
                if (result.Victory && spec != null && !spec.TargetSystemId.IsEmpty)
                {
                    step = _s.Navigation.ClaimSystem(spec.TargetSystemId);
                    if (!step.Success) return step;
                    int wreckCount = result.DestroyedEnemyIds != null ? result.DestroyedEnemyIds.Length : 0;
                    if (wreckCount > 0)
                    {
                        step = _s.Economy.Grant(new ResourceAmount
                        { Kind = ResourceKind.Alloy, Amount = wreckCount * 10 }, "战场缴获");
                        if (!step.Success) return step;
                    }
                }

                // 结算标记与事实记账也在事务内；失败时 Atomic 会一并撤销。
                _settledBattleIds.Add(battleId);
                _submittedBattles.Add(new BattleSummary
                {
                    BattleId = battleId, Victory = result.Victory,
                    DestroyedCount = result.DestroyedEnemyIds != null ? result.DestroyedEnemyIds.Length : 0
                });

                // 剧情推进也进事务：它改的 node/used choices 由 Narrative 检查点保护。
                NarrativeUpdate update;
                step = _s.Narrative.Evaluate(BuildFacts(), out update);
                return step;
            });

            return settled;
        }

        public CommandResult ChooseStoryOption(StoryChoiceId choiceId)
        {
            var facts = BuildFacts();
            var decision = _s.Narrative.Choose(choiceId, facts);
            if (!decision.IsLegal) return CommandResult.Fail("CHOICE_ILLEGAL", decision.DenyReason);

            // 效果执行 + 节点迁移必须在同一事务里。CommitChoice 只在全部效果成功后调用。
            return Atomic(() =>
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
            return Atomic(() =>
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
