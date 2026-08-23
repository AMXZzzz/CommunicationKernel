// -----------------------------------------------------------------------------
// 文件: Services/AppLogStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 进程内环形日志缓冲，供通讯日志页订阅；禁止每打开一次页面就挂一个 ILoggerProvider。
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;

namespace CommunicationKernel.UI.Web.Services;

/// <summary>一条面向操作员的日志。</summary>
public sealed class AppLogEntry
{
    public DateTime Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    public string LevelText => Level switch
    {
        LogLevel.Warning => "WRN",
        LogLevel.Error or LogLevel.Critical => "ERR",
        LogLevel.Debug or LogLevel.Trace => "DBG",
        _ => "INF",
    };

    public string LevelClass => Level switch
    {
        LogLevel.Warning => "warn",
        LogLevel.Error or LogLevel.Critical => "error",
        LogLevel.Debug or LogLevel.Trace => "muted",
        _ => "info",
    };
}

/// <summary>线程安全环形缓冲。后台线程写入，Blazor 通过 <see cref="Changed"/> 刷新。</summary>
public sealed class AppLogStore
{
    private const int Capacity = 2000;
    private readonly ConcurrentQueue<AppLogEntry> _entries = new();
    private int _count;

    /// <summary>有新条目时在写入线程触发，订阅方必须 <c>InvokeAsync</c> 回 UI 线程。</summary>
    public event Action? Changed;

    public IReadOnlyList<AppLogEntry> Snapshot() => _entries.ToArray();

    public void Append(LogLevel level, string category, string message)
    {
        var entry = new AppLogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category ?? string.Empty,
            Message = message ?? string.Empty,
        };
        _entries.Enqueue(entry);
        int n = Interlocked.Increment(ref _count);
        while (n > Capacity && _entries.TryDequeue(out _))
            n = Interlocked.Decrement(ref _count);
        Changed?.Invoke();
    }

    public void Info(string category, string message) => Append(LogLevel.Information, category, message);
    public void Warn(string category, string message) => Append(LogLevel.Warning, category, message);
    public void Error(string category, string message) => Append(LogLevel.Error, category, message);

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _count, 0);
        Changed?.Invoke();
    }
}
