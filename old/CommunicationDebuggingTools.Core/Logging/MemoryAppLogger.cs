using System;
using System.Collections.Generic;
using System.Linq;

namespace CommunicationDebuggingTools.Core.Logging {
    /// <summary>进程内环形日志，默认保留最近 500 条。</summary>
    public sealed class MemoryAppLogger : IAppLogger {
        private readonly object _sync = new object();
        private readonly Queue<LogEntry> _entries = new Queue<LogEntry>();
        private readonly int _capacity;

        public event Action<LogEntry> EntryAdded;

        public MemoryAppLogger (int capacity = 500) {
            _capacity = capacity < 50 ? 50 : capacity;
        }

        public void Debug (string source, string message) =>
            Add(LogLevel.Debug, source, message);

        public void Info (string source, string message) =>
            Add(LogLevel.Info, source, message);

        public void Warn (string source, string message) =>
            Add(LogLevel.Warn, source, message);

        public void Error (string source, string message) =>
            Add(LogLevel.Error, source, message);

        public void Error (string source, string message, Exception ex) {
            string detail = message ?? "";
            if (ex != null)
                detail = detail + " | " + ex.GetType().Name + ": " + ex.Message;
            Add(LogLevel.Error, source, detail);
        }

        public IList<LogEntry> GetRecent () {
            lock (_sync)
                return _entries.ToList();
        }

        public void Clear () {
            lock (_sync)
                _entries.Clear();
        }

        private void Add (LogLevel level, string source, string message) {
            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Source = source ?? "",
                Message = message ?? ""
            };

            lock (_sync) {
                _entries.Enqueue(entry);
                while (_entries.Count > _capacity)
                    _entries.Dequeue();
            }

            Action<LogEntry> h = EntryAdded;
            if (h != null) {
                try { h(entry); } catch { }
            }
        }
    }
}