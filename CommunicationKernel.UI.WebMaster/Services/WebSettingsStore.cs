// -----------------------------------------------------------------------------
// 文件: Services/WebSettingsStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 持久化本进程 Web 监听端口。宿主已内嵌，不再保存远端 Host 地址。
// -----------------------------------------------------------------------------

using CommunicationKernel.Hosting.Sdk;
using System.Text.Json;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>读写 exe 旁 <c>config/web-listen.json</c>。</summary>
public sealed class WebSettingsStore
{
    /// <summary>保护写入的互斥锁，避免两条线路同时保存导致交错写。</summary>
    private readonly object _lock = new();
    private readonly AppLogStore _log;

    /// <param name="log">应用日志，用于上报保存失败。</param>
    public WebSettingsStore(AppLogStore log) => _log = log;

    /// <summary>web-listen.json 完整路径，供设置页展示。</summary>
    public string ListenFilePath => WebPaths.ListenFile;

    /// <summary>本进程 gRPC（Hosting.App）占用的端口，Web 禁止绑在上面。</summary>
    public const int HostPort = 5000;

    /// <summary>允许的 Web 端口：1024–65535，且不能是宿主的 5000。</summary>
    public static bool IsAllowedPort(int port) =>
        port is >= 1024 and <= 65535 && port != HostPort;

    /// <summary>
    /// 解析本次启动应监听的端口。
    /// 优先级：web-listen.json → ASPNETCORE_URLS → appsettings Web:ListenPort → 64000。
    /// 任何一层若是 5000 都跳过。
    /// </summary>
    public static int ResolveListenPort(IConfiguration config, string? aspnetUrls)
    {
        if (TryReadSavedPort(out int saved))
            return saved;

        if (TryParseFirstPort(aspnetUrls, out int envPort) && IsAllowedPort(envPort))
            return envPort;

        if (int.TryParse(config["Web:ListenPort"], out int cfg) && IsAllowedPort(cfg))
            return cfg;

        return LanAccess.DefaultPort;
    }

    /// <summary>读取设置页保存的端口；文件没有或非法时返回 false。</summary>
    public static bool TryReadSavedPort(out int port)
    {
        port = 0;
        try
        {
            string path = WebPaths.ListenFile;
            if (!File.Exists(path))
                return false;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Port", out JsonElement el))
                return false;
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int saved))
                return false;
            if (!IsAllowedPort(saved))
                return false;
            port = saved;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>保存 Web 监听端口。下次启动才生效——Kestrel 不能在进程内换端口。</summary>
    public void SaveListenPort(int port)
    {
        if (!IsAllowedPort(port))
            throw new ArgumentOutOfRangeException(nameof(port),
                "端口必须在 1024–65535，且不能使用 5000（那是本进程 gRPC 的）。");

        lock (_lock)
        {
            if (!JsonFileStore.SaveObject(
                    WebPaths.ListenFile, new { Port = port }, out string error))
            {
                _log.Warn("Settings", "保存 Web 端口失败: " + error);
                throw new InvalidOperationException("保存 Web 端口失败: " + error);
            }
        }
        _log.Info("Settings", "已保存 Web 端口 " + port + "，下次启动生效");
    }

    /// <summary>从 URL 列表取出第一个端口，例如 http://0.0.0.0:64000;https://... 。</summary>
    static bool TryParseFirstPort(string? urls, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(urls))
            return false;

        string first = urls.Split(';')[0].Trim();
        string candidate = first
            .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.OrdinalIgnoreCase)
            .Replace("://[::]", "://127.0.0.1", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) || uri.Port <= 0)
            return false;
        port = uri.Port;
        return true;
    }
}
