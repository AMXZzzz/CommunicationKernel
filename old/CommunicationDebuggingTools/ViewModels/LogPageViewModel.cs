using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunicationDebuggingTools.Core;
using CommunicationDebuggingTools.Core.Logging;

namespace CommunicationDebuggingTools.ViewModels {

    /// <summary>
    /// 通讯日志页 ViewModel。
    /// 订阅 IAppLogger.EntryAdded；Page 在 UI 线程调用 AppendEntry。
    ///
    /// 实现 IDisposable：VM 是 AddTransient，每次导航都会新建。
    /// Page.Unloaded → Dispose()，保证退订，防止 MemoryAppLogger 持有旧 VM 引用导致内存泄漏。
    /// </summary>
    public sealed class LogPageViewModel : ViewModelBase, IDisposable {

        private readonly IAppLogger _logger;
        private bool _disposed;

        public ObservableCollection<LogEntry> Entries { get; } =
            new ObservableCollection<LogEntry>();

        public ICommand ClearCommand { get; }

        /// <summary>后台线程产生新条目；Page 订阅后通过 Dispatcher 转到 UI 线程。</summary>
        public event Action<LogEntry> EntryAdded;

        public LogPageViewModel (IAppLogger logger) {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ClearCommand = new RelayCommand(Clear);

            foreach (LogEntry e in _logger.GetRecent())
                Entries.Add(e);

            _logger.EntryAdded += OnLoggerEntryAdded;
        }

        private void OnLoggerEntryAdded (LogEntry entry) =>
            EntryAdded?.Invoke(entry);

        /// <summary>在 UI 线程调用。超出容量时移除最旧条目。</summary>
        public void AppendEntry (LogEntry entry) {
            if (entry == null || _disposed) return;
            Entries.Add(entry);
            while (Entries.Count > AppConfig.LogCapacity)
                Entries.RemoveAt(0);
        }

        public void Clear () {
            _logger.Clear();
            Entries.Clear();
        }

        /// <summary>
        /// 退订日志事件并释放资源。
        /// Page.Unloaded 里调用，防止 MemoryAppLogger 通过事件持有已失效 VM。
        /// </summary>
        public void Dispose () {
            if (_disposed) return;
            _disposed = true;
            _logger.EntryAdded -= OnLoggerEntryAdded;
        }
    }
}