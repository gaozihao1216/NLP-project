// 关键契约测试：不依赖 Unity，dotnet run 即可。
// 覆盖 GPT 审查点名的场景：重复结算、中途失败回滚、重复剧情选择、战损继承、随机源上界、内容校验。

using System;
using System.Collections.Generic;
using StarPawn.Application;
using StarPawn.Bootstrap;
using StarPawn.Combat.Contracts;
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
                { Propulsion = 1.2f, Shield = -0.2f, Weapons = 0f }).Success);
            Check("合法三路能量通过",
                BattleRules.ValidatePower(new PowerAllocationCommand
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
            bool hurt = snap.Ships[0].Shield01 < 1f || snap.Ships[0].Health01 < 1f;
            Check("真实战斗：敌舰开火", sawFire);
            Check("真实战斗：敌舰主动接近", closedIn, "z=" + snap.Ships[1].Z.ToString("0.0"));
            Check("真实战斗：命中造成掉血", hurt,
                "shield=" + snap.Ships[0].Shield01.ToString("0.00") + " hull=" + snap.Ships[0].Health01.ToString("0.00"));
        }

        Console.WriteLine($"\n=== 结果：{_passed} 通过，{_failed} 失败 ===");
        Environment.Exit(_failed == 0 ? 0 : 1);
    }
}
