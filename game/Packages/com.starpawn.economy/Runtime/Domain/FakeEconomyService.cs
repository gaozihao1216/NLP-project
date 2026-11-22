using System;
using StarPawn.Economy.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Economy.Domain
{
    public sealed class FakeEconomyService : IEconomyService
    {
        private int _credits = 1000, _alloy = 200, _energy = 500, _rare = 50;
        private readonly bool _highCommandCoversSupply;
        public FakeEconomyService(bool highCommandCoversSupply) { _highCommandCoversSupply = highCommandCoversSupply; }

        public EconomySnapshot Read() => new EconomySnapshot
        {
            Balances = new[] {
                new ResourceAmount { Kind = ResourceKind.Credits, Amount = _credits },
                new ResourceAmount { Kind = ResourceKind.Alloy, Amount = _alloy },
                new ResourceAmount { Kind = ResourceKind.Energy, Amount = _energy },
                new ResourceAmount { Kind = ResourceKind.RareMetal, Amount = _rare }
            },
            CanPaySupply = _highCommandCoversSupply || _credits >= 20
        };

        public CommandResult Pay(ResourceAmount cost, string reason)
        {
            if (!Can(cost.Kind, cost.Amount)) return CommandResult.Fail("INSUFFICIENT", "资源不足: " + reason);
            Deduct(cost.Kind, cost.Amount); return CommandResult.Ok();
        }
        public CommandResult Grant(ResourceAmount reward, string reason)
        {
            Add(reward.Kind, reward.Amount); return CommandResult.Ok();
        }
        public CommandResult SettleSupply(int supplyCost, out bool paidByHighCommand)
        {
            paidByHighCommand = _highCommandCoversSupply;
            if (_highCommandCoversSupply) return CommandResult.Ok();
            return Pay(new ResourceAmount { Kind = ResourceKind.Credits, Amount = supplyCost }, "补给");
        }

        private bool Can(ResourceKind k, int a) => k == ResourceKind.Credits ? _credits >= a
            : k == ResourceKind.Alloy ? _alloy >= a : k == ResourceKind.Energy ? _energy >= a : _rare >= a;
        private void Deduct(ResourceKind k, int a) { switch (k) {
            case ResourceKind.Credits: _credits -= a; break; case ResourceKind.Alloy: _alloy -= a; break;
            case ResourceKind.Energy: _energy -= a; break; case ResourceKind.RareMetal: _rare -= a; break; } }
        private void Add(ResourceKind k, int a) { switch (k) {
            case ResourceKind.Credits: _credits += a; break; case ResourceKind.Alloy: _alloy += a; break;
            case ResourceKind.Energy: _energy += a; break; case ResourceKind.RareMetal: _rare += a; break; } }
    }
}
