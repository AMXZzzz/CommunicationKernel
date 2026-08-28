// -----------------------------------------------------------------------------
// 文件: Services/WebTunnelSettings.cs
// 层级: UI 层 — WebMaster 内网穿透
// 作用: frpc 隧道的设置读写，以及"frpc.exe 在不在"的探测。
//
// 为什么不随包分发 frpc.exe:
//   内网穿透工具被国内杀软报毒是常态。一旦打进发布包，落地那一刻就会被扫，
//   放在哪个目录、跑不跑都一样——杀软扫的是磁盘文件，不是运行中的进程。
//   严重时整个 WebMaster 会被一起拦掉。
//
//   因此改成：我们的产物里<b>没有</b>任何穿透工具；需要的人自己从官方仓库下一个
//   放到 exe 旁边。WebMaster 探测到才启用该功能。
//   我们提供的价值（生成配置、托管进程、显示状态、日志汇聚）一点不少，
//   而"文件不在功能就不存在"也是最强的默认关闭。
//
// 隧道只是一半:
//   公网服务器上仍然要装 frps。那半边内嵌不了——隧道必须两头都有。
// -----------------------------------------------------------------------------

using CommunicationKernel.Hosting.Sdk;
using System.Text.Json;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>frpc 隧道设置。</summary>
/// <param name="Enabled">是否启用。即便为 true，找不到 frpc.exe 也不会启动。</param>
/// <param name="ServerAddr">公网服务器地址（IP 或域名）。</param>
/// <param name="ServerPort">frps 的 bindPort，默认 7000。</param>
/// <param name="Token">与 frps 的 auth.token 相同。<b>不得写进日志。</b></param>
/// <param name="RemotePort">在公网机上暴露的端口，反代指向它，默认 16400。</param>
public sealed record WebTunnelSettings(
    bool Enabled,
    string ServerAddr,
    int ServerPort,
    string Token,
    int RemotePort)
{
    /// <summary>默认：不启用。</summary>
    public static WebTunnelSettings Disabled { get; } =
        new(false, string.Empty, 7000, string.Empty, 16400);

    /// <summary>
    /// 配置是否完整到可以尝试连接。
    /// </summary>
    /// <remarks>
    /// 缺服务器地址或 token 就不启动——frpc 会连不上并反复重试，
    /// 日志里刷一堆失败，而根因只是没填完。
    /// </remarks>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ServerAddr)
        && !string.IsNullOrWhiteSpace(Token)
        && ServerPort is > 0 and <= 65535
        && RemotePort is > 0 and <= 65535;
}

/// <summary>读写 exe 旁 <c>config/web-tunnel.json</c>，并探测 frpc 可执行文件。</summary>
public sealed class WebTunnelSettingsStore
{
    /// <summary>期望的 frpc 文件名。</summary>
    public const string FrpcFileName = "frpc.exe";

    /// <summary>官方下载页，界面上给用户看。</summary>
    public const string FrpcDownloadUrl = "https://github.com/fatedier/frp/releases";

    /// <summary>保护写入的互斥锁。</summary>
    private readonly object _lock = new();

    /// <summary>应用日志。</summary>
    private readonly AppLogStore _log;

    /// <param name="log">应用日志。</param>
    public WebTunnelSettingsStore(AppLogStore log) => _log = log;

    /// <summary>配置文件完整路径，供设置页展示。</summary>
    public string FilePath => WebPaths.TunnelFile;

    /// <summary>frpc 应当放置的完整路径（exe 同目录）。</summary>
    public static string FrpcPath => Path.Combine(AppContext.BaseDirectory, FrpcFileName);

    /// <summary>frpc 是否已由用户自行放置。</summary>
    public static bool FrpcPresent => File.Exists(FrpcPath);

    /// <summary>
    /// 读取隧道设置。任何失败都回落到未启用。
    /// </summary>
    /// <remarks>
    /// 静态方法：Main.cs 要在容器建好之前判断是否注册 <see cref="FrpcHost"/>。
    /// </remarks>
    public static WebTunnelSettings Load()
    {
        try
        {
            string path = WebPaths.TunnelFile;
            if (!File.Exists(path)) return WebTunnelSettings.Disabled;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement r = doc.RootElement;

            return new WebTunnelSettings(
                r.TryGetProperty("Enabled", out JsonElement e) && e.ValueKind == JsonValueKind.True,
                r.TryGetProperty("ServerAddr", out JsonElement a) ? (a.GetString() ?? string.Empty) : string.Empty,
                r.TryGetProperty("ServerPort", out JsonElement sp) && sp.TryGetInt32(out int spv) ? spv : 7000,
                r.TryGetProperty("Token", out JsonElement t) ? (t.GetString() ?? string.Empty) : string.Empty,
                r.TryGetProperty("RemotePort", out JsonElement rp) && rp.TryGetInt32(out int rpv) ? rpv : 16400);
        }
        catch
        {
            return WebTunnelSettings.Disabled;
        }
    }

    /// <summary>保存隧道设置。下次启动生效。</summary>
    /// <exception cref="InvalidOperationException">落盘失败。</exception>
    public void Save(WebTunnelSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);

        lock (_lock)
        {
            if (!JsonFileStore.SaveObject(WebPaths.TunnelFile, new
            {
                s.Enabled,
                ServerAddr = (s.ServerAddr ?? string.Empty).Trim(),
                s.ServerPort,
                Token = s.Token ?? string.Empty,
                s.RemotePort,
            }, out string error))
            {
                _log.Warn("Tunnel", "保存穿透设置失败: " + error);
                throw new InvalidOperationException("保存穿透设置失败: " + error);
            }
        }

        // 记服务器与端口，但<b>绝不记 token</b>——日志页任何登录用户都能看
        _log.Info("Tunnel",
            "已保存穿透设置（启用=" + s.Enabled + "，服务器=" + s.ServerAddr +
            ":" + s.ServerPort + "，远端口=" + s.RemotePort + "），下次启动生效");
    }
}
