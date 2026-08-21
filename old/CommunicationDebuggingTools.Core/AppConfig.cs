namespace CommunicationDebuggingTools.Core {

    /// <summary>
    /// 应用级常量集中配置。
    /// 替换散落在 Business / Plugin / UI 各层的魔法数字。
    /// 仅为只读常量（const / static readonly），不依赖任何外部状态。
    /// 协议特定默认端口定义在各自 Session 类内（Core 不应知道具体协议）。
    /// </summary>
    public static class AppConfig {

        // ── 超时 ─────────────────────────────────────
        /// <summary>TCP 连接 / 读写默认超时（毫秒）。</summary>
        public const int DefaultTimeoutMs = 3_000;

        /// <summary>端口可达性探测超时（毫秒）。</summary>
        public const int TcpProbeTimeoutMs = 1_000;

        // ── 心跳 ─────────────────────────────────────
        /// <summary>DeviceService.CheckConnections 调用间隔（秒）。</summary>
        public const int HeartbeatIntervalSeconds = 3;

        // ── 通讯故障阈值 ─────────────────────────────
        /// <summary>连续通讯失败 N 次后将设备标为 ALARM。</summary>
        public const int CommErrorThreshold = 3;

        // ── 轮询引擎 ─────────────────────────────────
        /// <summary>PollingEngine 主循环节拍（毫秒）；决定 ScanRateMs 最小粒度。</summary>
        public const int PollingTickMs = 100;

        /// <summary>Stop() 等待后台任务完成的超时（毫秒）。</summary>
        public const int PollingStopWaitMs = 5_000;

        // ── 日志 ─────────────────────────────────────
        /// <summary>MemoryAppLogger 环形缓冲默认容量（条）。</summary>
        public const int LogCapacity = 500;

        // ── EngineHost 默认端口 ───────────────────────
        public const int DefaultEngineHostGrpcPort = 5100;
        public const int DefaultEngineHostWebPort = 5101;

        // ── WPF 远端探测与自启策略默认值 ─────────────
        public const int RemoteProbeIntervalMs = 1000;
        public const int EngineHostRestartAfterSeconds = 10;
        public const int EngineHostStartRetryIntervalSeconds = 10;
        public const int EngineHostStartMaxAttempts = 3;
        public const int EngineHostStartWindowSeconds = 60;
    }
}
