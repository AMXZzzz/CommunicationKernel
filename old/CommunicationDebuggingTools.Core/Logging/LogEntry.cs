using System;

namespace CommunicationDebuggingTools.Core.Logging {
    /// <summary>单条日志（UI 绑定用）。</summary>
    public sealed class LogEntry {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public string Source { get; set; }
        public string Message { get; set; }

        public string LevelText {
            get {
                switch (Level) {
                    case LogLevel.Debug: return "DEBUG";
                    case LogLevel.Info: return "INFO";
                    case LogLevel.Warn: return "WARN";
                    case LogLevel.Error: return "ERROR";
                    default: return Level.ToString();
                }
            }
        }

        public string TimeText => Time.ToString("HH:mm:ss.fff");
    }
}