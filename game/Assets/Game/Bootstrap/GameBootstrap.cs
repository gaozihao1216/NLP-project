// 唯一知道所有具体实现的地方。业务模块之间互不知晓。
// 换假实现为真实现，只改这里，不碰 Application 和各模块。

using StarPawn.Application;
using StarPawn.Combat.Domain;
using StarPawn.Economy.Domain;
using StarPawn.Fleet.Domain;
using StarPawn.Foundation;
using StarPawn.Narrative.Domain;
using StarPawn.Navigation.Domain;
using StarPawn.Officer.Domain;
using StarPawn.Progression.Domain;
using StarPawn.Relations.Domain;

namespace StarPawn.Bootstrap
{
    public static class GameBootstrap
    {
        public static CampaignFacade Create(bool highCommandCoversSupply = true)
        {
            var services = new GameServices
            {
                Navigation = new FakeNavigationService(new EntityId("sys.kaila")),
                Fleet = new FakeFleetService(),
                Economy = new FakeEconomyService(highCommandCoversSupply),
                Progression = new FakeProgressionService(),
                Relations = new FakeRelationsService(),
                Narrative = new FakeNarrativeRuntime(),
                Combat = new FakeCombatRuntime(),
                Advisor = new RuleBasedOfficer(),
                TacticalHints = new RuleBasedOfficer() as StarPawn.Officer.Contracts.ITacticalHintProvider
            };
            return new CampaignFacade(services);
        }
    }
}
