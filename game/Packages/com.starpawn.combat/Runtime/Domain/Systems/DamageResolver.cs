// 伤害结算的唯一入口。所有通道都从这里进，但**响应必须按通道分支**：
// 光束不吃跳弹与入射角，投射体吃厚甲。把四种物理机制塞进一套通用公式，
// 最后会得到一个谁都算不清、谁都不敢改的中间层。
//
// 结算顺序（与设计稿 5.1 一致，不要重排）：
//   0 判面 → 1 护盾 DR（电不够先削）→ 2 过载缓冲 → 3 装甲 → 4 结构
//
// 第 2 步只统计「护盾实际吸收量」：背面挨打时盾没接住什么，就不该过载。
// 这条口径决定了单舰能不能触发过载，也决定了饱和战术的收益。

using StarPawn.Combat.Contracts;
using StarPawn.Foundation;

namespace StarPawn.Combat.Domain.Systems
{
    /// <summary>一次命中的结算事实。HUD 与调试输出读它，不要在别处重算 DR。</summary>
    public readonly struct HitOutcome
    {
        public readonly ShieldFace Face;
        /// <summary>实际生效的护盾 DR（已含转向损耗与供电不足的削减）。</summary>
        public readonly float ShieldDr;
        public readonly float ArmorDr;
        public readonly float Absorbed;
        public readonly float ToHull;
        public readonly bool BrokeShield;
        public readonly bool Destroyed;

        public HitOutcome(ShieldFace face, float shieldDr, float armorDr,
            float absorbed, float toHull, bool brokeShield, bool destroyed)
        {
            Face = face; ShieldDr = shieldDr; ArmorDr = armorDr;
            Absorbed = absorbed; ToHull = toHull;
            BrokeShield = brokeShield; Destroyed = destroyed;
        }
    }

    public static class DamageResolver
    {
        /// <summary>
        /// 结算一次命中。incomingDir 是「从目标指向威胁来源」的单位向量，
        /// 由调用方按弹道算好——结算层不猜方向。
        /// </summary>
        public static HitOutcome ResolveHit(CombatWorld w, CombatShip target, EntityId source,
            DamageChannel channel, float amount, Vec3 incomingDir)
        {
            if (target == null || !target.IsAlive || target.Stats == null || amount <= 0f)
                return new HitOutcome(ShieldFace.Front, 0f, 0f, 0f, 0f, false, false);

            float now = w.Clock.Seconds;
            var profile = target.Stats.Shield;

            ShieldFace face = ShieldSystem.FaceOf(target, incomingDir);
            float drS = ShieldSystem.EffectiveDr(w, target, face);

            // 先削 DR，再用削过的 DR 算吸收，最后按吸收付费。
            // 全舰记账：敌舰与护航舰同样付电，所以「盾硬不硬」也取决于它自己的储能。
            if (drS > 0f && profile.DrainPerDamage > 0f)
            {
                float cost = amount * drS * profile.DrainPerDamage;
                if (target.Energy < cost)
                {
                    float scale = cost > 1e-6f ? target.Energy / cost : 0f;
                    drS *= scale;
                    target.Energy = 0f;
                }
                else
                {
                    target.Energy -= cost;
                }
            }

            float absorbed = amount * drS;
            if (absorbed > amount) absorbed = amount;
            if (absorbed < 0f) absorbed = 0f;

            // 过载统计用 absorbed；本击是否破盾不影响本击的 DR（见 ShieldSystem 文件头第 3 条）。
            bool broke = absorbed > 0f && ShieldSystem.AccumulateOverload(w, target, absorbed, now);

            float passed = amount - absorbed;
            float drA = target.Stats.Armor.DrFor(channel);
            passed *= 1f - drA;
            if (passed < 0f) passed = 0f;

            target.LastHitTime = now;
            target.Hull -= passed;
            if (target.Hull < 0f) target.Hull = 0f;

            bool destroyed = !target.IsAlive;

            if (destroyed)
            {
                w.Emit("Destroyed", source, target.Id, "击沉");
            }
            else
            {
                w.Emit("Hit", source, target.Id,
                    ShieldSystem.FaceName(face) +
                    " 盾挡 " + drS.ToString("0.00") +
                    " 吸收 " + absorbed.ToString("0") +
                    " 装甲挡 " + drA.ToString("0.00") +
                    " 入舰体 " + passed.ToString("0") +
                    " 舰体 " + target.Health01.ToString("0.00") +
                    " 池 " + target.Buffer01.ToString("0.00"));
            }

            return new HitOutcome(face, drS, drA, absorbed, passed, broke, destroyed);
        }
    }
}
