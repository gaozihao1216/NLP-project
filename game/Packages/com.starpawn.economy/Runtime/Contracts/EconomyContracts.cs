// 经济契约：账户、库存、补给。不知道军衔也不知道剧情节点。
using System;
using StarPawn.Foundation;

namespace StarPawn.Economy.Contracts
{
    public enum ResourceKind { Credits, Alloy, Energy, RareMetal }

    public sealed class ResourceAmount
    {
        public ResourceKind Kind;
        public int Amount;
    }

    public sealed class EconomySnapshot
    {
        public ResourceAmount[] Balances;
        public bool CanPaySupply;
    }

    public interface IEconomyService
    {
        EconomySnapshot Read();
        CommandResult Pay(ResourceAmount cost, string reason);
        CommandResult Grant(ResourceAmount reward, string reason);
        // 资源托管策略：联邦/银鹰前期由高层支付补给，家族自行支付。
        CommandResult SettleSupply(int supplyCost, out bool paidByHighCommand);
    }
}
