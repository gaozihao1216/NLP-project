// 唯一知道所有具体实现的地方。业务模块之间互不知晓。
// 换假实现为真实现，只改这里，不碰 Application 和各模块。

using System.IO;
using StarPawn.Application;
using StarPawn.Combat.Content;
using StarPawn.Combat.Contracts;
using StarPawn.Combat.Domain;
using StarPawn.Economy.Domain;
using StarPawn.Fleet.Domain;
using StarPawn.Foundation;
using StarPawn.Infrastructure;
using StarPawn.Narrative.Domain;
using StarPawn.Navigation.Domain;
using StarPawn.Officer.Domain;
using StarPawn.Progression.Domain;
using StarPawn.Relations.Domain;

namespace StarPawn.Bootstrap
{
    public static class GameBootstrap
    {
        /// <summary>
        /// 内容根目录。Unity 里用 Application.streamingAssetsPath 之类传入；
        /// 独立运行时可传 content/ 的实际路径。
        /// </summary>
        public static CampaignFacade Create(bool highCommandCoversSupply = true, string contentRoot = null)
        {
            var services = new GameServices
            {
                Navigation = new FakeNavigationService(new EntityId("sys.kaila")),
                Fleet = new FakeFleetService(),
                Economy = new FakeEconomyService(highCommandCoversSupply),
                Progression = new FakeProgressionService(),
                Relations = new FakeRelationsService(),
                Narrative = new FakeNarrativeRuntime(),
                Combat = new CombatRuntime(),
                Ships = ResolveShips(contentRoot),
                Advisor = new RuleBasedOfficer(),
                TacticalHints = new RuleBasedOfficer() as StarPawn.Officer.Contracts.ITacticalHintProvider,
                Rewards = string.IsNullOrEmpty(contentRoot) ? null : new JsonMissionRewardSource(contentRoot)
            };
            return new CampaignFacade(services);
        }

        /// <summary>
        /// 舰体配置源。有内容根就读 content/ships，没有就用内置镜像。
        /// 装配期不吞错误：目录里一个舰都没读出来时退回内置表（不让编辑器里点开战斗直接崩），
        /// 但那是「配置没生效」的状态，先用 ValidateContent 查清楚再跑数值。
        /// </summary>
        private static IShipStatsSource ResolveShips(string contentRoot)
        {
            if (string.IsNullOrEmpty(contentRoot)) return ShipCatalog.Builtin;

            var catalog = ShipCatalog.LoadDirectory(Path.Combine(contentRoot, "ships"), out _);
            return catalog.Count > 0 ? (IShipStatsSource)catalog : ShipCatalog.Builtin;
        }

        /// <summary>内容校验。返回空数组表示通过；调用方决定是报错还是只打日志。</summary>
        public static string[] ValidateContent(string contentRoot)
        {
            return new BasicContentValidator().Validate(contentRoot);
        }
    }
}
