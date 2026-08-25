// -----------------------------------------------------------------------------
// 文件: Services/AppLogLoggerProvider.cs
// 层级: UI 层 — Blazor Server
// 作用: 把框架 ILogger 接到 AppLogStore；框架噪音在 Information 以下丢弃。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.Web.Services;

/// <summary>单例 Provider，应用生命周期内只注册一次。</summary>
/// <remarks>
/// 必须是单例：<see cref="ILoggerProvider"/> 每注册一次就会为每个 Category
/// 多产生一个 Sink，日志会按注册次数成倍写入 <see cref="AppLogStore"/>，
/// 页面上表现为每条日志重复若干遍。
/// </remarks>
public sealed class AppLogLoggerProvider : ILoggerProvider
{
    /// <summary>日志汇聚目标，由 DI 注入的单例。</summary>
    private readonly AppLogStore _store;

    /// <param name="store">日志缓冲，必填。</param>
    public AppLogLoggerProvider(AppLogStore store) => _store = store;

    /// <summary>为指定分类创建一个写入 <see cref="AppLogStore"/> 的记录器。</summary>
    /// <param name="categoryName">框架给出的完整类型名，例如 <c>Microsoft.AspNetCore.Xxx</c>。</param>
    public ILogger CreateLogger(string categoryName) => new Sink(_store, categoryName);

    /// <summary>
    /// 无需释放：本 Provider 不持有任何非托管资源，
    /// <see cref="AppLogStore"/> 的生命周期由 DI 容器管理。
    /// </summary>
    public void Dispose() { }

    /// <summary>
    /// 单个分类的日志出口，把框架日志转写进 <see cref="AppLogStore"/>。
    /// </summary>
    private sealed class Sink : ILogger
    {
        /// <summary>日志汇聚目标。</summary>
        private readonly AppLogStore _store;

        /// <summary>本 Sink 负责的完整分类名。</summary>
        private readonly string _category;

        /// <param name="store">日志缓冲。</param>
        /// <param name="category">完整分类名。</param>
        public Sink(AppLogStore store, string category)
        {
            _store = store;
            _category = category;
        }

        /// <summary>
        /// 不支持日志作用域。
        /// </summary>
        /// <remarks>
        /// 返回 null 是合法实现。操作员日志是扁平的时间线，
        /// 作用域嵌套信息在这个界面上没有展示位置，实现它只会增加分配。
        /// </remarks>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>
        /// 判定某级别的日志是否应当进入操作员日志。
        /// </summary>
        /// <remarks>
        /// 两道闸门：
        /// <list type="number">
        ///   <item>Debug / Trace 一律丢弃——那是开发期诊断，不是现场信息。</item>
        ///   <item>框架分类（Microsoft.* / System.*）的 Information 也丢弃：
        ///         每个 HTTP 请求、每次线路连接都会产生一条，
        ///         几分钟就能把真正的通讯日志顶出 2000 条的环形缓冲。
        ///         但框架的 Warning 以上要保留——线路断开、端口冲突都从那里来。</item>
        /// </list>
        /// </remarks>
        public bool IsEnabled(LogLevel logLevel)
        {
            if (logLevel < LogLevel.Information) return false;

            // 框架流水不要灌进操作员日志；Warning 以上仍保留
            if (logLevel < LogLevel.Warning && IsFramework(_category)) return false;

            return true;
        }

        /// <summary>
        /// 格式化并写入一条日志。
        /// </summary>
        /// <remarks>
        /// 异常的类型名与消息要拼进正文：<see cref="AppLogEntry"/> 没有异常字段，
        /// 而"哪一类异常"往往是现场排查的第一线索
        /// （<c>SocketException</c> 与 <c>TimeoutException</c> 的处置完全不同）。
        /// 堆栈不拼——操作员看不懂，且会撑爆列表行高。
        /// </remarks>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // 提前返回，避免为被过滤掉的日志付出格式化代价
            if (!IsEnabled(logLevel)) return;

            string message = formatter(state, exception);

            if (exception is not null)
                message += " [" + exception.GetType().Name + "] " + exception.Message;

            _store.Append(logLevel, Shorten(_category), message);
        }

        /// <summary>该分类是否来自 .NET 框架而非本应用。</summary>
        private static bool IsFramework(string category) =>
            category.StartsWith("Microsoft.", StringComparison.Ordinal) ||
            category.StartsWith("System.", StringComparison.Ordinal);

        /// <summary>
        /// 取完整分类名的最后一段作为显示分类。
        /// </summary>
        /// <remarks>
        /// 完整命名空间会把日志列表的分类列撑得很宽，把正文挤没。
        /// 末尾就是点号时（异常输入）回落到原串，避免返回空字符串。
        /// </remarks>
        private static string Shorten(string category)
        {
            int i = category.LastIndexOf('.');
            return i >= 0 && i < category.Length - 1 ? category[(i + 1)..] : category;
        }
    }
}
