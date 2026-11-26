// 战场容器：唯一的状态所有者。
// 它只管持有与增删查，不含任何规则——规则全在 Systems/ 里。
// 这样系统之间不需要互相引用，也就没有「谁先改状态」的隐式依赖，
// 执行顺序只在 CombatRuntime.Step 一处固定，确定性才守得住。

using System.Collections.Generic;
using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain
{
    public sealed class CombatWorld
    {
        public BattleStartSpec Spec;
        public SimClock Clock;
        /// <summary>
        /// 全场唯一随机源。消费顺序固定，否则同 seed 结果会分叉。
        /// 默认给一个 seed=0 的实例而不是留 null：手工构造世界（测试、灰盒探针）常常不走
        /// CombatRuntime.Initialize，只要有人开一枪就会在散布里 NRE。正式开战由 Initialize 按种子覆盖。
        /// </summary>
        public DeterministicRandom Rng = new DeterministicRandom(0);

        public readonly List<CombatShip> Ships = new List<CombatShip>();
        public readonly List<Projectile> Shells = new List<Projectile>();
        public readonly List<Obstacle> Rocks = new List<Obstacle>();

        public BattlePhase Phase = BattlePhase.Running;
        public readonly List<BattleEvent> Events = new List<BattleEvent>();

        /// <summary>滚转姿态回平（可选层，默认关闭）。太空没有「上」，所以不默认开启。</summary>
        public bool RollLeveling;

        /// <summary>
        /// 转向损耗开关（默认开）：高速自转时护盾场不稳，DR 按 1 − 0.25×负荷 打折。
        /// 留成开关是因为灰盒要开/关各跑一次——不动玩家行为就该删掉这条规则。
        /// </summary>
        public bool TurnLossEnabled = true;

        private int _nextShellSerial;
        private int _nextObstacleSerial;

        public float Dt => Spec != null ? Spec.FixedStep : 0f;

        public string NextShellId() => "shell." + (_nextShellSerial++);
        public string NextObstacleId() => Spec.BattleId + ".rock." + (_nextObstacleSerial++);

        public CombatShip FindShip(EntityId id)
        {
            for (int i = 0; i < Ships.Count; i++)
                if (Ships[i].Id == id) return Ships[i];
            return null;
        }

        /// <summary>
        /// 记一条事件。shot 只为 Fire 事件准备：结构化载荷与 Detail 文案并行，
        /// 表现层读前者、副官提示读后者，两边不用互相猜。
        /// </summary>
        public void Emit(string kind, EntityId source, EntityId target, string detail, TurretShot shot = null)
        {
            Events.Add(new BattleEvent
            {
                Kind = kind,
                SourceId = source,
                TargetId = target,
                At = Clock,
                Detail = detail,
                Shot = shot
            });
        }
    }
}
