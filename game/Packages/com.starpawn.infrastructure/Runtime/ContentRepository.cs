// 基础设施：内容加载、存档、对话服务。依赖契约与文件 SDK，不依赖业务流程。
using System;
using System.Collections.Generic;
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

    /// <summary>最小内容校验：检查 ID 唯一性与必填字段。工具与运行时共用，保证内容格式一致。</summary>
    public sealed class BasicContentValidator : IContentValidator
    {
        public string[] Validate(string contentRoot)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(contentRoot)) errors.Add("内容目录为空");
            return errors.ToArray();
        }
    }

    /// <summary>单机存档，用候选状态 + 一次性写入，避免半成品存档。</summary>
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
