using System;
using StarPawn.Economy.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Economy.Domain
{
    public sealed class FakeEconomyService : IEconomyService, IStateCheckpoint
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
            if (cost.Amount < 0) return CommandResult.Fail("NEGATIVE_AMOUNT", "支付金额不能为负");
            if (!Can(cost.Kind, cost.Amount)) return CommandResult.Fail("INSUFFICIENT", "资源不足: " + reason);
            Deduct(cost.Kind, cost.Amount); return CommandResult.Ok();
        }
        public CommandResult Grant(ResourceAmount reward, string reason)
        {
            if (reward.Amount < 0) return CommandResult.Fail("NEGATIVE_AMOUNT", "奖励金额不能为负");
            int cur = Get(reward.Kind);
            if (cur > int.MaxValue - reward.Amount) return CommandResult.Fail("OVERFLOW", "资源溢出");
            Add(reward.Kind, reward.Amount); return CommandResult.Ok();
        }
        public CommandResult SettleSupply(int supplyCost, out bool paidByHighCommand)
        {
            paidByHighCommand = _highCommandCoversSupply;
            if (_highCommandCoversSupply) return CommandResult.Ok();
            if (supplyCost < 0) return CommandResult.Fail("NEGATIVE_AMOUNT", "补给费用不能为负");
            return Pay(new ResourceAmount { Kind = ResourceKind.Credits, Amount = supplyCost }, "补给");
        }

        private int Get(ResourceKind k) => k == ResourceKind.Credits ? _credits
            : k == ResourceKind.Alloy ? _alloy : k == ResourceKind.Energy ? _energy : _rare;
        private bool Can(ResourceKind k, int a) => Get(k) >= a;
        private void Deduct(ResourceKind k, int a) { switch (k) {
            case ResourceKind.Credits: _credits -= a; break; case ResourceKind.Alloy: _alloy -= a; break;
            case ResourceKind.Energy: _energy -= a; break; case ResourceKind.RareMetal: _rare -= a; break; } }
        private void Add(ResourceKind k, int a) { switch (k) {
            case ResourceKind.Credits: _credits += a; break; case ResourceKind.Alloy: _alloy += a; break;
            case ResourceKind.Energy: _energy += a; break; case ResourceKind.RareMetal: _rare += a; break; } }

        public object CaptureState() => new int[] { _credits, _alloy, _energy, _rare };
        public void RestoreState(object state)
        {
            var a = state as int[];
            if (a == null || a.Length != 4) return;
            _credits = a[0]; _alloy = a[1]; _energy = a[2]; _rare = a[3];
        }
    }
}
