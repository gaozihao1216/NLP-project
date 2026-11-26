// 无 Unity 的一局三维战斗。既是验收工具，也是调参时唯一能看见数值的地方。
//
// 用法：
//   dotnet run --project tools/CombatSimCli -- --seed 20261122
//   --seed N        随机种子（默认 20261122）
//   --no-lead       不用提前量（用来证明提前量确实有效）
//   --seconds N     战斗时长上限（默认 300）
//   --enemies N     敌舰数量（默认 3）
//   --content DIR   内容根目录（含 ships/ 子目录）。默认向上查找仓库里的 content/
//   --quiet         只打印结算，不打印逐 0.5 秒状态
//
// 舰船属性一律从 content/ships/*.json 读（T01b）。CLI 自己不再留数值表——
// 以前那份「与 JSON 一一对应」的副本迟早会与 JSON 分叉，而分叉后调参结论全是错的。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using StarPawn.Combat.Content;
using StarPawn.Combat.Contracts;
using StarPawn.Combat.Domain;
using StarPawn.Foundation;

internal static class Program
{
    private const float Rad2Deg = 57.29578f;

    /// <summary>从内容目录加载的舰船配置表；Main 里初始化，退出前一直有效。</summary>
    private static ShipCatalog Catalog;

    private static int Main(string[] args)
    {
        int seed = 20261122;
        bool useLead = true;
        bool quiet = false;
        int enemyCount = 3;
        float maxSeconds = 300f;
        string contentRoot = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--no-lead": useLead = false; break;
                case "--quiet": quiet = true; break;
                case "--enemies": enemyCount = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seconds": maxSeconds = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--content": contentRoot = args[++i]; break;
                default:
                    Console.WriteLine("未知参数: " + args[i]);
                    return 2;
            }
        }

        string contentError;
        Catalog = LoadCatalog(contentRoot, out contentError);
        if (contentError != null)
        {
            Console.WriteLine("舰船配置加载失败: " + contentError);
            Console.WriteLine("（可以用 --content <仓库>/content 指定内容根）");
            return 2;
        }

        var spec = BuildSpec(seed, enemyCount);
        var runtime = new CombatRuntime();
        runtime.Initialize(spec);

        Console.WriteLine("=== StarPawn 三维战斗仿真 ===");
        Console.WriteLine("seed=" + seed + "  dt=" + spec.FixedStep.ToString("0.####") + "s  lead=" + useLead);
        Console.WriteLine("舰船配置: " + Catalog.Count + " 型（来自 content/ships）");
        PrintCatalog(spec);

        int printEvery = Math.Max(1, (int)Math.Round(0.5f / spec.FixedStep));
        int maxTicks = (int)Math.Round(maxSeconds / spec.FixedStep);

        for (int tick = 0; tick <= maxTicks; tick++)
        {
            DrivePlayerSide(runtime, useLead);
            runtime.Step();

            foreach (var e in runtime.DrainEvents())
            {
                if (quiet || e.Kind == "NotReady") continue;
                Console.WriteLine("[" + e.At.Seconds.ToString("0.00").PadLeft(7) + "s] " +
                    e.Kind.PadRight(12) + e.SourceId + " -> " + e.TargetId + "  " + e.Detail);
            }

            if (!quiet && tick % printEvery == 0) PrintStatus(runtime, tick);

            if (runtime.TryGetResult(out var result))
            {
                PrintResult(result);
                return result.Victory ? 0 : (result.ElapsedSeconds >= maxSeconds ? 3 : 1);
            }
        }

        Console.WriteLine("达到时长上限仍未分出胜负（" + maxSeconds + "s）");
        return 3;
    }

    /// <summary>
    /// 加载舰船配置。contentRoot 为空时从当前目录向上找 content/ships（dotnet run 的 cwd
    /// 通常是仓库根，但 IDE 里可能不是，所以两个起点各找一遍）。
    /// 一份都读不出来就报错退出——CLI 是调参工具，跑在「以为在读配置」的状态比跑不起来更糟。
    /// </summary>
    private static ShipCatalog LoadCatalog(string contentRoot, out string error)
    {
        error = null;
        string root = contentRoot ?? FindContentRoot();
        if (string.IsNullOrEmpty(root))
        {
            error = "找不到 content/ships 目录";
            return null;
        }

        string[] errors;
        var catalog = ShipCatalog.LoadDirectory(Path.Combine(root, "ships"), out errors);
        if (errors.Length > 0)
        {
            error = string.Join("; ", errors);
            return null;
        }
        if (catalog.Count == 0)
        {
            error = root + "/ships 里没有可用的舰船配置";
            return null;
        }
        return catalog;
    }

    private static string FindContentRoot()
    {
        var candidates = new List<string>();
        AddAncestors(candidates, Directory.GetCurrentDirectory());
        AddAncestors(candidates, AppContext.BaseDirectory);
        foreach (var dir in candidates)
        {
            var ships = Path.Combine(dir, "content", "ships");
            if (Directory.Exists(ships)) return Path.Combine(dir, "content");
        }
        return null;
    }

    private static void AddAncestors(List<string> list, string start)
    {
        try
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                if (!list.Contains(dir.FullName)) list.Add(dir.FullName);
        }
        catch (Exception)
        {
            // 起点不存在（例如被删掉的构建目录）就当作没找到，继续试下一个起点。
        }
    }

    private static BattleStartSpec BuildSpec(int seed, int enemyCount)
    {
        var allies = new List<FleetEntry>
        {
            MakeEntry("ship.g_cruiser", "ally.cruiser", true, 0, new SpawnPoint(0f, 0f, 0f, 0f, 0f, 0f), 1f),
            MakeEntry("ship.a_patrol", "ally.patrol", true, 1, new SpawnPoint(-30f, 10f, -20f, 0f, 0f, 0f), 1f),
        };

        var enemies = new List<FleetEntry>();
        for (int i = 0; i < enemyCount; i++)
        {
            enemies.Add(MakeEntry("ship.a_patrol", "enemy." + i, false, i % 4,
                new SpawnPoint(-40f + i * 40f, 0f, 250f, 180f, 0f, 0f), 1f));
        }

        return new BattleStartSpec
        {
            BattleId = new EntityId("battle.cli"),
            MissionId = "mission.cli",
            OriginSystemId = new EntityId("sys.kaila"),
            TargetSystemId = new EntityId("sys.kaila"),
            Allies = allies.ToArray(),
            Enemies = enemies.ToArray(),
            BattlefieldLayout = "asteroid_belt_01",
            Objective = new BattleObjective { Kind = "DestroyAll", TimeLimitSeconds = 300f },
            RandomSeed = seed,
            FixedStep = 1f / 30f
        };
    }

    private static FleetEntry MakeEntry(string defId, string shipId, bool ally, int squad, SpawnPoint spawn, float hull01)
        => new FleetEntry
        {
            ShipId = new EntityId(shipId),
            DefinitionId = new ContentId(defId),
            Role = squad == 0 ? SquadRole.Flagship : SquadRole.Escort,
            SquadIndex = squad,
            Spawn = spawn,
            Stats = Catalog.Get(defId),
            InheritedHull01 = hull01
        };

    /// <summary>
    /// 用命令驱动我方（相当于一个会瞄准的玩家）：读快照 → 算提前量 → 发 Helm/Aim/Fire。
    /// 全程走 Submit，不绕过装填与射程限制。
    /// </summary>
    private static void DrivePlayerSide(CombatRuntime runtime, bool useLead)
    {
        var snap = runtime.ReadSnapshot();
        if (snap == null) return;

        foreach (var s in snap.Ships)
        {
            if (!s.IsAlly || !s.IsAlive) continue;

            var stats = Catalog.Get(s.DefinitionId.ToString());
            ShipSnapshot target = NearestEnemy(snap, s);
            if (target == null) continue;

            Vec3 pos = new Vec3(s.X, s.Y, s.Z);
            Vec3 vel = new Vec3(s.Vx, s.Vy, s.Vz);
            Vec3 targetPos = new Vec3(target.X, target.Y, target.Z);
            Vec3 targetVel = new Vec3(target.Vx, target.Vy, target.Vz);

            Vec3 aim = targetPos;
            if (useLead)
                CombatMath.TryLeadPoint(pos, vel, targetPos, targetVel, stats.ShellVelocityKps, out aim);

            Quat attitude = Quat.FromEulerDeg(s.Yaw, s.Pitch, s.Roll);
            Vec3 local = attitude.Conjugate.Rotate((aim - pos).Normalized);
            float yawErr = (float)Math.Atan2(local.X, local.Z) * Rad2Deg;
            float pitchErr = -(float)Math.Asin(Math.Clamp(local.Y, -1f, 1f)) * Rad2Deg;

            var m = stats.Maneuver;
            float desiredYaw = Math.Clamp(yawErr * 2f, -m.YawRateDps, m.YawRateDps);
            float desiredPitch = Math.Clamp(pitchErr * 2f, -m.PitchRateDps, m.PitchRateDps);

            float dt = runtime.Spec.FixedStep;
            runtime.Submit(new HelmCommand
            {
                ShipId = s.ShipId,
                Thrust = ThrustForDistance((aim - pos).Length, stats.GunRangeKm),
                Yaw = InputForTarget(s.YawRate, desiredYaw, m.YawAccelDps2, dt),
                Pitch = InputForTarget(s.PitchRate, desiredPitch, m.PitchAccelDps2, dt),
                Roll = 0f,
                FlightAssist = true
            });

            if (useLead)
            {
                // 正常玩法：锁定目标，提前量由仿真每 tick 解算（和 HUD 准星同源）。
                runtime.Submit(new LockTargetCommand { ShipId = s.ShipId, TargetId = target.ShipId });
            }
            else
            {
                // 对照组：不锁定，手动把瞄准点指向目标当前位置——没有提前量。
                runtime.Submit(new AimCommand
                {
                    ShipId = s.ShipId, WorldX = targetPos.X, WorldY = targetPos.Y, WorldZ = targetPos.Z
                });
            }

            // 开火只判射程；射界由炮座判（炮塔化后一门炮能打 ±140°，不再按舰首 ±15° 卡）。
            float dist = (aim - pos).Length;
            if (dist <= stats.GunRangeKm * 0.95f)
                runtime.Submit(new FireCommand { ShipId = s.ShipId });
        }
    }

    private static float ThrustForDistance(float dist, float gunRange)
        => dist > gunRange * 0.9f
            ? 1f
            : Math.Clamp((dist - gunRange * 0.7f) / 40f, BattleRules.MinThrust, BattleRules.MaxThrust);

    private static float InputForTarget(float current, float target, float accel, float dt)
    {
        float maxDelta = accel * dt;
        if (maxDelta <= 1e-9f) return 0f;
        return Math.Clamp((target - current) / maxDelta, -1f, 1f);
    }

    private static ShipSnapshot NearestEnemy(BattleSnapshot snap, ShipSnapshot self)
    {
        ShipSnapshot best = null;
        float bestSqr = float.MaxValue;
        foreach (var other in snap.Ships)
        {
            if (other.IsAlly || !other.IsAlive) continue;
            float dx = other.X - self.X, dy = other.Y - self.Y, dz = other.Z - self.Z;
            float sqr = dx * dx + dy * dy + dz * dz;
            if (sqr < bestSqr) { bestSqr = sqr; best = other; }
        }
        return best;
    }

    private static void PrintCatalog(BattleStartSpec spec)
    {
        Console.WriteLine("--- 舰体参数（来自 content/ships，代码不含倍率） ---");
        var seen = new HashSet<string>();
        foreach (var e in spec.Allies) PrintShipLine(e, seen);
        foreach (var e in spec.Enemies) PrintShipLine(e, seen);
        Console.WriteLine();
    }

    private static void PrintShipLine(FleetEntry e, HashSet<string> seen)
    {
        string id = e.DefinitionId.ToString();
        if (!seen.Add(id)) return;
        var s = e.Stats;
        var m = s.Maneuver;
        Console.WriteLine("  " + id.PadRight(16) +
            " 半径 " + s.HullRadiusKm.ToString("0.00") + "km" +
            " 推力 " + m.MaxThrustKmS2.ToString("0.0") + "km/s²" +
            " 转速 " + m.YawRateDps.ToString("0") + "/" + m.PitchRateDps.ToString("0") + "/" + m.RollRateDps.ToString("0") + "°/s" +
            " 角加速度 " + m.YawAccelDps2.ToString("0") + "/" + m.PitchAccelDps2.ToString("0") + "/" + m.RollAccelDps2.ToString("0") + "°/s²" +
            " 限速 " + s.MaxSpeedKps.ToString("0") + "km/s" +
            " 盾 " + s.Shield.FrontDr.ToString("0.00") + "/" + s.Shield.SideDr.ToString("0.00") + "/" + s.Shield.RearDr.ToString("0.00") +
            " 池 " + s.Overload.BufferCapacity.ToString("0") + "/阈 " + s.Overload.Threshold.ToString("0") +
            " 能源 " + s.Power.Capacity.ToString("0") + "点/再生 " + s.Power.RegenPerSec.ToString("0.0") + "/s" +
            " 炮座 " + s.Turrets.Length + "（单发 " +
            string.Join("/", System.Linq.Enumerable.Select(s.Turrets, t => t.Damage.ToString("0"))) + "）");
    }

    private static void PrintStatus(CombatRuntime runtime, int tick)
    {
        var snap = runtime.ReadSnapshot();
        if (snap == null) return;
        Console.WriteLine("--- t=" + snap.Clock.Seconds.ToString("0.00") + "s  弹道 " + snap.Projectiles.Length + " ---");
        foreach (var s in snap.Ships)
        {
            Console.WriteLine("  " + (s.IsAlive ? "■" : "□") + " " + s.ShipId.ToString().PadRight(12) +
                " pos(" + s.X.ToString("0") + "," + s.Y.ToString("0") + "," + s.Z.ToString("0") + ")" +
                " v=" + s.Speed.ToString("0.0") + "km/s" +
                " hp=" + s.Health01.ToString("0.00") +
                " 盾=" + (s.ShieldBroken ? "破" : s.Buffer01.ToString("0.00")) +
                " en=" + s.Energy01.ToString("0.00") + (s.PropulsionLocked ? "(熄火)" : "") +
                " 炮位 " + s.TurretsInArc + "/" + s.TurretCount + "（装填完 " + s.TurretsLoaded + "）" +
                " 转速 " + s.YawRate.ToString("0.0") + "/" + s.PitchRate.ToString("0.0") + "°/s" +
                (s.LockedTargetId.IsEmpty ? "" : " 锁定 " + s.LockedTargetId));
        }
    }

    private static void PrintResult(BattleResult r)
    {
        Console.WriteLine();
        Console.WriteLine("=== 战斗结果 ===");
        Console.WriteLine("胜负: " + (r.Victory ? "胜利" : "失败") +
                          "  用时 " + r.ElapsedSeconds.ToString("0.00") + "s");
        Console.WriteLine("击沉敌舰: " + r.DestroyedEnemyIds.Length + "  损失我方: " + r.LostAllyIds.Length);
        foreach (var o in r.ShipOutcomes)
        {
            Console.WriteLine("  " + o.ShipId.ToString().PadRight(14) +
                (o.IsLost ? "击沉" : "存活") + " 剩余 " + o.HullAfter01.ToString("0.000"));
        }
    }
}
