// 关键契约测试：不依赖 Unity，dotnet run 即可。
// 覆盖 GPT 审查点名的场景：重复结算、中途失败回滚、重复剧情选择、战损继承、随机源上界、内容校验。

using System;
using System.Collections.Generic;
using System.IO;
using StarPawn.Application;
using StarPawn.Bootstrap;
using StarPawn.Combat.Content;
using StarPawn.Combat.Contracts;
using StarPawn.Combat.Domain;
using StarPawn.Combat.Domain.Systems;
using StarPawn.Economy.Contracts;
using StarPawn.Fleet.Contracts;
using StarPawn.Foundation;
using StarPawn.Relations.Domain;
using StarPawn.Relations.Contracts;
using StarPawn.Infrastructure;
using StarPawn.Narrative.Contracts;

internal static class Program
{
    private static int _passed, _failed;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine("[PASS] " + name); }
        else { _failed++; Console.WriteLine("[FAIL] " + name + " - " + detail); }
    }

    private static void FinishBattle(GameServices s, CampaignFacade f, EntityId battleId)
    {
        for (int i = 0; i < 10; i++) f.StepBattle();
    }

    /// <summary>护盾单测用的最小世界：只有时钟，没有编成与地形。</summary>
    private static CombatWorld ShieldWorld()
        => new CombatWorld { Clock = new SimClock(0, 1f / 30f) };

    private static CombatShip ShieldTarget(CombatWorld w, ResolvedShipStats stats, float energy = -1f)
    {
        var s = new CombatShip
        {
            Id = new EntityId("target"),
            Stats = stats,
            Pos = Vec3.Zero,
            ShieldBuffer = stats.Overload.BufferCapacity,
            // 全舰记账后每艘舰都有真实储能：默认满电，要测「电不够」就显式传 energy。
            Energy = energy >= 0f ? energy : stats.Power.Capacity,
            LastHitTime = -999f
        };
        w.Ships.Add(s);
        return s;
    }

    /// <summary>推进仿真时钟并让 ShieldSystem 走一遍：剪窗口、破盾恢复、延迟回充都靠它。</summary>
    private static void Advance(CombatWorld w, float seconds)
    {
        int ticks = (int)Math.Round(seconds / w.Clock.FixedStep);
        for (int i = 0; i < ticks; i++)
        {
            ShieldSystem.Step(w, w.Clock.FixedStep);
            w.Clock = w.Clock.Advance();
        }
    }

    /// <summary>
    /// 护盾测试基线：三面 DR 走设计稿，池 300 / 阈值 120，装甲按默认通道值。
    /// 缓冲池、阈值、DR 全显式给，避免测试跟着缺省推导漂移。
    /// </summary>
    private static ResolvedShipStats ShieldStats(float frontDr = 0.85f, float drainPerDamage = 0.12f,
        float armorKinetic = 0.35f)
        => new ResolvedShipStats(
            800f, 20f, 20f, 100f, 320f, 300f, 6f, 300f, 8f, 0.92f,
            0.37f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f,
            new PowerProfile(120f, 3.2f, 4.4f, 0.9f, 7f, 0.18f),
            new ArmorProfile(0.20f, armorKinetic),
            new ShieldProfile(frontDr, frontDr, frontDr, 0.25f, 4f, 8f, drainPerDamage, 0.3f),
            new OverloadProfile(120f, 1.5f, 300f, 6f));

    private static GameServices Build(out CampaignFacade facade)
    {
        var s = new GameServices
        {
            Navigation = new StarPawn.Navigation.Domain.FakeNavigationService(new EntityId("sys.kaila")),
            Fleet = new StarPawn.Fleet.Domain.FakeFleetService(),
            Economy = new StarPawn.Economy.Domain.FakeEconomyService(false),
            Progression = new StarPawn.Progression.Domain.FakeProgressionService(),
            Relations = new StarPawn.Relations.Domain.FakeRelationsService(),
            Narrative = new StarPawn.Narrative.Domain.FakeNarrativeRuntime(),
            Combat = new StarPawn.Combat.Domain.FakeCombatRuntime(),
            // 配置源用内置镜像：契约测试不该依赖磁盘上 content 是否存在。
            Ships = ShipCatalog.Builtin,
            Advisor = new StarPawn.Officer.Domain.RuleBasedOfficer()
        };
        facade = new CampaignFacade(s);
        return s;
    }

    private static int Credits(GameServices s)
    {
        foreach (var b in s.Economy.Read().Balances) if (b.Kind == ResourceKind.Credits) return b.Amount;
        return -1;
    }

    /// <summary>从 cwd 与程序目录各向上找 content/ships，兼容「仓库根运行」与「tools/SmokeTests 下运行」。</summary>
    private static string FindContentShipsDir()
    {
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "content", "ships");
                if (Directory.Exists(path)) return path;
            }
        }
        return null;
    }

    /// <summary>逐字段比对两份属性，返回第一个差异的描述；完全一致返回 null。</summary>
    private static string DiffStats(ResolvedShipStats a, ResolvedShipStats b)
    {
        var d = new List<string>();
        void Num(string name, float x, float y)
        {
            if (Math.Abs(x - y) > 1e-4f) d.Add(name + " " + x.ToString("0.####") + "≠" + y.ToString("0.####"));
        }

        Num("hp", a.MaxHull, b.MaxHull);
        Num("maxSpeed", a.MaxSpeedKps, b.MaxSpeedKps);
        Num("gunDamage", a.GunDamage, b.GunDamage);
        Num("gunRange", a.GunRangeKm, b.GunRangeKm);
        Num("shellVelocity", a.ShellVelocityKps, b.ShellVelocityKps);
        Num("reload", a.ReloadSeconds, b.ReloadSeconds);
        Num("accuracy", a.Accuracy01, b.Accuracy01);
        Num("hullRadius", a.HullRadiusKm, b.HullRadiusKm);
        Num("maxSpread", a.MaxSpreadDeg, b.MaxSpreadDeg);

        Num("thrust", a.Maneuver.MaxThrustKmS2, b.Maneuver.MaxThrustKmS2);
        Num("yawRate", a.Maneuver.YawRateDps, b.Maneuver.YawRateDps);
        Num("pitchRate", a.Maneuver.PitchRateDps, b.Maneuver.PitchRateDps);
        Num("rollRate", a.Maneuver.RollRateDps, b.Maneuver.RollRateDps);
        Num("yawAccel", a.Maneuver.YawAccelDps2, b.Maneuver.YawAccelDps2);
        Num("pitchAccel", a.Maneuver.PitchAccelDps2, b.Maneuver.PitchAccelDps2);
        Num("rollAccel", a.Maneuver.RollAccelDps2, b.Maneuver.RollAccelDps2);

        Num("powerCapacity", a.Power.Capacity, b.Power.Capacity);
        Num("powerRegen", a.Power.RegenPerSec, b.Power.RegenPerSec);
        Num("propulsionDrain", a.Power.PropulsionDrainPerSec, b.Power.PropulsionDrainPerSec);
        Num("turnDrain", a.Power.TurnDrainPerSec, b.Power.TurnDrainPerSec);
        Num("weaponDrain", a.Power.WeaponDrainPerShot, b.Power.WeaponDrainPerShot);
        Num("restart", a.Power.RestartFraction, b.Power.RestartFraction);

        Num("beamDr", a.Armor.BeamDr, b.Armor.BeamDr);
        Num("kineticDr", a.Armor.KineticDr, b.Armor.KineticDr);

        Num("frontDr", a.Shield.FrontDr, b.Shield.FrontDr);
        Num("sideDr", a.Shield.SideDr, b.Shield.SideDr);
        Num("rearDr", a.Shield.RearDr, b.Shield.RearDr);
        Num("turnLoss", a.Shield.TurnLossFactor, b.Shield.TurnLossFactor);
        Num("regenDelay", a.Shield.RegenDelaySeconds, b.Shield.RegenDelaySeconds);
        Num("bufferRegen", a.Shield.RegenPerSec, b.Shield.RegenPerSec);
        Num("drainPerDamage", a.Shield.DrainPerDamage, b.Shield.DrainPerDamage);
        Num("recoverFraction", a.Shield.RecoverFraction, b.Shield.RecoverFraction);

        Num("threshold", a.Overload.Threshold, b.Overload.Threshold);
        Num("window", a.Overload.WindowSeconds, b.Overload.WindowSeconds);
        Num("bufferCapacity", a.Overload.BufferCapacity, b.Overload.BufferCapacity);
        Num("breakSeconds", a.Overload.BreakSeconds, b.Overload.BreakSeconds);

        return d.Count == 0 ? null : string.Join(", ", d);
    }

    private static void Main()
    {
        Console.WriteLine("=== StarPawn 关键契约测试 ===\n");

        // 1. 重复结算不重复发奖
        {
            GameServices s; CampaignFacade f; s = Build(out f);
            var spec = f.LaunchBattle(new[] { new EntityId("enemy.001") });
            FinishBattle(s, f, spec.BattleId);
            var first = f.SettleBattle(spec.BattleId);
            int afterFirst = Credits(s);
            var second = f.SettleBattle(spec.BattleId);
            Check("重复结算被拒绝", first.Success && !second.Success && second.Error.Code == "ALREADY_SETTLED",
                "first=" + first.Success + " second=" + second.Error.Code);
            Check("重复结算不发第二份奖励", Credits(s) == afterFirst,
                "afterFirst=" + afterFirst + " now=" + Credits(s));
        }

        // 2. 中途失败全量回滚（关系写入失败 → 军功/资源也不该变）
        {
            GameServices s; CampaignFacade f; s = Build(out f);
            var spec = f.LaunchBattle(new[] { new EntityId("enemy.001") });
            FinishBattle(s, f, spec.BattleId);
            int creditsBefore = Credits(s);
            int meritBefore = s.Progression.Read().Merit;
            FakeRelationsService.ForceFailNextApplyEffect = true;
            var r = f.SettleBattle(spec.BattleId);
            Check("中途失败返回错误", !r.Success && r.Error.Code == "FORCED", r.Error.Code);
            Check("失败后资源回滚", Credits(s) == creditsBefore, "before=" + creditsBefore + " now=" + Credits(s));
            Check("失败后军功回滚", s.Progression.Read().Merit == meritBefore);
            // 回滚后重试应能成功（战斗未被标记为已结算）
            var retry = f.SettleBattle(spec.BattleId);
            Check("回滚后可重试成功", retry.Success, retry.Error.Code);
        }

        // 3. 剧情重复选择不重复发奖
        {
            GameServices s; CampaignFacade f; s = Build(out f);
            var spec = f.LaunchBattle(new[] { new EntityId("enemy.001") });
            FinishBattle(s, f, spec.BattleId);
            f.SettleBattle(spec.BattleId);
            f.ReadStory(); // 结算里的 Evaluate 已推进节点，这里只读
            int meritBefore = s.Progression.Read().Merit;
            var c1 = f.ChooseStoryOption(new StoryChoiceId("report_merit"));
            int afterFirst = s.Progression.Read().Merit;
            var c2 = f.ChooseStoryOption(new StoryChoiceId("report_merit"));
            Check("合法选择成功", c1.Success, c1.Error.Code);
            Check("重复选择被拒绝", !c2.Success, c2.Error.Code);
            Check("重复选择不发第二份军功", s.Progression.Read().Merit == afterFirst,
                "before=" + meritBefore + " after=" + afterFirst + " now=" + s.Progression.Read().Merit);
        }

        // 4. 剧情选项必须属于当前节点（初始节点不能提交战后选项）
        {
            GameServices s; CampaignFacade f; s = Build(out f);
            var r = f.ChooseStoryOption(new StoryChoiceId("report_merit"));
            Check("跨节点选项被拒绝", !r.Success && r.Error.Code == "CHOICE_ILLEGAL", r.Error.Code);
        }

        // 5. 战损继承：击沉舰下一场不能满血复活
        {
            GameServices s; CampaignFacade f; s = Build(out f);
            // 敌人多于我方 → 我方战败 → 两艘舰都被击沉
            var ids = new EntityId[6];
            for (int i = 0; i < ids.Length; i++) ids[i] = new EntityId("enemy." + i);
            var spec = f.LaunchBattle(ids);
            FinishBattle(s, f, spec.BattleId);
            f.SettleBattle(spec.BattleId);
            var fleet = f.ReadFleet();
            int launchable = 0;
            foreach (var sh in fleet.Ships) if (s.Fleet.IsLaunchable(sh.ShipId)) launchable++;
            Check("击沉舰退出舰队", launchable == 0, "launchable=" + launchable);
            var spec2 = f.LaunchBattle(new[] { new EntityId("enemy.x") });
            Check("无可用舰时不生成我方编成", spec2.Allies.Length == 0, "allies=" + spec2.Allies.Length);
        }

        // 6. 快照深拷贝：外部改快照不影响内部
        {
            GameServices s; CampaignFacade f; s = Build(out f);
            var fleet = f.ReadFleet();
            fleet.Ships[0].HullIntegrity01 = 0f;
            var again = f.ReadFleet();
            Check("舰队快照深拷贝", again.Ships[0].HullIntegrity01 > 0.99f,
                "hull=" + again.Ships[0].HullIntegrity01);
            var nav = f.ReadNavigation();
            nav.KnownSystems[0].IsVisited = false;
            Check("导航快照重建", f.ReadNavigation().KnownSystems[0].IsVisited);
        }

        // 7. DeterministicRandom 上界与零种子
        {
            var rng = new DeterministicRandom(247665088);
            bool outOfRange = false;
            for (int i = 0; i < 10000; i++)
            {
                int v = rng.NextInt(0, 10);
                if (v < 0 || v >= 10) { outOfRange = true; break; }
            }
            Check("NextInt 不返回上界", !outOfRange);
            var zero = new DeterministicRandom(0);
            Check("零种子不退化", zero.NextInt(0, 10) >= 0 && zero.NextInt(0, 10) < 10);
        }

        // 8. 内容校验：悬空引用被拦截、合法内容通过
        {
            string root = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../content"));
            var errors = new BasicContentValidator().Validate(root);
            Check("现有内容校验通过", errors.Length == 0, string.Join("; ", errors));
        }

        // 9. 内容驱动奖励：改 JSON 会改变结算数值
        {
            string root = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../content"));
            var src = new JsonMissionRewardSource(root);
            var rw = src.Get("mission.first_contact");
            Check("任务奖励从配置读取", rw != null && rw.Merit == 35 && rw.Credits == 200,
                rw == null ? "null" : rw.Merit + "/" + rw.Credits);
        }

        // 10. 事务基础：TravelTo 失败时补给不丢（用高成本场景间接验证原子包装存在）
        {
            GameServices s; CampaignFacade f; s = Build(out f);
            var before = Credits(s);
            var ok = f.TravelToSystem(new EntityId("sys.zhongzhou"));
            Check("航行事务成功", ok.Success);
            Check("高层代管关闭时补给被扣", Credits(s) == before - 20, "before=" + before + " now=" + Credits(s));
        }

        // 11. 战斗契约校验：四分队上限、能量非负、合法输入
        {
            GameServices s; CampaignFacade f; s = Build(out f);
            var spec = f.LaunchBattle(new[] { new EntityId("enemy.001") });
            Check("合法战斗输入通过校验", BattleRules.ValidateSpec(spec).Success);

            var bad = new BattleStartSpec
            {
                BattleId = new EntityId("battle.bad"),
                Allies = new[] { new FleetEntry
                {
                    ShipId = new EntityId("ship.x"), Stats = ResolvedShipStats.Default,
                    InheritedHull01 = 1f, SquadIndex = BattleRules.MaxSquads
                } },
                Enemies = new FleetEntry[0],
                FixedStep = 1f / 30f
            };
            Check("超过四分队被拒绝", BattleRules.ValidateSpec(bad).Error.Code == "SQUAD_OUT_OF_RANGE");
            Check("负能量投入被拒绝",
                !BattleRules.ValidatePower(new PowerAllocationCommand
                { ShipId = new EntityId("ship.x"), Propulsion = 1.2f, Shield = -0.2f, Weapons = 0f }).Success);
            Check("合法三路能量通过",
                BattleRules.ValidatePower(new PowerAllocationCommand
                { ShipId = new EntityId("ship.x"), Propulsion = 0.3f, Shield = 0.3f, Weapons = 0.4f }).Success);
            Check("缺少舰船 ID 的能量命令被拒绝",
                !BattleRules.ValidatePower(new PowerAllocationCommand
                { Propulsion = 0.3f, Shield = 0.3f, Weapons = 0.4f }).Success);
        }

        // 12. 剧情推进失败也必须整体回滚，且回滚后可重试
        {
            GameServices s; CampaignFacade f; s = Build(out f);
            var spec = f.LaunchBattle(new[] { new EntityId("enemy.001") });
            FinishBattle(s, f, spec.BattleId);
            int creditsBefore = Credits(s);
            int meritBefore = s.Progression.Read().Merit;
            string nodeBefore = f.ReadStory().NodeId;
            StarPawn.Narrative.Domain.FakeNarrativeRuntime.ForceFailNextEvaluate = true;
            var r = f.SettleBattle(spec.BattleId);
            Check("剧情推进失败导致结算失败", !r.Success && r.Error.Code == "EVALUATE_FORCED", r.Error.Code);
            Check("剧情失败后资源回滚", Credits(s) == creditsBefore);
            Check("剧情失败后军功回滚", s.Progression.Read().Merit == meritBefore);
            Check("剧情失败后节点回滚", f.ReadStory().NodeId == nodeBefore);
            Check("剧情失败后可重试成功", f.SettleBattle(spec.BattleId).Success);
        }

        // 13. 胜利后果：星系管辖 + 残骸缴获，都在同一事务内
        {
            GameServices s; CampaignFacade f; s = Build(out f);
            var target = new EntityId("sys.zhongzhou");
            var spec = f.LaunchBattle(new[] { new EntityId("enemy.001") }, target);
            int alloyBefore = 0;
            foreach (var b in s.Economy.Read().Balances) if (b.Kind == ResourceKind.Alloy) alloyBefore = b.Amount;
            FinishBattle(s, f, spec.BattleId);
            var r = f.SettleBattle(spec.BattleId);
            Check("胜利结算成功", r.Success, r.Error.Code);
            bool claimed = false;
            foreach (var sys in f.ReadNavigation().KnownSystems)
                if (sys.SystemId == target && sys.IsVisited) claimed = true;
            Check("胜利后主张星系管辖", claimed);
            int alloyAfter = 0;
            foreach (var b in s.Economy.Read().Balances) if (b.Kind == ResourceKind.Alloy) alloyAfter = b.Amount;
            Check("胜利后缴获残骸资源", alloyAfter > alloyBefore, "before=" + alloyBefore + " after=" + alloyAfter);
        }

        // 14. 战斗结果深拷贝：调用方改数组不能污染内部结果
        {
            GameServices s; CampaignFacade f; s = Build(out f);
            var spec = f.LaunchBattle(new[] { new EntityId("enemy.001") });
            FinishBattle(s, f, spec.BattleId);
            BattleResult first, second;
            f.TryGetBattleResult(out first);
            first.DestroyedEnemyIds[0] = new EntityId("tampered");
            f.TryGetBattleResult(out second);
            Check("战斗结果数组深拷贝", second.DestroyedEnemyIds[0] != new EntityId("tampered"));
        }

        // 15. 三维姿态与扫掠碰撞的数学基元
        {
            var q = StarPawn.Combat.Domain.Quat.FromEulerDeg(37f, -21f, 64f);
            float yaw, pitch, roll;
            q.ToEulerDeg(out yaw, out pitch, out roll);
            Check("四元数欧拉往返",
                Math.Abs(yaw - 37f) < 0.01f && Math.Abs(pitch + 21f) < 0.01f && Math.Abs(roll - 64f) < 0.01f,
                "yaw=" + yaw.ToString("0.00") + " pitch=" + pitch.ToString("0.00") + " roll=" + roll.ToString("0.00"));

            Vec3 fwd = StarPawn.Combat.Domain.Quat.FromEulerDeg(90f, 0f, 0f).Forward;
            Check("偏航 90° 后舰首指向 +X",
                Math.Abs(fwd.X - 1f) < 0.001f && Math.Abs(fwd.Z) < 0.001f, fwd.ToString());

            // 弹速 7 km/tick 的情形：起止点都在球外，但线段穿过 → 必须判命中
            float t;
            bool through = StarPawn.Combat.Domain.Systems.DamageSystem.SegmentSphere(
                new Vec3(0f, 0f, -5f), new Vec3(0f, 0f, 5f), 0.3f, out t);
            Check("扫掠碰撞：高速穿过也命中", through);

            bool graze = StarPawn.Combat.Domain.Systems.DamageSystem.SegmentSphere(
                new Vec3(0.5f, 0f, -5f), new Vec3(0.5f, 0f, 5f), 0.3f, out t);
            Check("扫掠碰撞：擦过不误判", !graze);
        }

        // 16. 反喷回正：线性减速到 0，不阶跃、不反向过冲
        {
            float omega = 45f;
            float prev = omega;
            bool monotonic = true;
            bool overshoot = false;
            int ticks = 0;
            while (omega != 0f && ticks < 300)
            {
                omega = StarPawn.Combat.Domain.Systems.MovementSystem.StepAxis(
                    omega, 0f, 45f, 180f, true, 1f / 30f);
                if (Math.Abs(omega) > Math.Abs(prev)) monotonic = false;
                if (omega * prev < 0f) overshoot = true;
                prev = omega;
                ticks++;
            }
            Check("松键后角速度减到 0", omega == 0f, "ticks=" + ticks);
            Check("回正过程单调、无反向过冲", monotonic && !overshoot, "ticks=" + ticks + " monotonic=" + monotonic);
            Check("回正耗时约 0.25 s", ticks >= 7 && ticks <= 9, "ticks=" + ticks);

            // 关掉飞控 = 纯牛顿：角速度必须保持
            float kept = 45f;
            for (int i = 0; i < 60; i++)
                kept = StarPawn.Combat.Domain.Systems.MovementSystem.StepAxis(
                    kept, 0f, 45f, 180f, false, 1f / 30f);
            Check("关飞控后角速度保持（纯牛顿）", Math.Abs(kept - 45f) < 0.001f, "omega=" + kept);
        }

        // 17. 提前量必须用相对速度：射手自己也在动
        {
            var shooterPos = new Vec3(0f, 0f, 0f);
            var shooterVel = new Vec3(0f, 0f, 32f);
            var targetPos = new Vec3(100f, 0f, 150f);
            var targetVel = new Vec3(-32f, 0f, 0f);
            Vec3 aim;
            bool ok = CombatMath.TryLeadPoint(shooterPos, shooterVel, targetPos, targetVel, 220f, out aim);
            Check("提前量可解", ok);

            // 按瞄准方向发射，让炮弹自己飞，检查是否与目标相遇
            Vec3 dir = (aim - shooterPos).Normalized;
            Vec3 shellVel = shooterVel + dir * 220f;
            float bestMiss = float.MaxValue;
            for (int i = 1; i <= 60; i++)
            {
                float time = i * 0.05f;
                Vec3 shellAt = shooterPos + shellVel * time;
                Vec3 targetAt = targetPos + targetVel * time;
                float miss = (shellAt - targetAt).Length;
                if (miss < bestMiss) bestMiss = miss;
            }
            Check("炮弹与移动目标相遇（误差 < 1 km）", bestMiss < 1f, "最小偏差 " + bestMiss.ToString("0.000") + " km");
        }

        // 18. 真实战斗仿真能推进、能开火、能造成伤害
        {
            var spec = new BattleStartSpec
            {
                BattleId = new EntityId("battle.smoke"),
                MissionId = "mission.smoke",
                OriginSystemId = new EntityId("sys.kaila"),
                TargetSystemId = new EntityId("sys.kaila"),
                Allies = new[]
                {
                    new FleetEntry
                    {
                        ShipId = new EntityId("ally.cruiser"), DefinitionId = new ContentId("ship.g_cruiser"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Flagship, SquadIndex = 0,
                        Spawn = new SpawnPoint(0f, 0f, 0f, 0f, 0f, 0f),
                        Stats = new ResolvedShipStats(800f, 20f, 20f, 100f, 320f, 300f, 6f, 300f, 8f, 0.92f,
                            0.37f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f),
                        InheritedHull01 = 1f
                    }
                },
                Enemies = new[]
                {
                    new FleetEntry
                    {
                        ShipId = new EntityId("enemy.patrol"), DefinitionId = new ContentId("ship.a_patrol"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Escort, SquadIndex = 1,
                        Spawn = new SpawnPoint(0f, 0f, 120f, 180f, 0f, 0f),
                        Stats = new ResolvedShipStats(120f, 32f, 45f, 20f, 180f, 220f, 3.2f, 80f, 3f, 0.85f,
                            0.20f, new ManeuverProfile(4.0f, 45f, 27f, 81f, 180f, 108f, 324f), 1f),
                        InheritedHull01 = 1f
                    }
                },
                BattlefieldLayout = "open_space",
                Objective = new BattleObjective { Kind = "DestroyAll", TimeLimitSeconds = 300f },
                RandomSeed = 7,
                FixedStep = 1f / 30f
            };

            var rt = new StarPawn.Combat.Domain.CombatRuntime();
            rt.Initialize(spec);
            bool sawFire = false;
            for (int i = 0; i < 600; i++)
            {
                rt.Step();
                foreach (var e in rt.DrainEvents())
                    if (e.Kind == "Fire") sawFire = true;
            }
            var snap = rt.ReadSnapshot();
            bool closedIn = snap.Ships[1].Z < 119f;
            bool hurt = snap.Ships[0].ShieldBroken || snap.Ships[0].Buffer01 < 1f || snap.Ships[0].Health01 < 1f;
            Check("真实战斗：敌舰开火", sawFire);
            Check("真实战斗：敌舰主动接近", closedIn, "z=" + snap.Ships[1].Z.ToString("0.0"));
            Check("真实战斗：命中造成掉血", hurt,
                "池=" + snap.Ships[0].Buffer01.ToString("0.00") + " 破盾=" + snap.Ships[0].ShieldBroken +
                " hull=" + snap.Ships[0].Health01.ToString("0.00"));
        }

        // 19. 能源：满推力持续消耗 → 耗尽锁推进 → 回充后恢复
        {
            // 小容量 + 低再生 + 高耗电，10 秒左右就能把电打空，不必真跑五分钟。
            var power = new PowerProfile(40f, 1.0f, 5.0f, 0f, 1f, 0.25f);
            var spec = new BattleStartSpec
            {
                BattleId = new EntityId("battle.energy"),
                MissionId = "mission.smoke",
                OriginSystemId = new EntityId("sys.kaila"),
                TargetSystemId = new EntityId("sys.kaila"),
                Allies = new[]
                {
                    new FleetEntry
                    {
                        ShipId = new EntityId("ally.flagship"), DefinitionId = new ContentId("ship.test"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Flagship, SquadIndex = 0,
                        Spawn = new SpawnPoint(0f, 0f, 0f, 0f, 0f, 0f),
                        Stats = new ResolvedShipStats(200f, 200f, 30f, 10f, 180f, 220f, 3f, 50f, 2f, 0.9f,
                            0.30f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f, power),
                        InheritedHull01 = 1f
                    }
                },
                Enemies = new[]
                {
                    new FleetEntry
                    {
                        ShipId = new EntityId("enemy.far"), DefinitionId = new ContentId("ship.test"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Escort, SquadIndex = 1,
                        Spawn = new SpawnPoint(0f, 0f, 500f, 180f, 0f, 0f),
                        Stats = new ResolvedShipStats(120f, 20f, 30f, 10f, 180f, 220f, 3f, 50f, 2f, 0.9f,
                            0.30f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f, power),
                        InheritedHull01 = 1f
                    }
                },
                BattlefieldLayout = "open_space",
                Objective = new BattleObjective { Kind = "DestroyAll", TimeLimitSeconds = 300f },
                RandomSeed = 11,
                FixedStep = 1f / 30f
            };

            var rt = new StarPawn.Combat.Domain.CombatRuntime();
            rt.Initialize(spec);
            Check("初始能源为满", rt.ReadSnapshot().Ships[0].Energy01 > 0.99f);

            // 限速故意设到 200，让这条只测能源：否则速度被钳在 20，熄火后「速度不变」就是假通过。
            var helm = new HelmCommand { ShipId = new EntityId("ally.flagship"), Thrust = 1f };
            bool sawEmpty = false;
            for (int i = 0; i < 450; i++)
            {
                rt.Submit(helm);
                rt.Step();
                foreach (var e in rt.DrainEvents()) if (e.Kind == "PowerEmpty") sawEmpty = true;
            }

            var locked = rt.ReadSnapshot().Ships[0];
            Check("满推力会把主舰能源打空", sawEmpty && locked.Energy01 < 0.25f,
                "energy=" + locked.Energy01.ToString("0.000"));
            Check("能源耗尽后推进被锁", locked.PropulsionLocked);

            // 熄火后继续提交满推：档位必须被当 tick 的能源结算清零，速度靠惯性保持。
            float speedAtLock = locked.Speed;
            for (int i = 0; i < 90; i++) { rt.Submit(helm); rt.Step(); }     // 熄火后再推 3 s
            var coasting = rt.ReadSnapshot().Ships[0];
            Check("熄火期间推力被强制归零", Math.Abs(coasting.Speed - speedAtLock) < 1e-3f,
                "Δv=" + (coasting.Speed - speedAtLock).ToString("0.0000") +
                " speed=" + coasting.Speed.ToString("0.000"));

            float energyAtLock = locked.Energy01;
            for (int i = 0; i < 600; i++) rt.Step();                          // 停推 20 s 回充
            var restored = rt.ReadSnapshot().Ships[0];
            Check("回充过阈值后推进恢复", !restored.PropulsionLocked, "energy=" + restored.Energy01.ToString("0.000"));
            Check("回充确实让能源回升", restored.Energy01 > energyAtLock + 0.3f,
                "lock=" + energyAtLock.ToString("0.000") + " now=" + restored.Energy01.ToString("0.000"));
        }

        // 20. 推力方向恒为舰体主轴：偏航 90° 朝 +X，加速必须落在 +X 上
        {
            var spec = new BattleStartSpec
            {
                BattleId = new EntityId("battle.thrust"),
                MissionId = "mission.smoke",
                OriginSystemId = new EntityId("sys.kaila"),
                TargetSystemId = new EntityId("sys.kaila"),
                Allies = new[]
                {
                    new FleetEntry
                    {
                        ShipId = new EntityId("ally.t"), DefinitionId = new ContentId("ship.test"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Flagship, SquadIndex = 0,
                        // 偏航 90° → 舰首指 +X，而不是世界 +Z
                        Spawn = new SpawnPoint(0f, 0f, 0f, 90f, 0f, 0f),
                        Stats = new ResolvedShipStats(200f, 20f, 30f, 10f, 180f, 220f, 3f, 50f, 2f, 0.9f,
                            0.30f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f),
                        InheritedHull01 = 1f
                    }
                },
                Enemies = new[]
                {
                    new FleetEntry
                    {
                        ShipId = new EntityId("enemy.t"), DefinitionId = new ContentId("ship.test"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Escort, SquadIndex = 1,
                        Spawn = new SpawnPoint(0f, 0f, 500f, 180f, 0f, 0f),
                        Stats = new ResolvedShipStats(120f, 20f, 30f, 10f, 180f, 220f, 3f, 50f, 2f, 0.9f,
                            0.30f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f),
                        InheritedHull01 = 1f
                    }
                },
                BattlefieldLayout = "open_space",
                Objective = new BattleObjective { Kind = "DestroyAll", TimeLimitSeconds = 300f },
                RandomSeed = 12,
                FixedStep = 1f / 30f
            };

            var rt = new StarPawn.Combat.Domain.CombatRuntime();
            rt.Initialize(spec);
            rt.Submit(new HelmCommand { ShipId = new EntityId("ally.t"), Thrust = 1f });
            rt.Step();

            var s = rt.ReadSnapshot().Ships[0];
            // 2.5 km/s² × 1/30 s ≈ 0.083 km/s，全部落在 +X
            Check("推力沿舰体主轴而非世界轴", s.Vx > 0.07f && Math.Abs(s.Vz) < 1e-3f,
                "v=(" + s.Vx.ToString("0.000") + ", " + s.Vy.ToString("0.000") + ", " + s.Vz.ToString("0.000") + ")");

            // 档位是粘滞的：不再发命令也会继续推，20 s 后必然顶到限速（20 km/s）。
            for (int i = 0; i < 600; i++) rt.Step();
            var capped = rt.ReadSnapshot().Ships[0];
            Check("航速被钳在舰体限速", Math.Abs(capped.Speed - 20f) < 0.01f, "speed=" + capped.Speed.ToString("0.000"));
            Check("限速不改变推力方向", Math.Abs(capped.Vx - 20f) < 0.05f && Math.Abs(capped.Vz) < 1e-3f,
                "v=(" + capped.Vx.ToString("0.00") + ", " + capped.Vz.ToString("0.000") + ")");
        }

        // 21. 目标锁定：解提前量、超距与友军拒绝
        {
            var spec = new BattleStartSpec
            {
                BattleId = new EntityId("battle.lock"),
                MissionId = "mission.smoke",
                OriginSystemId = new EntityId("sys.kaila"),
                TargetSystemId = new EntityId("sys.kaila"),
                Allies = new[]
                {
                    new FleetEntry
                    {
                        ShipId = new EntityId("ally.l"), DefinitionId = new ContentId("ship.test"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Flagship, SquadIndex = 0,
                        Spawn = new SpawnPoint(0f, 0f, 0f, 0f, 0f, 0f),
                        Stats = new ResolvedShipStats(200f, 20f, 30f, 10f, 180f, 220f, 3f, 50f, 2f, 0.9f,
                            0.30f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f),
                        InheritedHull01 = 1f
                    },
                    new FleetEntry
                    {
                        ShipId = new EntityId("ally.wing"), DefinitionId = new ContentId("ship.test"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Escort, SquadIndex = 1,
                        Spawn = new SpawnPoint(10f, 0f, -10f, 0f, 0f, 0f),
                        Stats = new ResolvedShipStats(120f, 20f, 30f, 10f, 180f, 220f, 3f, 50f, 2f, 0.9f,
                            0.30f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f),
                        InheritedHull01 = 1f
                    }
                },
                Enemies = new[]
                {
                    new FleetEntry
                    {
                        ShipId = new EntityId("enemy.near"), DefinitionId = new ContentId("ship.test"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Escort, SquadIndex = 2,
                        Spawn = new SpawnPoint(0f, 0f, 100f, 180f, 0f, 0f),
                        Stats = new ResolvedShipStats(120f, 20f, 30f, 10f, 180f, 220f, 3f, 50f, 2f, 0.9f,
                            0.30f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f),
                        InheritedHull01 = 1f
                    },
                    new FleetEntry
                    {
                        ShipId = new EntityId("enemy.far"), DefinitionId = new ContentId("ship.test"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Escort, SquadIndex = 3,
                        Spawn = new SpawnPoint(0f, 0f, 600f, 180f, 0f, 0f),
                        Stats = new ResolvedShipStats(120f, 20f, 30f, 10f, 180f, 220f, 3f, 50f, 2f, 0.9f,
                            0.30f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f),
                        InheritedHull01 = 1f
                    }
                },
                BattlefieldLayout = "open_space",
                Objective = new BattleObjective { Kind = "DestroyAll", TimeLimitSeconds = 300f },
                RandomSeed = 13,
                FixedStep = 1f / 30f
            };

            var rt = new StarPawn.Combat.Domain.CombatRuntime();
            rt.Initialize(spec);
            var ally = new EntityId("ally.l");

            var lockOk = rt.Submit(new LockTargetCommand { ShipId = ally, TargetId = new EntityId("enemy.near") });
            Check("锁定射程内敌舰成功", lockOk.Success, lockOk.Error.Code);

            // 命令是入队语义（T03 定死的）：Submit 只把命令放进队列，下一个固定步开始才生效。
            // 所以「提交后立刻读快照」必然读不到，必须 Step 一帧再断言。
            rt.Step();
            var snap = rt.ReadSnapshot();
            Check("下一帧快照记录锁定目标", snap.Ships[0].LockedTargetId == new EntityId("enemy.near"));
            Check("锁定的同一帧就有瞄准点", snap.Ships[0].HasAim);

            // 打几 tick，让目标横向移动，再看提前量是否领先于目标当前位置
            for (int i = 0; i < 30; i++) rt.Step();
            snap = rt.ReadSnapshot();
            var aim = new Vec3(snap.Ships[0].AimX, snap.Ships[0].AimY, snap.Ships[0].AimZ);
            ShipSnapshot tgt = null;
            foreach (var sh in snap.Ships) if (sh.ShipId == new EntityId("enemy.near")) tgt = sh;
            var tgtPos = new Vec3(tgt.X, tgt.Y, tgt.Z);
            Check("锁定后解出提前量（瞄准点领先目标）", (aim - tgtPos).Length > 0.05f,
                "偏差 " + (aim - tgtPos).Length.ToString("0.00") + " km");

            var tooFar = rt.Submit(new LockTargetCommand { ShipId = ally, TargetId = new EntityId("enemy.far") });
            Check("超出锁定距离被拒绝", !tooFar.Success && tooFar.Error.Code == "LOCK_OUT_OF_RANGE", tooFar.Error.Code);

            var friendly = rt.Submit(new LockTargetCommand { ShipId = ally, TargetId = new EntityId("ally.wing") });
            Check("锁定友军被拒绝", !friendly.Success && friendly.Error.Code == "FRIENDLY_TARGET", friendly.Error.Code);

            var clear = rt.Submit(new LockTargetCommand { ShipId = ally });
            rt.Step();     // 同样是入队语义：解除锁定也要等下一帧
            Check("空目标等于解除锁定",
                clear.Success && rt.ReadSnapshot().Ships[0].LockedTargetId.IsEmpty);
        }

        // 22. 判面：三档 DR 必须真的改变穿透量（这是「打哪一面」有意义的全部前提）
        {
            var w = ShieldWorld();
            var s = ShieldTarget(w, ShieldStats());
            var src = new EntityId("attacker");

            // incomingDir 是「从目标指向威胁来源」：+Z 表示打脸，−Z 表示打屁股
            var front = DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 100f, new Vec3(0f, 0f, 1f));
            var side = DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 100f, new Vec3(1f, 0f, 0f));
            var rear = DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 100f, new Vec3(0f, 0f, -1f));

            Check("正面/侧面/背面判对",
                front.Face == ShieldFace.Front && side.Face == ShieldFace.Side && rear.Face == ShieldFace.Rear,
                front.Face.ToString() + "/" + side.Face.ToString() + "/" + rear.Face.ToString());
            Check("正面盾挡 0.85、侧面 0.55、背面 0.15",
                Math.Abs(front.ShieldDr - 0.85f) < 1e-3f &&
                Math.Abs(side.ShieldDr - 0.55f) < 1e-3f &&
                Math.Abs(rear.ShieldDr - 0.15f) < 1e-3f,
                front.ShieldDr.ToString("0.000") + "/" + side.ShieldDr.ToString("0.000") + "/" + rear.ShieldDr.ToString("0.000"));
            Check("三面穿透量单调递增（背面最痛）",
                front.ToHull < side.ToHull && side.ToHull < rear.ToHull,
                front.ToHull.ToString("0.00") + " < " + side.ToHull.ToString("0.00") + " < " + rear.ToHull.ToString("0.00"));
            // 100 × (1−0.85) × (1−0.35) = 9.75
            Check("装甲按通道减伤：正面入舰体 9.75",
                Math.Abs(front.ToHull - 9.75f) < 0.01f, front.ToHull.ToString("0.000"));
        }

        // 23. 转向损耗：满转时盾变薄，且这条规则可以整体关掉
        {
            var w = ShieldWorld();
            var s = ShieldTarget(w, ShieldStats());

            float rest = ShieldSystem.EffectiveDr(w, s, ShieldFace.Front);
            s.AngularVelocity = new Vec3(0f, 20f, 0f);                  // 巡洋舰满偏航 20 °/s
            float turning = ShieldSystem.EffectiveDr(w, s, ShieldFace.Front);
            w.TurnLossEnabled = false;
            float off = ShieldSystem.EffectiveDr(w, s, ShieldFace.Front);

            Check("静止时正面 DR 是常驻值", Math.Abs(rest - 0.85f) < 1e-3f, rest.ToString("0.000"));
            Check("满转时正面 DR 掉四分之一", Math.Abs(turning - 0.6375f) < 1e-3f, turning.ToString("0.000"));
            Check("转向损耗把正面拉到侧舷档位附近（设计意图）", Math.Abs(turning - 0.55f) < 0.1f);
            Check("关掉转向损耗后回到常驻值", Math.Abs(off - 0.85f) < 1e-3f, off.ToString("0.000"));
        }

        // 24. 抗伤耗电：电不够时先削 DR，不是继续免费扛
        {
            var w = ShieldWorld();
            // 应付电 = 100 × 0.85 × 0.12 = 10.2，实际只有 1 点
            var s = ShieldTarget(w, ShieldStats(), energy: 1f);
            var hit = DamageResolver.ResolveHit(w, s, new EntityId("attacker"),
                DamageChannel.Kinetic, 100f, new Vec3(0f, 0f, 1f));

            Check("电不够时护盾变薄", hit.ShieldDr < 0.1f && hit.Absorbed < 9f,
                "dr=" + hit.ShieldDr.ToString("0.000") + " absorbed=" + hit.Absorbed.ToString("0.00"));
            Check("削 DR 后能源恰好清零", s.Energy < 1e-4f, s.Energy.ToString("0.0000"));
            Check("少挡的部分转成穿透", hit.ToHull > 55f, hit.ToHull.ToString("0.00"));
        }

        // 25. 过载：单舰一轮打不破，双舰同窗口三轮破；破盾那一击不穿透
        {
            var w = ShieldWorld();
            // 三面 DR 拉满、装甲为零：只测过载账，不让 DR 与装甲干扰轮数
            var s = ShieldTarget(w, ShieldStats(frontDr: 1f, drainPerDamage: 0f, armorKinetic: 0f));
            var src = new EntityId("attacker");
            var dir = new Vec3(0f, 0f, 1f);

            // 每轮两发 130 = 窗口内 260，超额 140
            DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 130f, dir);
            DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 130f, dir);
            Check("第 1 轮齐射：缓冲池 300 → 160", Math.Abs(s.ShieldBuffer - 160f) < 0.01f,
                s.ShieldBuffer.ToString("0.0"));

            Advance(w, 2f);          // 窗口清空，但 4 s 回充延迟未到
            DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 130f, dir);
            DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 130f, dir);
            Check("第 2 轮齐射：只剩 20", Math.Abs(s.ShieldBuffer - 20f) < 0.01f,
                s.ShieldBuffer.ToString("0.0"));

            float hullBefore = s.Hull;
            Advance(w, 2f);
            DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 130f, dir);
            DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 130f, dir);
            Check("第 3 轮才破盾（v0.3「两轮必破」是错的）", s.ShieldBroken, "buffer=" + s.ShieldBuffer.ToString("0.0"));
            Check("打空池的那一击仍按已算 DR 结算，不穿透", Math.Abs(s.Hull - hullBefore) < 1e-3f,
                "Δhull=" + (hullBefore - s.Hull).ToString("0.00"));

            // 破盾从下一击开始：DR 归零，全额进舰体
            float hullAtBreak = s.Hull;
            var afterBreak = DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 100f, dir);
            Check("破盾后 DR 归零且吸收为零", afterBreak.ShieldDr <= 0f && afterBreak.Absorbed <= 0f);
            Check("破盾后伤害全额进舰体", Math.Abs((hullAtBreak - s.Hull) - 100f) < 0.01f,
                (hullAtBreak - s.Hull).ToString("0.00"));

            Advance(w, 6.05f);
            Check("失效 6 s 后解除破盾", !s.ShieldBroken);
            Check("恢复只回三成（210 点仍是空的）", Math.Abs(s.ShieldBuffer - 90f) < 3f,
                s.ShieldBuffer.ToString("0.0"));
        }

        // 26. 延迟回充：4 s 未受击才启动，早一秒都不回
        {
            var w = ShieldWorld();
            var s = ShieldTarget(w, ShieldStats(frontDr: 1f, drainPerDamage: 0f, armorKinetic: 0f));
            var src = new EntityId("attacker");
            var dir = new Vec3(0f, 0f, 1f);

            DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 130f, dir);
            DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 130f, dir);

            Advance(w, 3f);
            Check("3 s 内不回充", Math.Abs(s.ShieldBuffer - 160f) < 0.01f, s.ShieldBuffer.ToString("0.0"));

            Advance(w, 2.5f);
            Check("过了 4 s 开始回充", s.ShieldBuffer > 165f, s.ShieldBuffer.ToString("0.0"));
        }

        // 27. 全舰记账：护航舰与敌舰同样扣电（T06 删掉了「只算主舰」的开关）
        {
            var w = ShieldWorld();
            var stats = ShieldStats();

            CombatShip Make(string id, bool ally)
            {
                var s = new CombatShip
                {
                    Id = new EntityId(id),
                    Stats = stats,
                    IsAlly = ally,
                    Role = StarPawn.Combat.Contracts.SquadRole.Escort,
                    // 血量必须给：IsAlive 就是 Hull > 0，不设 Hull 的舰在 PowerSystem 里会被当死人跳过，
                    // 于是「护航舰掉电」这条断言测的是一个根本没参与结算的世界。
                    Hull = stats.MaxHull,
                    Energy = stats.Power.Capacity,
                    ThrustSetpoint = 1f,        // 满推：耗电最高的档
                    LastHitTime = -999f
                };
                w.Ships.Add(s);
                return s;
            }

            var ally = Make("ally.escort", true);
            var enemy = Make("enemy.01", false);
            float allyStart = ally.Energy, enemyStart = enemy.Energy;

            for (int i = 0; i < 300; i++)     // 10 s
            {
                PowerSystem.Step(w, w.Clock.FixedStep);
                w.Clock = w.Clock.Advance();
            }

            Check("护航舰满推 10 s 会掉电（不再按满电处理）",
                ally.Energy < allyStart - 5f, allyStart.ToString("0.0") + " → " + ally.Energy.ToString("0.0"));
            Check("敌舰同样记账", enemy.Energy < enemyStart - 5f,
                enemyStart.ToString("0.0") + " → " + enemy.Energy.ToString("0.0"));
        }

        // 28. 配置加载：JSON → ResolvedShipStats，且内置镜像必须与磁盘配置一致
        {
            string shipsDir = FindContentShipsDir();
            Check("找得到 content/ships", shipsDir != null, "找不到则本机 cwd 不在仓库内");

            if (shipsDir != null)
            {
                string[] errors;
                var disk = ShipCatalog.LoadDirectory(shipsDir, out errors);
                Check("三份舰体配置全部解析通过", errors.Length == 0 && disk.Count == 3,
                    "count=" + disk.Count + " errors=" + string.Join("; ", errors));

                ResolvedShipStats cruiser;
                bool ok = disk.TryGet("ship.g_cruiser", out cruiser);
                Check("读到的数值与 JSON 一致（关键项抽查）",
                    ok &&
                    Math.Abs(cruiser.MaxHull - 800f) < 1e-3f &&
                    Math.Abs(cruiser.GunDamage - 100f) < 1e-3f &&
                    Math.Abs(cruiser.Accuracy01 - 0.92f) < 1e-4f &&
                    Math.Abs(cruiser.Shield.FrontDr - 0.85f) < 1e-4f &&
                    Math.Abs(cruiser.Overload.Threshold - 120f) < 1e-3f &&
                    Math.Abs(cruiser.Power.TurnDrainPerSec - 0.9f) < 1e-4f &&
                    Math.Abs(cruiser.Maneuver.MaxThrustKmS2 - 2.5f) < 1e-4f,
                    ok ? cruiser.MaxHull + "/" + cruiser.GunDamage + "/" + cruiser.Shield.FrontDr : "缺少 ship.g_cruiser");

                // 内置镜像只是兜底，一旦与 JSON 漂移就会让「测试通过但游戏里的数是旧的」。
                var builtin = ShipCatalog.Builtin;
                var diffs = new List<string>();
                foreach (var id in new[] { "ship.a_patrol", "ship.d_destroyer", "ship.g_cruiser" })
                {
                    ResolvedShipStats fromDisk, fromCode;
                    if (!disk.TryGet(id, out fromDisk) || !builtin.TryGet(id, out fromCode)) { diffs.Add(id + " 缺失"); continue; }
                    string diff = DiffStats(fromDisk, fromCode);
                    if (diff != null) diffs.Add(id + ": " + diff);
                }
                Check("内置镜像与 content/ships 逐字段一致", diffs.Count == 0, string.Join(" | ", diffs));

                // 缺字段必须报错，且要指出路径——静默补缺省正是 T01b 要消灭的东西
                string text = File.ReadAllText(Path.Combine(shipsDir, "g_cruiser.json"));
                string broken = text.Replace("\"frontDr\": 0.85,", "");
                string bid;
                ResolvedShipStats bstats;
                string[] berrors;
                bool parsed = ShipConfigParser.TryParse(broken, out bid, out bstats, out berrors);
                Check("缺 shield.frontDr 时解析失败并指出字段",
                    !parsed && string.Join("; ", berrors).Contains("shield.frontDr"),
                    parsed ? "居然解析成功了" : string.Join("; ", berrors));
            }
        }

        // 29. 过载窗口：旧记录没过期时不许重复扣，部分过期也不退还已扣量
        {
            var src = new EntityId("attacker");
            var dir = new Vec3(0f, 0f, 1f);

            // 29a. 窗口内两笔不重叠的部分：第二笔只能扣「新增的超额」。
            //      不做 deducted 记账时，第二笔会按 excess=130 再扣一遍（池 300→90），
            //      这是最容易犯的错，也是这条断言存在的理由。
            {
                var w = ShieldWorld();
                var s = ShieldTarget(w, ShieldStats(frontDr: 1f, drainPerDamage: 0f, armorKinetic: 0f));
                DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 200f, dir);
                Check("单笔超额扣 80（300 → 220）", Math.Abs(s.ShieldBuffer - 220f) < 0.01f,
                    s.ShieldBuffer.ToString("0.0"));
                Advance(w, 0.2f);
                DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 50f, dir);
                Check("窗口未过期时第二笔只扣新增超额（220 → 170）",
                    Math.Abs(s.ShieldBuffer - 170f) < 0.01f, s.ShieldBuffer.ToString("0.0"));
            }

            // 29b. 部分旧记录过期、窗口始终压着的连续命中。
            //      每 0.5 s 一发 60，窗口 1.5 s 里恒压四发 = 240 > 阈值 120。
            //      规则读法：超额是「状态」，每发只付自己带来的那份增量；过期清的是窗口信用，
            //      不是缓冲池——所以池会一路减到 0，而不是停在 180。
            {
                var w = ShieldWorld();
                var s = ShieldTarget(w, ShieldStats(frontDr: 1f, drainPerDamage: 0f, armorKinetic: 0f));

                var afterHit = new float[7];
                float prev = s.ShieldBuffer;
                bool overcharged = false;
                for (int hit = 0; hit < 7; hit++)
                {
                    DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 60f, dir);
                    // 任何一发最多扣掉它自己带来的 60：扣多了就是把同一笔超额记了两遍。
                    if (prev - s.ShieldBuffer > 60f + 1e-3f) overcharged = true;
                    prev = s.ShieldBuffer;
                    afterHit[hit] = s.ShieldBuffer;
                    if (hit < 6 && !s.ShieldBroken) Advance(w, 0.5f);
                }

                Check("重叠命中每发最多扣自己那份（无重复记账）", !overcharged);
                Check("前两发在阈值内不扣池（300）", Math.Abs(afterHit[1] - 300f) < 0.01f,
                    afterHit[1].ToString("0.0"));
                Check("第 3、4 发各扣自己那份（180）", Math.Abs(afterHit[3] - 180f) < 0.01f,
                    afterHit[3].ToString("0.0"));
                Check("窗口部分过期后不退还已扣、也不双倍扣（120）",
                    Math.Abs(afterHit[4] - 120f) < 0.01f, afterHit[4].ToString("0.0"));
                Check("持续饱和第 7 发打空池并破盾", s.ShieldBroken && s.ShieldBuffer <= 0.01f,
                    "buffer=" + s.ShieldBuffer.ToString("0.0"));
            }

            // 29c. 阈值以内怎么叠都不扣：窗口压满 120 也仍是满池。
            {
                var w = ShieldWorld();
                var s = ShieldTarget(w, ShieldStats(frontDr: 1f, drainPerDamage: 0f, armorKinetic: 0f));
                for (int hit = 0; hit < 2; hit++)
                {
                    DamageResolver.ResolveHit(w, s, src, DamageChannel.Kinetic, 60f, dir);
                    if (hit < 1) Advance(w, 0.5f);
                }
                Check("窗口累计恰在阈值上不扣池", Math.Abs(s.ShieldBuffer - 300f) < 0.01f,
                    s.ShieldBuffer.ToString("0.0"));
            }
        }

        // 30. 熄火后持续请求满推：能源必须还能回升并重新点火
        {
            // 极端剖面：40 容量 / 1.0 再生 / 5.0 满推耗电 → 约 10 s 见底；
            // 阈值 25% = 10 点，熄火后再 10 s 才够重新点火。
            var power = new PowerProfile(40f, 1.0f, 5.0f, 0f, 1f, 0.25f);
            var stats = new ResolvedShipStats(200f, 200f, 30f, 10f, 180f, 220f, 3f, 50f, 2f, 0.9f,
                0.30f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f, power);

            var spec = new BattleStartSpec
            {
                BattleId = new EntityId("battle.pump"),
                MissionId = "mission.smoke",
                OriginSystemId = new EntityId("sys.kaila"),
                TargetSystemId = new EntityId("sys.kaila"),
                Allies = new[]
                {
                    new FleetEntry
                    {
                        ShipId = new EntityId("ally.engine"), DefinitionId = new ContentId("ship.test"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Flagship, SquadIndex = 0,
                        Spawn = new SpawnPoint(0f, 0f, 0f, 0f, 0f, 0f),
                        Stats = stats, InheritedHull01 = 1f
                    }
                },
                Enemies = new[]
                {
                    new FleetEntry
                    {
                        ShipId = new EntityId("enemy.far"), DefinitionId = new ContentId("ship.test"),
                        Role = StarPawn.Combat.Contracts.SquadRole.Escort, SquadIndex = 1,
                        // 放到 5000 km 外：这条只测「熄火能否恢复」，不能让敌舰在测量窗口里开火，
                        // 否则护盾抗伤电费会混进能源曲线，测出来的数就说不清是谁造成的。
                        Spawn = new SpawnPoint(0f, 0f, 5000f, 180f, 0f, 0f),
                        Stats = stats, InheritedHull01 = 1f
                    }
                },
                BattlefieldLayout = "open_space",
                Objective = new BattleObjective { Kind = "DestroyAll", TimeLimitSeconds = 600f },
                RandomSeed = 17,
                FixedStep = 1f / 30f
            };

            var rt = new StarPawn.Combat.Domain.CombatRuntime();
            rt.Initialize(spec);
            var helm = new HelmCommand { ShipId = new EntityId("ally.engine"), Thrust = 1f };

            bool locked = false;
            for (int i = 0; i < 600 && !locked; i++)
            {
                rt.Submit(helm);
                rt.Step();
                locked = rt.ReadSnapshot().Ships[0].PropulsionLocked;
            }
            Check("满推会把能源打到熄火", locked);

            // 熄火之后每 tick 仍提交满推。旧实现会继续为这笔「不会发生的推力」收费，
            // 能源死在 0 上，舰船永远回不到重启阈值——AI 每 tick 重写档位，最容易命中。
            bool restored = false;
            float energyAfter5s = -1f;
            for (int i = 0; i < 900; i++)
            {
                rt.Submit(helm);
                rt.Step();
                foreach (var e in rt.DrainEvents()) if (e.Kind == "PowerRestored") restored = true;
                if (i == 150) energyAfter5s = rt.ReadSnapshot().Ships[0].Energy01;
            }
            Check("熄火后仍请求满推，能源照样回升", energyAfter5s > 0.03f,
                "5 s 后 energy=" + energyAfter5s.ToString("0.000"));
            Check("回充过阈值后能重新点火", restored);
        }

        // 31. 开火失败不改任何状态：射程外连点不扣电、不出弹、不进装填
        {
            ResolvedShipStats gunStats = new ResolvedShipStats(
                800f, 20f, 20f, 100f, 320f, 300f, 6f, 300f, 8f, 0.92f,
                0.37f, new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f), 1f,
                new PowerProfile(120f, 3.2f, 4.4f, 0.9f, 7f, 0.18f),
                new ArmorProfile(0.20f, 0.35f),
                new ShieldProfile(0.85f, 0.55f, 0.15f, 0.25f, 4f, 8f, 0.12f, 0.3f),
                new OverloadProfile(120f, 1.5f, 300f, 6f));

            StarPawn.Combat.Domain.CombatRuntime MakeGunRuntime(string battleId)
            {
                var spec = new BattleStartSpec
                {
                    BattleId = new EntityId(battleId),
                    MissionId = "mission.smoke",
                    OriginSystemId = new EntityId("sys.kaila"),
                    TargetSystemId = new EntityId("sys.kaila"),
                    Allies = new[]
                    {
                        new FleetEntry
                        {
                            ShipId = new EntityId("ally.gun"), DefinitionId = new ContentId("ship.g_cruiser"),
                            Role = StarPawn.Combat.Contracts.SquadRole.Flagship, SquadIndex = 0,
                            Spawn = new SpawnPoint(0f, 0f, 0f, 0f, 0f, 0f),
                            Stats = gunStats, InheritedHull01 = 1f
                        }
                    },
                    Enemies = new[]
                    {
                        new FleetEntry
                        {
                            ShipId = new EntityId("enemy.far"), DefinitionId = new ContentId("ship.a_patrol"),
                            Role = StarPawn.Combat.Contracts.SquadRole.Escort, SquadIndex = 1,
                            Spawn = new SpawnPoint(0f, 0f, 900f, 180f, 0f, 0f),
                            Stats = gunStats, InheritedHull01 = 1f
                        }
                    },
                    BattlefieldLayout = "open_space",
                    Objective = new BattleObjective { Kind = "DestroyAll", TimeLimitSeconds = 300f },
                    RandomSeed = 19,
                    FixedStep = 1f / 30f
                };
                var rt = new StarPawn.Combat.Domain.CombatRuntime();
                rt.Initialize(spec);
                return rt;
            }

            var ally = new EntityId("ally.gun");
            var outOfRange = MakeGunRuntime("battle.gun.far");
            outOfRange.Submit(new AimCommand { ShipId = ally, WorldX = 0f, WorldY = 0f, WorldZ = 400f });
            outOfRange.Submit(new FireCommand { ShipId = ally });
            outOfRange.Step();
            var miss = outOfRange.ReadSnapshot();

            Check("射程外开火不扣电", Math.Abs(miss.Ships[0].Energy01 - 1f) < 1e-4f,
                "energy=" + miss.Ships[0].Energy01.ToString("0.0000"));
            Check("射程外开火不出弹", miss.Projectiles.Length == 0, "shells=" + miss.Projectiles.Length);
            Check("射程外开火不进装填", Math.Abs(miss.Ships[0].Reload01 - 1f) < 1e-3f,
                "reload=" + miss.Ships[0].Reload01.ToString("0.000"));

            // 对照组：同样的输入换成射程内，必须扣电且出弹——否则上面三条可能是「根本没开火」而不是「被拦下」。
            var inRange = MakeGunRuntime("battle.gun.near");
            inRange.Submit(new AimCommand { ShipId = ally, WorldX = 0f, WorldY = 0f, WorldZ = 200f });
            inRange.Submit(new FireCommand { ShipId = ally });
            inRange.Step();
            var hit = inRange.ReadSnapshot();
            Check("射程内开火扣电并出弹",
                hit.Projectiles.Length == 1 && hit.Ships[0].Energy01 < 1f,
                "shells=" + hit.Projectiles.Length + " energy=" + hit.Ships[0].Energy01.ToString("0.0000"));
        }

        // 32. 非法配置必须报错，不能被契约层的 clamp 悄悄改成合法值
        {
            string shipsDir = FindContentShipsDir();
            if (shipsDir == null)
            {
                Check("找得到 content/ships（非法配置用例）", false, "本机 cwd 不在仓库内");
            }
            else
            {
                string text = File.ReadAllText(Path.Combine(shipsDir, "g_cruiser.json"));
                string id;
                ResolvedShipStats st;
                string[] errs;

                bool ok = ShipConfigParser.TryParse(
                    text.Replace("\"frontDr\": 0.85", "\"frontDr\": 1.5"), out id, out st, out errs);
                Check("frontDr 填 1.5 报错（不能截成 1）",
                    !ok && string.Join("; ", errs).Contains("shield.frontDr"), string.Join("; ", errs));

                ok = ShipConfigParser.TryParse(
                    text.Replace("\"threshold\": 120", "\"threshold\": -5"), out id, out st, out errs);
                Check("threshold 填负数报错（不能改成 1）",
                    !ok && string.Join("; ", errs).Contains("overload.threshold"), string.Join("; ", errs));
            }
        }

        Console.WriteLine($"\n=== 结果：{_passed} 通过，{_failed} 失败 ===");
        Environment.Exit(_failed == 0 ? 0 : 1);
    }
}
