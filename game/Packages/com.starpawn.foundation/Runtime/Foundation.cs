// Foundation 只放少量稳定基础类型：ID、错误、时间、随机源。
// 不放 PlayerState、GameManager 或任何完整游戏状态。

using System;

namespace StarPawn.Foundation
{
    /// <summary>实体 ID。各模块用自己的工厂创建，禁止跨模块直接构造。</summary>
    public readonly struct EntityId : IEquatable<EntityId>
    {
        private readonly string _value;
        public EntityId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("ID 不能为空", nameof(value));
            _value = value;
        }
        public bool IsEmpty => string.IsNullOrEmpty(_value);
        public bool Equals(EntityId other) => _value == other._value;
        public override bool Equals(object obj) => obj is EntityId id && Equals(id);
        public override int GetHashCode() => _value == null ? 0 : _value.GetHashCode();
        public override string ToString() => _value ?? string.Empty;
        public static bool operator ==(EntityId a, EntityId b) => a.Equals(b);
        public static bool operator !=(EntityId a, EntityId b) => !a.Equals(b);
    }

    /// <summary>内容 ID：指向 content/ 下的配置文件。</summary>
    public readonly struct ContentId : IEquatable<ContentId>
    {
        private readonly string _value;
        public ContentId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("内容 ID 不能为空", nameof(value));
            _value = value;
        }
        public bool Equals(ContentId other) => _value == other._value;
        public override bool Equals(object obj) => obj is ContentId id && Equals(id);
        public override int GetHashCode() => _value == null ? 0 : _value.GetHashCode();
        public override string ToString() => _value ?? string.Empty;
        public static bool operator ==(ContentId a, ContentId b) => a.Equals(b);
        public static bool operator !=(ContentId a, ContentId b) => !a.Equals(b);
    }

    /// <summary>统一错误码。领域模块只返回这些错误，不抛跨模块异常。</summary>
    public readonly struct Error
    {
        public readonly string Code;
        public readonly string Message;
        public Error(string code, string message)
        {
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
        }
        public override string ToString() => $"{Code}: {Message}";
        public static Error None => new Error(string.Empty, string.Empty);
    }

    /// <summary>命令返回。所有对外命令都用它，不用 bool + out 混搭。</summary>
    public readonly struct CommandResult
    {
        public bool Success { get; }
        public Error Error { get; }
        private CommandResult(bool ok, Error err) { Success = ok; Error = err; }
        public static CommandResult Ok() => new CommandResult(true, Error.None);
        public static CommandResult Fail(string code, string msg) => new CommandResult(false, new Error(code, msg));
    }

    /// <summary>泛型结果：返回值或错误，二者必居其一。</summary>
    public readonly struct Result<T>
    {
        public bool Success { get; }
        public T Value { get; }
        public Error Error { get; }
        private Result(bool ok, T value, Error err) { Success = ok; Value = value; Error = err; }
        public static Result<T> Ok(T value) => new Result<T>(true, value, Error.None);
        public static Result<T> Fail(string code, string msg) => new Result<T>(false, default, new Error(code, msg));
    }

    /// <summary>确定性随机源。战斗可复现的前提，禁止直接用 UnityEngine.Random。</summary>
    public interface IRandomSource
    {
        float NextFloat();
        int NextInt(int minInclusive, int maxExclusive);
    }

    /// <summary>线性同余随机源，同一 seed 必然同一序列。放在 Foundation 供各模块使用。</summary>
    public sealed class DeterministicRandom : IRandomSource
    {
        private long _state;
        public DeterministicRandom(int seed) { _state = seed & 0x7FFFFFFF; }
        private long Next()
        {
            _state = (_state * 48271L) % 2147483647L;
            return _state;
        }
        public float NextFloat() => (Next() - 1) / 2147483646f;
        public int NextInt(int minInclusive, int maxExclusive)
            => minInclusive + (int)(NextFloat() * (maxExclusive - minInclusive));
    }

    /// <summary>仿真时钟。固定步长由应用层统一定义，战斗模块不自己决定推进节奏。</summary>
    public readonly struct SimClock
    {
        public readonly int Tick;
        public readonly float FixedStep;
        public SimClock(int tick, float fixedStep) { Tick = tick; FixedStep = fixedStep; }
        public float Seconds => Tick * FixedStep;
        public SimClock Advance() => new SimClock(Tick + 1, FixedStep);
    }
}
