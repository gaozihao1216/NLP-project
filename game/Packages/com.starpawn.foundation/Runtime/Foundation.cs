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

    /// <summary>
    /// 状态检查点。跨模块原子提交用：工作流开始前 Capture，任一步失败 Restore。
    /// 单机同步执行，无并发观察者，回滚等价于原子性。
    /// </summary>
    public interface IStateCheckpoint
    {
        object CaptureState();
        void RestoreState(object state);
    }

    /// <summary>任务奖励定义。内容驱动：由 Infrastructure 从 missions/*.json 读出，注入应用层。</summary>
    public sealed class MissionReward
    {
        public int Merit;
        public int Credits;
    }

    /// <summary>任务奖励来源。应用层只认接口，不关心 JSON 还是数据库。</summary>
    public interface IMissionRewardSource
    {
        MissionReward Get(string missionId);
    }

    /// <summary>单位约定。星图存光分整数，战斗用公里浮点，只在进战斗时换算一次。</summary>
    public static class DistanceUnits
    {
        public const double KilometersPerLightMinute = 17987547.48;
        public const long LightMinutesPerLightYear = 525960;
        public static double ToKilometers(long lightMinutes) => lightMinutes * KilometersPerLightMinute;
        public static long ToLightMinutes(double kilometers) => (long)Math.Round(kilometers / KilometersPerLightMinute);
    }

    /// <summary>确定性随机源。战斗可复现的前提，禁止直接用 UnityEngine.Random。</summary>
    public interface IRandomSource
    {
        float NextFloat();
        int NextInt(int minInclusive, int maxExclusive);
    }

    /// <summary>
    /// 线性同余随机源。两个修正：零种子映射为 1（否则退化）；
    /// NextInt 用整数取模而非 float 乘法，避免浮点舍入把上界包含进来。
    /// </summary>
    public sealed class DeterministicRandom : IRandomSource
    {
        private long _state;
        public DeterministicRandom(int seed)
        {
            long s = seed & 0x7FFFFFFF;
            _state = s == 0 ? 1 : s;
        }
        private long Next()
        {
            _state = (_state * 48271L) % 2147483647L;
            return _state;
        }
        public float NextFloat()
        {
            // state 范围 1..2147483646，减 1 后为 0..2147483645。
            // 用 double 中转，避免 float 舍入成 1.0f。
            return (float)((double)(Next() - 1) / 2147483646.0);
        }
        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                throw new ArgumentException("maxExclusive 必须大于 minInclusive", nameof(maxExclusive));
            long range = (long)maxExclusive - minInclusive;
            return minInclusive + (int)(Next() % range);
        }
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
