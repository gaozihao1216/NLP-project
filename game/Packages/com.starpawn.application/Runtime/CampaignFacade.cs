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
    }

    /// <summary>应用层门面，供表现层调用。表现层不接触任何 Domain。</summary>
    public sealed class CampaignFacade
    {
        private readonly GameServices _s;
        private readonly HashSet<string> _settledBattleIds = new HashSet<string>();
        private readonly List<BattleSummary> _submittedBattles = new List<BattleSummary>();
        private string _activeMissionId = "mission.first_contact";
        private EntityId _playerFlagshipId = new EntityId("ship.flag.001");

        public CampaignFacade(GameServices services) { _s = services; }

        public CommandResult StartCampaign() => _s.Narrative != null ? CommandResult.Ok()
            : CommandResult.Fail("NO_NARRATIVE", "剧情模块未装配");

        public CommandResult TravelToSystem(EntityId destination)
        {
            // 跨模块编排：移动本身由 Navigation 负责；补给扣费由 Economy 的规则策略决定，
            // Navigation 不知道资源，也不知道高层代管这回事。
            bool paidByHighCommand;
            var supply = _s.Economy.SettleSupply(20, out paidByHighCommand);
            if (!supply.Success) return supply;
            return _s.Navigation.TravelTo(destination);
        }

        public BattleStartSpec LaunchBattle(EntityId[] enemyShipIds)
        {
            // 战斗只接收准备好的输入。不传军事身份、军功、剧情对象进去。
            var fleet = _s.Fleet.Read();
            var allies = new List<FleetEntry>();
            int i = 0;
            foreach (var ship in fleet.Ships)
            {
                if (!ship.IsInFleet) continue;
                float heading = i * 45f;
                allies.Add(new FleetEntry
                {
                    ShipId = ship.ShipId, DefinitionId = ship.DefinitionId,
                    Role = ship.AssignedRole, Spawn = new SpawnPoint(i * 30f, 0f, heading)
                });
                i++;
            }
            var enemies = new List<FleetEntry>();
            for (int j = 0; j < enemyShipIds.Length; j++)
                enemies.Add(new FleetEntry
                {
                    ShipId = enemyShipIds[j], DefinitionId = new ContentId("ship.a_patrol"),
                    Role = SquadRole.Escort, Spawn = new SpawnPoint(300f + j * 25f, 120f, 180f)
                });

            var spec = new BattleStartSpec
            {
                BattleId = new EntityId("battle." + Guid.NewGuid().ToString("N").Substring(0, 8)),
                Allies = allies.ToArray(), Enemies = enemies.ToArray(),
                BattlefieldLayout = "asteroid_belt_01",
                Objective = new BattleObjective { Kind = "DestroyAll", TimeLimitSeconds = 300f },
                RandomSeed = 20261122, FixedStep = 1f / 30f
            };
            _s.Combat.Initialize(spec);
            return spec;
        }

        /// <summary>
        /// 战斗结算。正确性约束：BattleId 只结算一次；先在候选状态里验证并算好全部更新，
        /// 全部成功后才提交；提交完成后剧情才读取新事实推进。单机用一次性提交即可，不需要数据库事务。
        /// </summary>
        public CommandResult SettleBattle(EntityId battleId)
        {
            BattleResult result;
            if (!_s.Combat.TryGetResult(out result)) return CommandResult.Fail("BATTLE_NOT_FINISHED", "战斗尚未结束");
            if (result.BattleId != battleId) return CommandResult.Fail("BATTLE_ID_MISMATCH", "战斗 ID 不匹配");
            if (_settledBattleIds.Contains(battleId.Value)) return CommandResult.Fail("ALREADY_SETTLED", "该战斗已结算");

            // ---- 候选阶段：全部验证，不提交 ----
            if (_s.Fleet.Read() == null || _s.Progression.Read() == null)
                return CommandResult.Fail("INVALID_STATE", "战略状态不可用");

            int meritDelta = result.Victory ? 35 : 5;
            int creditDelta = result.Victory ? 200 : 40;

            // ---- 提交阶段：按顺序执行，各模块只做自己的事 ----
            var step = _s.Fleet.ApplyBattleDamage(result.LostAllyIds, new float[result.LostAllyIds.Length]);
            if (!step.Success) return step;
            step = _s.Economy.Grant(new ResourceAmount { Kind = ResourceKind.Credits, Amount = creditDelta }, "战斗奖励");
            if (!step.Success) return step;
            step = _s.Progression.AwardMerit(meritDelta, result.Victory ? "战斗胜利" : "战斗参与");
            if (!step.Success) return step;
            step = _s.Relations.ApplyEffect(new EntityId("player"), new EntityId("npc.aomusi"),
                RelationKind.Loyalty, result.Victory ? 5 : -5);
            if (!step.Success) return step;

            // 记录已结算，防止重复发奖；这条必须在提交成功后写入。
            _settledBattleIds.Add(battleId.Value);
            _submittedBattles.Add(new BattleSummary
            {
                BattleId = battleId, Victory = result.Victory,
                DestroyedCount = result.DestroyedEnemyIds != null ? result.DestroyedEnemyIds.Length : 0
            });

            // 提交后再让剧情读取已落定的事实。
            var update = _s.Narrative.Evaluate(BuildFacts());
            return CommandResult.Ok();
        }

        public CommandResult ChooseStoryOption(StoryChoiceId choiceId)
        {
            var facts = BuildFacts();
            var decision = _s.Narrative.Choose(choiceId, facts);
            if (!decision.IsLegal) return CommandResult.Fail("CHOICE_ILLEGAL", decision.DenyReason);

            // 剧情只提出效果请求，应用层分派给对应模块校验执行。
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
            _s.Narrative.Evaluate(BuildFacts());
            return CommandResult.Ok();
        }

        public CommandResult PerformPoliticalAction(EntityId actor, string actionId)
        {
            string reason;
            var check = _s.Relations.CanPerform(actor, actionId, out reason);
            return check.Success ? check : CommandResult.Fail("POLITICAL_BLOCKED", reason);
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
