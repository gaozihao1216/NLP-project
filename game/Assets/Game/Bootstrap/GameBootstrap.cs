// 唯一知道所有具体实现的地方。业务模块之间互不知晓。
// 换假实现为真实现，只改这里，不碰 Application 和各模块。

using System;
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
        /// <para>
        /// 传了路径就表示「配置必须生效」：读不到、读错、一个舰都没解析出来都会抛
        /// <see cref="InvalidOperationException"/>，不会悄悄退回内置数值。
        /// 不传路径才走内置镜像，那是显式选择（编辑器里还没有内容、纯演示）。
        /// </para>
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
        /// 舰体配置源。
        ///
        /// 分成两种截然不同的情形，不要混：
        ///   · 没给 contentRoot —— 这是「明确选择不读配置」，用内置镜像，不算兜底；
        ///   · 给了 contentRoot —— 那就必须读到、且必须全对，任何错误直接抛。
        ///
        /// 旧实现把加载错误 `out _` 丢掉，全部失败时悄悄退回内置表：调参者改了 JSON、
        /// 战斗里却还是旧数值，而且没有任何一处会说「你的配置没生效」。这比崩溃难查得多。
        /// 内置表只服务于显式不传路径的测试与演示，不作为「路径给了但读坏了」的退路。
        /// </summary>
        private static IShipStatsSource ResolveShips(string contentRoot)
        {
            if (string.IsNullOrEmpty(contentRoot)) return ShipCatalog.Builtin;

            string shipsDir = Path.Combine(contentRoot, "ships");
            string[] errors;
            var catalog = ShipCatalog.LoadDirectory(shipsDir, out errors);

            if (errors.Length > 0)
                throw new InvalidOperationException(
                    "舰体配置读取失败（" + shipsDir + "）：\n  " + string.Join("\n  ", errors));

            if (catalog.Count == 0)
                throw new InvalidOperationException("舰体配置目录里没有任何可用舰体：" + shipsDir);

            return catalog;
        }

        /// <summary>内容校验。返回空数组表示通过；调用方决定是报错还是只打日志。</summary>
        public static string[] ValidateContent(string contentRoot)
        {
            return new BasicContentValidator().Validate(contentRoot);
        }
    }
}
