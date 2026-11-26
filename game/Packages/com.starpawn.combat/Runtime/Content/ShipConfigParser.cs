// 单份舰体配置 → ResolvedShipStats。
//
// 职责边界（T01b 刻意做小）：
//   做：读 content/ships/*.json 的 combat 段，字段缺一个就报错，不做静默补缺。
//   不做：mod 支持、热重载、本地化名。那些属完整加载器，以后整体替换它。
//
// 两条都不能破的规矩：
//   1. 缺字段必须报错。静默补缺省等于在代码里又藏一份数值表，调参时改 JSON 不生效而没人知道为什么。
//   2. **先校验原始值，再构造对象。** 契约层的构造函数会把非法值「修正」掉（1.5 截成 1、
//      负数改成 1、窗口 -1 改成 1.5 秒），于是配置能「解析成功」，跑起来的却不是作者填的数。
//      这种错只会在战斗里表现为「舰船怪怪的」，比读不进来难查一个量级。
//      所以下面所有数值都过 有限性 + 区间 两道检查，越界直接记错，不等构造。
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
        /// 解析一份舰体 JSON。成功时 stats 非空；失败时 errors 说明缺了什么或哪个值越界。
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
            // 全部先落成原始值并校验，构造放到 log 为空之后。
            float hp = (float)combat.Positive("hp");
            float maxSpeed = (float)combat.Positive("maxSpeed");
            float turnRate = (float)combat.Positive("turnRate");
            float gunRange = (float)combat.Positive("gunRange");
            float shellVelocity = (float)combat.Positive("shellVelocity");
            float reload = (float)combat.Positive("reloadSeconds");
            float gunDamage = (float)combat.NonNeg("gunDamage");
            float accuracy = (float)combat.Unit("accuracy");
            float maxSpread = (float)combat.OptNonNeg("maxSpreadDeg", ResolvedShipStats.DefaultMaxSpreadDeg);
            float hullRadius = (float)combat.OptPositive("hullRadiusKm", ResolvedShipStats.HullRadiusKmFromHull(hp));

            // ---- 机动 ----
            Node man;
            if (!combat.Child("maneuver", out man)) return Fail(out errors, log, id + ": 缺少 maneuver 段");
            float maxThrust = (float)man.Positive("maxThrustKmS2");
            float yawRate = (float)man.Positive("yawRateDps");
            float pitchRate = (float)man.Positive("pitchRateDps");
            float rollRate = (float)man.Positive("rollRateDps");
            float yawAccel = (float)man.Positive("yawAccelDps2");
            float pitchAccel = (float)man.Positive("pitchAccelDps2");
            float rollAccel = (float)man.Positive("rollAccelDps2");

            // ---- 能源 ----
            Node pow;
            if (!combat.Child("power", out pow)) return Fail(out errors, log, id + ": 缺少 power 段");
            float capacity = (float)pow.Positive("capacity");
            float regen = (float)pow.NonNeg("regenPerSecond");
            float propulsionDrain = (float)pow.NonNeg("propulsionDrainPerSecond");
            float turnDrain = (float)pow.NonNeg("turnDrainPerSecond");
            float weaponDrain = (float)pow.NonNeg("weaponDrainPerShot");
            float restartFraction = (float)pow.OpenUnit("restartFraction");

            // ---- 防御 ----
            Node armorNode;
            if (!combat.Child("armor", out armorNode)) return Fail(out errors, log, id + ": 缺少 armor 段");
            float beamDr = (float)armorNode.Unit("beamDr");
            float kineticDr = (float)armorNode.Unit("kineticDr");

            Node shieldNode;
            if (!combat.Child("shield", out shieldNode)) return Fail(out errors, log, id + ": 缺少 shield 段");
            float frontDr = (float)shieldNode.Unit("frontDr");
            float sideDr = (float)shieldNode.Unit("sideDr");
            float rearDr = (float)shieldNode.Unit("rearDr");
            float turnLossFactor = (float)shieldNode.Unit("turnLossFactor");
            float regenDelay = (float)shieldNode.NonNeg("regenDelaySeconds");
            float bufferRegen = (float)shieldNode.NonNeg("bufferRegenPerSecond");
            float drainPerDamage = (float)shieldNode.NonNeg("drainPerDamage");
            float recoverFraction = (float)shieldNode.OpenUnit("recoverFraction");

            Node overloadNode;
            if (!combat.Child("overload", out overloadNode)) return Fail(out errors, log, id + ": 缺少 overload 段");
            float threshold = (float)overloadNode.Positive("threshold");
            float windowSeconds = (float)overloadNode.Positive("windowSeconds");
            float bufferCapacity = (float)overloadNode.Positive("bufferCapacity");
            float breakSeconds = (float)overloadNode.Positive("breakSeconds");

            // ---- 炮座 ----
            // 可选段：没有就是「一门全向炮」，参数取上面的舰体标量（与炮塔化之前完全一致）。
            // 有就必须每座都合法：射界写错会让某一门永远打不出去，而且不报错、只是沉默，最难查。
            // 缺省规则（写在这里，不写进 JSON）：mount 缺省 0、pitch 缺省 -10..75、
            // 装填/伤害/弹速/射程缺省取舰体标量、channel 缺省 kinetic。
            List<TurretProfile> turretList = null;
            var rawTurrets = combat.Array("turrets");
            if (rawTurrets != null)
            {
                if (rawTurrets.Count == 0) log.Add("combat.turrets 是空数组：至少给一门炮");
                turretList = new List<TurretProfile>(rawTurrets.Count);
                for (int i = 0; i < rawTurrets.Count; i++)
                {
                    var tn = combat.Element(rawTurrets[i], "turrets", i);
                    if (tn == null) continue;

                    string tid = tn.OptStr("id", "gun" + i);
                    float mr = 0f, mu = 0f, mf = 0f;
                    Node mn;
                    if (tn.Child("mount", out mn))
                    {
                        mr = (float)mn.OptNum("right", 0d);
                        mu = (float)mn.OptNum("up", 0d);
                        mf = (float)mn.OptNum("forward", 0d);
                    }

                    float yawCenter = (float)tn.Num("yawCenter");
                    if (yawCenter < -180f || yawCenter > 180f)
                        log.Add(tn.Path + ".yawCenter 必须在 [-180,180]，当前 " + yawCenter);
                    float yawHalf = (float)tn.Num("yawHalfArc");
                    if (yawHalf < 0f || yawHalf > 180f)
                        log.Add(tn.Path + ".yawHalfArc 必须在 [0,180]，当前 " + yawHalf);

                    float pitchMin = (float)tn.OptNum("pitchMin", -10d);
                    float pitchMax = (float)tn.OptNum("pitchMax", 75d);
                    if (pitchMax <= pitchMin)
                        log.Add(tn.Path + ".pitchMax 必须大于 pitchMin");

                    float tReload = (float)tn.OptPositive("reloadSeconds", reload);
                    float tDamage = (float)tn.OptNonNeg("damage", gunDamage);
                    float tVelocity = (float)tn.OptPositive("shellVelocity", shellVelocity);
                    float tRange = (float)tn.OptPositive("range", gunRange);

                    string channelText = tn.OptStr("channel", "kinetic");
                    DamageChannel channel;
                    if (!TryChannel(channelText, out channel))
                        log.Add(tn.Path + ".channel 只能是 kinetic 或 beam，当前 " + channelText);

                    turretList.Add(new TurretProfile(tid, mr, mu, mf,
                        yawCenter, yawHalf, pitchMin, pitchMax,
                        tReload, tDamage, tVelocity, tRange, channel));
                }
            }

            // 缺字段与越界的错都收在这里，一次报全：改配置的人不希望改一个跑一次。
            if (log.Count > 0) return Fail(out errors, log, null);

            var maneuver = new ManeuverProfile(maxThrust,
                yawRate, pitchRate, rollRate, yawAccel, pitchAccel, rollAccel);
            var power = new PowerProfile(capacity, regen, propulsionDrain, turnDrain, weaponDrain, restartFraction);
            var armor = new ArmorProfile(beamDr, kineticDr);
            var shield = new ShieldProfile(frontDr, sideDr, rearDr,
                turnLossFactor, regenDelay, bufferRegen, drainPerDamage, recoverFraction);
            var overload = new OverloadProfile(threshold, windowSeconds, bufferCapacity, breakSeconds);

            // 构造后只剩「跨字段」的一致性：单个值都合法，组合起来才荒谬的那种。
            if (overload.BufferCapacity < overload.Threshold)
                log.Add(id + ": 缓冲池(" + overload.BufferCapacity + ") 小于过载阈值(" + overload.Threshold +
                        ")，一击就会破盾");
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
                overload: overload,
                turrets: turretList != null ? turretList.ToArray() : null);

            errors = new string[0];
            return true;
        }

        /// <summary>通道名区分大小写地解析。写错必须报错——静默当成动能会让光束武器悄悄打错装甲。</summary>
        private static bool TryChannel(string text, out DamageChannel channel)
        {
            channel = DamageChannel.Kinetic;
            if (string.IsNullOrEmpty(text)) return false;
            switch (text.Trim().ToLowerInvariant())
            {
                case "kinetic": channel = DamageChannel.Kinetic; return true;
                case "beam": channel = DamageChannel.Beam; return true;
                default: return false;
            }
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
        ///
        /// 每个取值方法都同时是校验方法：有限性 + 区间，越界就记错并返回 0。
        /// 返回 0 只是为了让调用方不必处理 null——只要 log 非空，函数一定在构造对象前就 return false。
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

            /// <summary>可缺省的字符串。给了就必须是字符串，缺省不算「放过坏值」。</summary>
            public string OptStr(string key, string fallback)
            {
                string v;
                if (_values != null && MiniJson.TryGetString(_values, key, out v)) return v;
                if (_values != null && _values.ContainsKey(key))
                {
                    _errors.Add(Where(key) + " 不是字符串");
                    return fallback;
                }
                return fallback;
            }

            /// <summary>必填的任意有限数。方位角、俯仰这类本来就可以为负的量不能用 Positive。</summary>
            public double Num(string key)
            {
                double v;
                if (!TryNumber(key, out v)) { _errors.Add(Where(key) + " 缺失或不是有限数"); return 0d; }
                return v;
            }

            public double OptNum(string key, double fallback)
            {
                double v;
                if (!TryNumber(key, out v))
                {
                    // 缺键 = 用缺省；键在但值坏 = 报错，不能当缺省吞掉。
                    if (_values != null && _values.ContainsKey(key))
                        _errors.Add(Where(key) + " 不是有限数");
                    return fallback;
                }
                return v;
            }

            /// <summary>可缺省的数组。缺键返回 null（调用方决定是否必需），键在但不是数组记错。</summary>
            public List<object> Array(string key)
            {
                List<object> list;
                if (_values != null && MiniJson.TryGetArray(_values, key, out list)) return list;
                if (_values != null && _values.ContainsKey(key))
                    _errors.Add(Where(key) + " 不是数组");
                return null;
            }

            /// <summary>数组里的一个对象元素。元素不是对象时记错并返回 null。</summary>
            public Node Element(object raw, string arrayKey, int index)
            {
                var d = raw as Dictionary<string, object>;
                if (d == null)
                {
                    _errors.Add(Where(arrayKey) + "[" + index + "] 不是对象");
                    return null;
                }
                return new Node(d, Where(arrayKey) + "[" + index + "]", _errors);
            }

            /// <summary>完整路径（如 combat.turrets[1]），供上面那些跨字段校验拼错误信息。</summary>
            public string Path => _path;

            /// <summary>正数。血量、半径、推力、射程这类「为零就没有意义」的量。</summary>
            public double Positive(string key)
            {
                double v;
                if (!TryNumber(key, out v)) { _errors.Add(Where(key) + " 缺失或不是有限数"); return 0d; }
                if (v <= 0d) { _errors.Add(Where(key) + " 必须为正，当前 " + Fmt(v)); return 0d; }
                return v;
            }

            /// <summary>非负数。0 有明确含义的量：再生、耗电、延迟。</summary>
            public double NonNeg(string key)
            {
                double v;
                if (!TryNumber(key, out v)) { _errors.Add(Where(key) + " 缺失或不是有限数"); return 0d; }
                if (v < 0d) { _errors.Add(Where(key) + " 不能为负，当前 " + Fmt(v)); return 0d; }
                return v;
            }

            /// <summary>[0,1]。减伤比例、精度这类「是比例不是倍数」的量。</summary>
            public double Unit(string key)
            {
                double v;
                if (!TryNumber(key, out v)) { _errors.Add(Where(key) + " 缺失或不是有限数"); return 0d; }
                if (v < 0d || v > 1d)
                {
                    _errors.Add(Where(key) + " 必须在 [0,1]（是减伤比例，不是倍数），当前 " + Fmt(v));
                    return 0d;
                }
                return v;
            }

            /// <summary>(0,1]。比例但 0 无意义：重启阈值、破盾恢复比例——填 0 会被契约层悄悄换成缺省值。</summary>
            public double OpenUnit(string key)
            {
                double v;
                if (!TryNumber(key, out v)) { _errors.Add(Where(key) + " 缺失或不是有限数"); return 0d; }
                if (v <= 0d || v > 1d)
                {
                    _errors.Add(Where(key) + " 必须在 (0,1]，当前 " + Fmt(v));
                    return 0d;
                }
                return v;
            }

            /// <summary>缺了用 fallback，给了就必须是正数（缺省不能顺便放过坏值）。</summary>
            public double OptPositive(string key, double fallback)
            {
                double v;
                if (!TryNumber(key, out v)) return fallback;
                if (v <= 0d) { _errors.Add(Where(key) + " 必须为正，当前 " + Fmt(v)); return fallback; }
                return v;
            }

            public double OptNonNeg(string key, double fallback)
            {
                double v;
                if (!TryNumber(key, out v)) return fallback;
                if (v < 0d) { _errors.Add(Where(key) + " 不能为负，当前 " + Fmt(v)); return fallback; }
                return v;
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

            /// <summary>取到数且是有限数才返回 true。缺字段与非有限数在这里合流，调用方各报各的话。</summary>
            private bool TryNumber(string key, out double v)
            {
                if (_values != null && MiniJson.TryGetNumber(_values, key, out v))
                    return !double.IsNaN(v) && !double.IsInfinity(v);
                v = 0d;
                return false;
            }

            private static string Fmt(double v)
                => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

            private string Where(string key)
                => (_path.Length == 0 ? "" : _path + ".") + key;
        }
    }
}
