// 舰体配置表：把 content/ships/*.json 解析成按定义 ID 索引的 ResolvedShipStats。
//
// 这是 T01b 的产出口：application 与 tools 都通过它取属性，代码里不再各留一份数值表。
// 唯一例外是 Builtin —— 内容目录不可达时的兜底（Unity 打包前的临时状态），
// 它是 JSON 的镜像而不是第二真源：改数值必须改 JSON，Builtin 只跟着同步。
//
// 用法：
//   var catalog = ShipCatalog.LoadDirectory("content/ships", out errors);
//   ResolvedShipStats stats;
//   if (!catalog.TryGet("ship.g_cruiser", out stats)) { ... }

using System;
using System.Collections.Generic;
using System.IO;
using StarPawn.Combat.Contracts;

namespace StarPawn.Combat.Content
{
    public sealed class ShipCatalog : IShipStatsSource
    {
        private readonly Dictionary<string, ResolvedShipStats> _byId;

        private ShipCatalog(Dictionary<string, ResolvedShipStats> byId)
        {
            _byId = byId ?? new Dictionary<string, ResolvedShipStats>();
        }

        public int Count => _byId.Count;

        public string[] Ids
        {
            get
            {
                var ids = new string[_byId.Count];
                _byId.Keys.CopyTo(ids, 0);
                Array.Sort(ids, StringComparer.Ordinal);
                return ids;
            }
        }

        public bool TryGet(string definitionId, out ResolvedShipStats stats)
        {
            stats = null;
            return !string.IsNullOrEmpty(definitionId) && _byId.TryGetValue(definitionId, out stats);
        }

        /// <summary>同 TryGet，但取不到直接抛出。装配期用它，免得把缺配置拖到战斗中途。</summary>
        public ResolvedShipStats Get(string definitionId)
        {
            ResolvedShipStats stats;
            if (!TryGet(definitionId, out stats))
                throw new InvalidOperationException("没有该舰船配置: " + (definitionId ?? "(null)"));
            return stats;
        }

        /// <summary>
        /// 读一个目录下的所有 *.json。坏文件不中断整批：解析错误累积到 errors，
        /// 其余舰船照常可用——这样一次运行就能看到所有配置问题，而不是改一个跑一次。
        /// </summary>
        public static ShipCatalog LoadDirectory(string shipsDir, out string[] errors)
        {
            var log = new List<string>();
            var map = new Dictionary<string, ResolvedShipStats>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(shipsDir) || !Directory.Exists(shipsDir))
            {
                errors = new[] { "舰船配置目录不存在: " + (shipsDir ?? "(null)") };
                return new ShipCatalog(map);
            }

            var files = Directory.GetFiles(shipsDir, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (Exception ex)
                {
                    // 读不动（权限、被占用）与格式错分开报，否则会去查 JSON 语法。
                    log.Add(Path.GetFileName(file) + ": 读取失败 " + ex.Message);
                    continue;
                }
                AddOne(text, Path.GetFileName(file), map, log);
            }

            errors = log.ToArray();
            return new ShipCatalog(map);
        }

        /// <summary>从内存里的 JSON 文本构造。测试与工具用，不碰文件系统。</summary>
        public static ShipCatalog FromJson(IEnumerable<string> jsonDocs, out string[] errors)
        {
            var log = new List<string>();
            var map = new Dictionary<string, ResolvedShipStats>(StringComparer.Ordinal);
            int i = 0;
            foreach (var json in jsonDocs) AddOne(json, "doc#" + (i++), map, log);
            errors = log.ToArray();
            return new ShipCatalog(map);
        }

        private static void AddOne(string json, string source,
            Dictionary<string, ResolvedShipStats> map, List<string> log)
        {
            string id;
            ResolvedShipStats stats;
            string[] errors;
            if (!ShipConfigParser.TryParse(json, out id, out stats, out errors))
            {
                foreach (var e in errors) log.Add(source + ": " + e);
                return;
            }

            if (map.ContainsKey(id))
            {
                log.Add(source + ": 舰船 ID 重复 (" + id + ")");
                return;
            }
            map[id] = stats;
        }

        private static ShipCatalog _builtin;

        /// <summary>
        /// 内容目录不可达时的兜底。**数值必须与 content/ships/*.json 一致**，
        /// 它只是让「没有 Unity 内容目录」的环境（契约测试、空 contentRoot 的 Bootstrap）能跑。
        /// 完整加载器接入后，这个属性应当整体删除。
        /// 属性缓存一份：ResolvedShipStats 不可变，重复构造只是白费 GC。
        /// </summary>
        public static ShipCatalog Builtin => _builtin ?? (_builtin = BuildBuiltin());

        private static ShipCatalog BuildBuiltin()
        {
            var map = new Dictionary<string, ResolvedShipStats>(StringComparer.Ordinal)
            {
                ["ship.a_patrol"] = Make(
                    120f, 32f, 45f, 20f, 180f, 220f, 3.2f, 0.85f, 0.20f,
                    new ManeuverProfile(4.0f, 45f, 27f, 81f, 180f, 108f, 324f),
                    new PowerProfile(80f, 4.2f, 4.8f, 0.5f, 1.25f, 0.2f),
                    new ShieldProfile(0.85f, 0.55f, 0.15f, 0.25f, 4f, 3f, 0.12f, 0.3f),
                    new OverloadProfile(32f, 1.5f, 80f, 6f),
                    new[]
                    {
                        Turret("bow", 0f, 0.03f, 0.15f, 0f, 135f, 3.2f, 10f, 220f, 180f),
                        Turret("stern", 0f, 0.02f, -0.15f, 180f, 135f, 3.2f, 10f, 220f, 180f)
                    }),
                ["ship.d_destroyer"] = Make(
                    300f, 28f, 32f, 40f, 240f, 260f, 4.5f, 0.88f, 0.27f,
                    new ManeuverProfile(3.5f, 32f, 19f, 58f, 128f, 76f, 230f),
                    new PowerProfile(140f, 3.6f, 4.4f, 0.7f, 1.5f, 0.2f),
                    new ShieldProfile(0.85f, 0.55f, 0.15f, 0.25f, 4f, 5f, 0.12f, 0.3f),
                    new OverloadProfile(64f, 1.5f, 160f, 6f),
                    new[]
                    {
                        Turret("bow.l", 0.09f, 0.04f, 0.20f, 0f, 130f, 4.5f, 13f, 260f, 240f),
                        Turret("bow.r", -0.09f, 0.04f, 0.20f, 0f, 130f, 4.5f, 13f, 260f, 240f),
                        Turret("stern", 0f, 0.03f, -0.20f, 180f, 130f, 4.5f, 13f, 260f, 240f)
                    }),
                ["ship.g_cruiser"] = Make(
                    800f, 20f, 20f, 100f, 320f, 300f, 6f, 0.92f, 0.37f,
                    new ManeuverProfile(2.5f, 20f, 12f, 36f, 80f, 48f, 144f),
                    new PowerProfile(120f, 3.2f, 4.4f, 0.9f, 1.75f, 0.18f),
                    new ShieldProfile(0.85f, 0.55f, 0.15f, 0.25f, 4f, 8f, 0.12f, 0.3f),
                    new OverloadProfile(120f, 1.5f, 300f, 6f),
                    new[]
                    {
                        Turret("bow.l", 0.13f, 0.05f, 0.27f, 0f, 140f, 6f, 25f, 300f, 320f),
                        Turret("bow.r", -0.13f, 0.05f, 0.27f, 0f, 140f, 6f, 25f, 300f, 320f),
                        Turret("stern.l", 0.11f, 0.03f, -0.27f, 180f, 140f, 6f, 25f, 300f, 320f),
                        Turret("stern.r", -0.11f, 0.03f, -0.27f, 180f, 140f, 6f, 25f, 300f, 320f)
                    }),
            };
            return new ShipCatalog(map);
        }

        /// <summary>内置镜像里的炮座。俯仰取缺省 -10..75、通道取动能，与 JSON 的缺省规则一致。</summary>
        private static TurretProfile Turret(string id, float right, float up, float forward,
            float yawCenter, float yawHalf, float reload, float damage, float velocity, float range)
            => new TurretProfile(id, right, up, forward, yawCenter, yawHalf, -10f, 75f,
                reload, damage, velocity, range, DamageChannel.Kinetic);

        private static ResolvedShipStats Make(float hp, float maxSpeed, float turnRate,
            float gunDamage, float gunRange, float shellVelocity, float reload,
            float accuracy, float hullRadius, ManeuverProfile maneuver, PowerProfile power,
            ShieldProfile shield, OverloadProfile overload, TurretProfile[] turrets)
            => new ResolvedShipStats(
                maxHull: hp, maxSpeedKps: maxSpeed, turnRateDps: turnRate,
                gunDamage: gunDamage, gunRangeKm: gunRange, shellVelocityKps: shellVelocity,
                reloadSeconds: reload,
                shieldCapacity: overload.BufferCapacity, shieldRegenPerSec: shield.RegenPerSec,
                accuracy01: accuracy, hullRadiusKm: hullRadius, maneuver: maneuver,
                maxSpreadDeg: ResolvedShipStats.DefaultMaxSpreadDeg, power: power,
                armor: ArmorProfile.Default, shield: shield, overload: overload,
                turrets: turrets);
    }
}
