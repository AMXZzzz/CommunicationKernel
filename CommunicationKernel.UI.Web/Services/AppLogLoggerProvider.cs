// -----------------------------------------------------------------------------
// 文件: Services/AppLogLoggerProvider.cs
// 层级: UI 层 — Blazor Server
// 作用: 把框架 ILogger 接到 AppLogStore；框架噪音在 Information 以下丢弃。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.Web.Services;

/// <summary>单例 Provider，应用生命周期内只注册一次。</summary>
public sealed class AppLogLoggerProvider : ILoggerProvider
{
    private readonly AppLogStore _store;

    public AppLogLoggerProvider(AppLogStore store) => _store = store;

    public ILogger CreateLogger(string categoryName) => new Sink(_store, categoryName);

    public void Dispose() { }

    private sealed class Sink : ILogger
    {
        private readonly AppLogStore _store;
        private readonly string _category;

        public Sink(AppLogStore store, string category)
        {
            _store = store;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (logLevel < LogLevel.Information) return false;
            // 框架流水不要灌进操作员日志；Warning 以上仍保留
            if (logLevel < LogLevel.Warning && IsFramework(_category)) return false;
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message = formatter(state, exception);
            if (exception is not null)
                message += " [" + exception.GetType().Name + "] " + exception.Message;
            _store.Append(logLevel, Shorten(_category), message);
        }

        private static bool IsFramework(string category) =>
            category.StartsWith("Microsoft.", StringComparison.Ordinal) ||
            category.StartsWith("System.", StringComparison.Ordinal);

        private static string Shorten(string category)
        {
            int i = category.LastIndexOf('.');
            return i >= 0 && i < category.Length - 1 ? category[(i + 1)..] : category;
        }
    }
}
