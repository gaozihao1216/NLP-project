// 单份舰体配置 → ResolvedShipStats。
//
// 职责边界（T01b 刻意做小）：
//   做：读 content/ships/*.json 的 combat 段，字段缺一个就报错，不做静默补缺。
//   不做：mod 支持、热重载、schema 校验、本地化名。那些属完整加载器，以后整体替换它。
//
// 为什么缺字段要报错而不是给默认值：这个加载器存在的全部意义，就是让 JSON 成为唯一真源。
// 缺字段静默补缺省，等于在代码里又藏了一份数值表，调参时改 JSON 不生效而没人知道为什么。
//
// 单位与 JSON 一致：km、km/s、km/s²、度、度/秒、能量点。

using System;
using System.Collections.Generic;
using StarPawn.Combat.Contracts;

namespace StarPawn.Combat.Content
{
    public static class ShipConfigParser
    {
        /// <summary>
        /// 解析一份舰体 JSON。成功时 stats 非空；失败时 errors 说明缺了什么或哪里不合法。
        /// </summary>
        public static bool TryParse(string json, out string id, out ResolvedShipStats stats, out string[] errors)
        {
            id = null;
            stats = null;
            var log = new List<string>();

            object root;
            string parseError;
            if (!MiniJson.TryParse(json, out root, out parseError))
            {
                errors = new[] { "JSON 解析失败: " + parseError };
                return false;
            }

            var top = new Node(root as Dictionary<string, object>, "", log);
            id = top.Str("id", required: true);
            if (string.IsNullOrEmpty(id))
            {
                errors = log.ToArray();
                if (errors.Length == 0) errors = new[] { "缺少 id" };
                return false;
            }

            Node combat;
            if (!top.Child("combat", out combat))
            {
                errors = new[] { id + ": 缺少 combat 段" };
                return false;
            }

            // ---- 舰体与武器 ----
            float hp = (float)combat.Num("hp", required: true);
            float maxSpeed = (float)combat.Num("maxSpeed", required: true);
            float turnRate = (float)combat.Num("turnRate", required: true);
            float gunRange = (float)combat.Num("gunRange", required: true);
            float shellVelocity = (float)combat.Num("shellVelocity", required: true);
            float reload = (float)combat.Num("reloadSeconds", required: true);
            float gunDamage = (float)combat.Num("gunDamage", required: true);
            float accuracy = (float)combat.Num("accuracy", required: true);
            float maxSpread = (float)combat.Num("maxSpreadDeg", fallback: ResolvedShipStats.DefaultMaxSpreadDeg);
            float hullRadius = (float)combat.Num("hullRadiusKm", fallback: ResolvedShipStats.HullRadiusKmFromHull(hp));

            // ---- 机动 ----
            Node man;
            if (!combat.Child("maneuver", out man)) return Fail(out errors, log, id + ": 缺少 maneuver 段");
            var maneuver = new ManeuverProfile(
                (float)man.Num("maxThrustKmS2", required: true),
                (float)man.Num("yawRateDps", required: true),
                (float)man.Num("pitchRateDps", required: true),
                (float)man.Num("rollRateDps", required: true),
                (float)man.Num("yawAccelDps2", required: true),
                (float)man.Num("pitchAccelDps2", required: true),
                (float)man.Num("rollAccelDps2", required: true));

            // ---- 能源 ----
            Node pow;
            if (!combat.Child("power", out pow)) return Fail(out errors, log, id + ": 缺少 power 段");
            var power = new PowerProfile(
                (float)pow.Num("capacity", required: true),
                (float)pow.Num("regenPerSecond", required: true),
                (float)pow.Num("propulsionDrainPerSecond", required: true),
                (float)pow.Num("turnDrainPerSecond", required: true),
                (float)pow.Num("weaponDrainPerShot", required: true),
                (float)pow.Num("restartFraction", required: true));

            // ---- 防御 ----
            Node armorNode;
            if (!combat.Child("armor", out armorNode)) return Fail(out errors, log, id + ": 缺少 armor 段");
            var armor = new ArmorProfile(
                (float)armorNode.Num("beamDr", required: true),
                (float)armorNode.Num("kineticDr", required: true));

            Node shieldNode;
            if (!combat.Child("shield", out shieldNode)) return Fail(out errors, log, id + ": 缺少 shield 段");
            var shield = new ShieldProfile(
                (float)shieldNode.Num("frontDr", required: true),
                (float)shieldNode.Num("sideDr", required: true),
                (float)shieldNode.Num("rearDr", required: true),
                (float)shieldNode.Num("turnLossFactor", required: true),
                (float)shieldNode.Num("regenDelaySeconds", required: true),
                (float)shieldNode.Num("bufferRegenPerSecond", required: true),
                (float)shieldNode.Num("drainPerDamage", required: true),
                (float)shieldNode.Num("recoverFraction", required: true));

            Node overloadNode;
            if (!combat.Child("overload", out overloadNode)) return Fail(out errors, log, id + ": 缺少 overload 段");
            var overload = new OverloadProfile(
                (float)overloadNode.Num("threshold", required: true),
                (float)overloadNode.Num("windowSeconds", required: true),
                (float)overloadNode.Num("bufferCapacity", required: true),
                (float)overloadNode.Num("breakSeconds", required: true));

            if (log.Count > 0) return Fail(out errors, log, null);

            // 构造之后再查一遍「会不会让 BattleRules.ValidateStats 失败」的量。
            // 配置读得进来但值不合法，比读不进来更难查——它只会在战斗里表现为舰船不动。
            if (hp <= 0f) log.Add(id + ": hp 必须为正，当前 " + hp);
            if (hullRadius <= 0f) log.Add(id + ": hullRadiusKm 必须为正，当前 " + hullRadius);
            if (maxSpeed <= 0f) log.Add(id + ": maxSpeed 必须为正，当前 " + maxSpeed);
            if (turnRate <= 0f) log.Add(id + ": turnRate 必须为正，当前 " + turnRate);
            if (shellVelocity <= 0f) log.Add(id + ": shellVelocity 必须为正，当前 " + shellVelocity);
            if (gunRange <= 0f) log.Add(id + ": gunRange 必须为正，当前 " + gunRange);
            if (overload.BufferCapacity < overload.Threshold)
                log.Add(id + ": 缓冲池(" + overload.BufferCapacity + ") 小于过载阈值(" + overload.Threshold +
                        ")，一击就会破盾");
            if (accuracy < 0f || accuracy > 1f) log.Add(id + ": accuracy 必须在 [0,1]，当前 " + accuracy);
            if (log.Count > 0) return Fail(out errors, log, null);

            stats = new ResolvedShipStats(
                maxHull: hp,
                maxSpeedKps: maxSpeed,
                turnRateDps: turnRate,
                gunDamage: gunDamage,
                gunRangeKm: gunRange,
                shellVelocityKps: shellVelocity,
                reloadSeconds: reload,
                // 这两个参数只为旧调用点保留，真实容量/回充在 Overload / Shield 里。
                shieldCapacity: overload.BufferCapacity,
                shieldRegenPerSec: shield.RegenPerSec,
                accuracy01: accuracy,
                hullRadiusKm: hullRadius,
                maneuver: maneuver,
                maxSpreadDeg: maxSpread,
                power: power,
                armor: armor,
                shield: shield,
                overload: overload);

            errors = new string[0];
            return true;
        }

        private static bool Fail(out string[] errors, List<string> log, string message)
        {
            if (!string.IsNullOrEmpty(message)) log.Add(message);
            errors = log.ToArray();
            return false;
        }

        /// <summary>
        /// 带路径的取值器：缺字段时报出「combat.shield.frontDr 缺失」这种能直接定位的错，
        /// 而不是在调用点拼字符串（拼多了总有一处漏路径）。
        /// </summary>
        private sealed class Node
        {
            private readonly Dictionary<string, object> _values;
            private readonly string _path;
            private readonly List<string> _errors;

            public Node(Dictionary<string, object> values, string path, List<string> errors)
            {
                _values = values;
                _path = path;
                _errors = errors;
            }

            public string Str(string key, bool required)
            {
                string v;
                if (_values != null && MiniJson.TryGetString(_values, key, out v)) return v;
                if (required) _errors.Add(Where(key) + " 缺失或不是字符串");
                return null;
            }

            public double Num(string key, bool required)
            {
                double v;
                if (_values != null && MiniJson.TryGetNumber(_values, key, out v)) return v;
                if (required) _errors.Add(Where(key) + " 缺失或不是数字");
                return 0d;
            }

            public double Num(string key, float fallback)
            {
                double v;
                return _values != null && MiniJson.TryGetNumber(_values, key, out v) ? v : fallback;
            }

            public bool Child(string key, out Node child)
            {
                Dictionary<string, object> d;
                if (_values != null && MiniJson.TryGetObject(_values, key, out d))
                {
                    child = new Node(d, string.IsNullOrEmpty(_path) ? key : _path + "." + key, _errors);
                    return true;
                }
                child = null;
                return false;
            }

            private string Where(string key)
                => (_path.Length == 0 ? "" : _path + ".") + key;
        }
    }
}
