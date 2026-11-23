// 基础设施：内容加载、校验、存档。依赖契约与 .NET 文件/JSON API，不依赖业务流程。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StarPawn.Foundation;

namespace StarPawn.Infrastructure
{
    public interface IContentValidator
    {
        string[] Validate(string contentRoot);
    }

    public interface ISaveStore
    {
        void Save(string slot, string json);
        string Load(string slot);
    }

    /// <summary>
    /// 最小内容校验：真正读取 content/ 下所有 JSON，检查
    /// ① 文件可解析 ② id 唯一且非空 ③ 跨文件 ID 引用不悬空。
    /// 当前覆盖本项目的 6 类配置；工具与运行时共用，保证内容格式一致。
    /// </summary>
    public sealed class BasicContentValidator : IContentValidator
    {
        private static readonly string[] SubDirs =
            { "ships", "factions", "star-maps", "missions", "stories", "quests" };

        public string[] Validate(string contentRoot)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(contentRoot) || !Directory.Exists(contentRoot))
                return new[] { "内容目录不存在: " + (contentRoot ?? "(null)") };

            var allIds = new HashSet<string>();
            var nodeIds = new HashSet<string>();       // 剧情节点（stories/*.json 的 nodes 键）
            var starNodes = new HashSet<string>();     // 星图节点
            var questIds = new HashSet<string>();
            var docs = new List<JsonElement>();

            foreach (var sub in SubDirs)
            {
                var dir = Path.Combine(contentRoot, sub);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    JsonElement root;
                    try
                    {
                        using (var stream = File.OpenRead(file))
                            root = JsonDocument.Parse(stream).RootElement.Clone();
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"JSON 解析失败 {Path.GetFileName(file)}: {ex.Message}");
                        continue;
                    }
                    docs.Add(root);
                    var name = Path.GetFileName(file);

                    if (root.ValueKind != JsonValueKind.Object)
                    { errors.Add($"{name}: 根节点必须是对象"); continue; }

                    string id = GetString(root, "id");
                    if (string.IsNullOrWhiteSpace(id))
                        errors.Add($"{name}: 缺少 id");
                    else if (!allIds.Add(id))
                        errors.Add($"{name}: id 重复 ({id})");

                    if (sub == "quests" && !string.IsNullOrEmpty(id)) questIds.Add(id);

                    if (sub == "stories" && root.TryGetProperty("nodes", out var nodes)
                        && nodes.ValueKind == JsonValueKind.Object)
                        foreach (var p in nodes.EnumerateObject()) nodeIds.Add(p.Name);

                    if (sub == "star-maps" && root.TryGetProperty("nodes", out var sn)
                        && sn.ValueKind == JsonValueKind.Array)
                        foreach (var n in sn.EnumerateArray())
                        {
                            var nid = GetString(n, "id");
                            if (!string.IsNullOrEmpty(nid)) starNodes.Add(nid);
                        }
                }
            }

            // 引用检查：剧情 next/StoryNode、任务 references/navigation/prerequisites、舰队配置
            foreach (var root in docs)
            {
                if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Object)
                {
                    foreach (var node in nodes.EnumerateObject())
                    {
                        if (node.Value.ValueKind != JsonValueKind.Object) continue;
                        if (node.Value.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                            foreach (var c in choices.EnumerateArray())
                            {
                                var next = GetString(c, "next");
                                if (!string.IsNullOrEmpty(next) && !nodeIds.Contains(next))
                                    errors.Add($"剧情节点 {node.Name} 的选项指向不存在的节点: {next}");
                            }
                    }
                }

                if (root.TryGetProperty("navigation", out var nav) && nav.ValueKind == JsonValueKind.Object)
                {
                    var target = GetString(nav, "targetSystemId");
                    if (!string.IsNullOrEmpty(target) && !starNodes.Contains(target))
                        errors.Add($"导航目标星系不存在: {target}");
                }

                if (root.TryGetProperty("references", out var refs) && refs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "startStoryNode", "endStoryNode", "storyNode" })
                    {
                        var v = GetString(refs, key);
                        if (!string.IsNullOrEmpty(v) && !nodeIds.Contains(v))
                            errors.Add($"任务引用的剧情节点不存在: {v}");
                    }
                    var mission = GetString(refs, "missionId");
                    if (!string.IsNullOrEmpty(mission) && !allIds.Contains(mission))
                        errors.Add($"任务引用的 mission 不存在: {mission}");
                }

                if (root.TryGetProperty("prerequisites", out var pre) && pre.ValueKind == JsonValueKind.Object
                    && pre.TryGetProperty("allOf", out var all) && all.ValueKind == JsonValueKind.Array)
                    foreach (var p in all.EnumerateArray())
                    {
                        var type = GetString(p, "type");
                        var value = GetString(p, "value");
                        if (type == "QuestCompleted" && !string.IsNullOrEmpty(value) && !questIds.Contains(value))
                            errors.Add($"前置任务不存在: {value}");
                    }
            }

            // 代码里写死引用的内容 ID：这些如果没配置，运行时会拿到空定义。
            foreach (var required in new[] { "ship.g_cruiser", "ship.d_destroyer", "ship.a_patrol" })
                if (!allIds.Contains(required)) errors.Add($"代码引用的舰船配置缺失: {required}");

            return errors.ToArray();
        }

        private static string GetString(JsonElement obj, string key)
            => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
               && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// 内容驱动的任务奖励。从 missions/*.json 读取，修改 JSON 会直接改变结算数值。
    /// 找不到时返回 null，由调用方决定降级策略。
    /// </summary>
    public sealed class JsonMissionRewardSource : IMissionRewardSource
    {
        private readonly Dictionary<string, MissionReward> _rewards = new Dictionary<string, MissionReward>();

        public JsonMissionRewardSource(string contentRoot)
        {
            if (string.IsNullOrEmpty(contentRoot) || !Directory.Exists(contentRoot)) return;
            var dir = Path.Combine(contentRoot, "missions");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    using (var stream = File.OpenRead(file))
                    using (var doc = JsonDocument.Parse(stream))
                    {
                        var root = doc.RootElement;
                        var id = GetStr(root, "id");
                        if (string.IsNullOrEmpty(id) || !root.TryGetProperty("rewards", out var rw)) continue;
                        _rewards[id] = new MissionReward
                        {
                            Merit = GetInt(rw, "merit"),
                            Credits = GetInt(rw, "credits")
                        };
                    }
                }
                catch (Exception)
                {
                    // 校验器负责报错；这里跳过坏文件，避免整个游戏起不来。
                }
            }
        }

        public MissionReward Get(string missionId)
        {
            MissionReward r;
            return _rewards.TryGetValue(missionId, out r) ? r : null;
        }

        private static string GetStr(JsonElement o, string k)
            => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v)
               && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static int GetInt(JsonElement o, string k)
            => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v)
               && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    }

    /// <summary>单机存档，用一次性写入，避免半成品存档。</summary>
    public sealed class MemorySaveStore : ISaveStore
    {
        private readonly Dictionary<string, string> _slots = new Dictionary<string, string>();
        public void Save(string slot, string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("存档内容不能为空");
            _slots[slot] = json;
        }
        public string Load(string slot)
        {
            string v; return _slots.TryGetValue(slot, out v) ? v : null;
        }
    }
}
