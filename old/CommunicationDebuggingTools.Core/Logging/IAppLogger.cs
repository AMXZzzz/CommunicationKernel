using System;
using System.Collections.Generic;

namespace CommunicationDebuggingTools.Core.Logging {
    /// <summary>应用日志契约；实现可换内存 / 文件 / Serilog。</summary>
    public interface IAppLogger {
        /// <summary>有新日志时触发（实现方可在任意线程触发，UI 需自行调度）。</summary>
        event Action<LogEntry> EntryAdded;

        void Debug (string source, string message);
        void Info (string source, string message);
        void Warn (string source, string message);
        void Error (string source, string message);
        void Error (string source, string message, Exception ex);

        /// <summary>最近条目（新在后）；最多 capacity 条。</summary>
        IList<LogEntry> GetRecent ();

        void Clear ();
    }
}