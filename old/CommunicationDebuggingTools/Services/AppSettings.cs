using System;
using System.IO;
using System.Text.Json;
using CommunicationDebuggingTools.Core;

namespace CommunicationDebuggingTools.Services {

    /// <summary>
    /// 应用设置：运行模式 + EngineHost 地址。
    /// 持久化到 %AppData%/CommunicationDebuggingTools/settings.json。
    /// </summary>
    public sealed class AppSettings {

        // ── 默认值 ──────────────────────────────────
        public static readonly string DefaultHostAddress = "http://127.0.0.1:" + AppConfig.DefaultEngineHostGrpcPort;

        // ── 属性 ────────────────────────────────────
        /// <summary>true = 连接远端 EngineHost；false = 本地 Business 层直连。</summary>
        public bool RemoteMode { get; set; } = false;

        /// <summary>EngineHost gRPC 地址。</summary>
        public string HostAddress { get; set; } = DefaultHostAddress;

        /// <summary>远端在线检测周期（毫秒）。</summary>
        public int RemoteProbeIntervalMs { get; set; } = AppConfig.RemoteProbeIntervalMs;

        /// <summary>离线达到该秒数后允许尝试拉起 EngineHost。</summary>
        public int EngineHostRestartAfterSeconds { get; set; } = AppConfig.EngineHostRestartAfterSeconds;

        /// <summary>两次拉起尝试之间最小间隔（秒）。</summary>
        public int EngineHostStartRetryIntervalSeconds { get; set; } = AppConfig.EngineHostStartRetryIntervalSeconds;

        /// <summary>滑动窗口内最大拉起尝试次数。</summary>
        public int EngineHostStartMaxAttempts { get; set; } = AppConfig.EngineHostStartMaxAttempts;

        /// <summary>拉起次数统计窗口（秒）。</summary>
        public int EngineHostStartWindowSeconds { get; set; } = AppConfig.EngineHostStartWindowSeconds;

        // ── 持久化 ───────────────────────────────────
        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CommunicationDebuggingTools", "settings.json");

        public static AppSettings Load () {
            try {
                if (File.Exists(_path)) {
                    string json = File.ReadAllText(_path);
                    return Normalize(JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings());
                }
            } catch { }
            return Normalize(new AppSettings());
        }

        public void Save () {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(Normalize(this),
                    new JsonSerializerOptions { WriteIndented = true }));
            } catch { }
        }

        private static AppSettings Normalize (AppSettings settings) {
            if (settings == null)
                settings = new AppSettings();

            if (string.IsNullOrWhiteSpace(settings.HostAddress))
                settings.HostAddress = DefaultHostAddress;

            if (settings.RemoteProbeIntervalMs <= 0)
                settings.RemoteProbeIntervalMs = AppConfig.RemoteProbeIntervalMs;
            if (settings.EngineHostRestartAfterSeconds <= 0)
                settings.EngineHostRestartAfterSeconds = AppConfig.EngineHostRestartAfterSeconds;
            if (settings.EngineHostStartRetryIntervalSeconds <= 0)
                settings.EngineHostStartRetryIntervalSeconds = AppConfig.EngineHostStartRetryIntervalSeconds;
            if (settings.EngineHostStartMaxAttempts <= 0)
                settings.EngineHostStartMaxAttempts = AppConfig.EngineHostStartMaxAttempts;
            if (settings.EngineHostStartWindowSeconds <= 0)
                settings.EngineHostStartWindowSeconds = AppConfig.EngineHostStartWindowSeconds;

            return settings;
        }
    }
}
