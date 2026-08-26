// -----------------------------------------------------------------------------
// 文件: Services/AppLogStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 进程内环形日志缓冲，供通讯日志页订阅；禁止每打开一次页面就挂一个 ILoggerProvider。
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>一条面向操作员的日志。</summary>
/// <remarks>
/// 不可变（全部 <c>init</c>）：条目一旦入队就可能被任意数量的渲染线程读取，
/// 可变字段会在快照期间被改写，导致同一页里同一条日志前后不一致。
/// </remarks>
public sealed class AppLogEntry
{
    /// <summary>记录时刻，取本地时间——操作员对照的是现场墙上的钟。</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>日志级别，决定显示的标签与配色。</summary>
    public LogLevel Level { get; init; }

    /// <summary>来源分类，例如 "Devices" / "Variables" / "Host"，用于页面筛选。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>面向操作员的正文。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 列表里显示的三字母级别标签。
    /// </summary>
    /// <remarks>
    /// 固定三字符宽度，等宽字体下各行天然对齐，扫读时级别列成一条直线。
    /// Critical 并入 ERR、Trace 并入 DBG：操作员的处置动作相同，多分一档没有意义。
    /// </remarks>
    public string LevelText => Level switch
    {
        LogLevel.Warning => "WRN",
        LogLevel.Error or LogLevel.Critical => "ERR",
        LogLevel.Debug or LogLevel.Trace => "DBG",
        _ => "INF",
    };

    /// <summary>该级别对应的 CSS 类名，与 theme.css 中的 .log-line 修饰类对应。</summary>
    public string LevelClass => Level switch
    {
        LogLevel.Warning => "warn",
        LogLevel.Error or LogLevel.Critical => "error",
        LogLevel.Debug or LogLevel.Trace => "muted",
        _ => "info",
    };
}

/// <summary>线程安全环形缓冲。后台线程写入，Blazor 通过 <see cref="Changed"/> 刷新。</summary>
/// <remarks>
/// 单例。日志来自多个后台线程（轮询器、健康探测、gRPC 回调），
/// 而读取来自 Blazor 的渲染线程，因此必须线程安全。
/// </remarks>
public sealed class AppLogStore
{
    /// <summary>
    /// 缓冲上限。超出后丢弃最旧的条目。
    /// </summary>
    /// <remarks>
    /// 必须有上限：一台设备离线后轮询会持续产生失败日志，
    /// 无上限的缓冲会在无人值守的产线上稳步吃光内存。
    /// 2000 条约能覆盖数小时的正常运行，足够回溯一次班次内的异常。
    /// </remarks>
    private const int Capacity = 2000;

    /// <summary>条目队列，先进先出。</summary>
    private readonly ConcurrentQueue<AppLogEntry> _entries = new();

    /// <summary>
    /// 当前条目数。
    /// </summary>
    /// <remarks>
    /// 单独维护而不用 <c>_entries.Count</c>：<see cref="ConcurrentQueue{T}"/> 的 Count
    /// 需要遍历整个队列，在每条日志都要检查容量的热路径上代价过高。
    /// </remarks>
    private int _count;

    /// <summary>有新条目时在写入线程触发，订阅方必须 <c>InvokeAsync</c> 回 UI 线程。</summary>
    public event Action? Changed;

    /// <summary>
    /// 取当前全部条目的快照。
    /// </summary>
    /// <remarks>
    /// 返回数组副本而非队列本身：调用方是 Blazor 渲染，
    /// 渲染期间若底层集合被后台线程修改会抛异常。
    /// </remarks>
    public IReadOnlyList<AppLogEntry> Snapshot() => _entries.ToArray();

    /// <summary>
    /// 追加一条日志，必要时丢弃最旧条目以维持容量上限。
    /// </summary>
    /// <param name="level">日志级别。</param>
    /// <param name="category">来源分类，null 归一为空字符串。</param>
    /// <param name="message">正文，null 归一为空字符串。</param>
    /// <remarks>
    /// 裁剪用 <c>while</c> 而非 <c>if</c>：多个线程可能同时入队，
    /// 单次检查会让队列在高并发下持续超出上限。
    /// <c>TryDequeue</c> 失败即退出循环——那说明别的线程已经裁剪过了。
    /// </remarks>
    public void Append(LogLevel level, string category, string message)
    {
        var entry = new AppLogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            // Protobuf 与部分调用方可能传 null，统一归一避免下游到处判空
            Category = category ?? string.Empty,
            Message = message ?? string.Empty,
        };

        _entries.Enqueue(entry);

        // 先增后裁：计数与队列之间存在短暂不一致，但只影响裁剪时机，不影响正确性
        int n = Interlocked.Increment(ref _count);
        while (n > Capacity && _entries.TryDequeue(out _))
            n = Interlocked.Decrement(ref _count);

        Changed?.Invoke();
    }

    /// <summary>记录一条信息级日志。</summary>
    public void Info(string category, string message) => Append(LogLevel.Information, category, message);

    /// <summary>记录一条警告级日志。</summary>
    public void Warn(string category, string message) => Append(LogLevel.Warning, category, message);

    /// <summary>记录一条错误级日志。</summary>
    public void Error(string category, string message) => Append(LogLevel.Error, category, message);

    /// <summary>
    /// 清空缓冲。
    /// </summary>
    /// <remarks>
    /// 逐条出队而非新建队列：<c>_entries</c> 是 readonly，
    /// 且并发写入方持有的是同一个引用，替换引用会让在途的写入落进被丢弃的队列。
    /// </remarks>
    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }

        Interlocked.Exchange(ref _count, 0);
        Changed?.Invoke();
    }
}
