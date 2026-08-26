// -----------------------------------------------------------------------------
// 文件: Services/WebSettingsStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 读写本端 EngineHostingServiceApp 地址。文件在本 exe 旁 config/，不与 WPF 共用。
// -----------------------------------------------------------------------------

using CommunicationKernel.EngineHost.Sdk;
using System.Text.Json;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>持久化 Web 自己的 Host 地址。</summary>
/// <remarks>
/// 落在本 exe 旁 <c>config/settings.json</c>，与 WPF 的 config 互不影响。
/// </remarks>
public sealed class WebSettingsStore
{
    /// <summary>读取时的 JSON 选项。写入侧的选项在 <see cref="JsonFileStore"/> 中统一定义。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>保护写入的互斥锁，避免两个线路同时保存导致交错写。</summary>
    private readonly object _lock = new();

    /// <summary>appsettings 配置源，作为文件缺失时的回落。</summary>
    private readonly IConfiguration _config;

    /// <summary>应用日志。</summary>
    private readonly AppLogStore _log;

    /// <param name="config">appsettings 配置。</param>
    /// <param name="log">应用日志。</param>
    public WebSettingsStore(IConfiguration config, AppLogStore log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>settings.json 完整路径，供设置页展示。</summary>
    public string FilePath => WebPaths.SettingsFile;

    /// <summary>web-listen.json 完整路径，供设置页展示。</summary>
    public string ListenFilePath => WebPaths.ListenFile;

    /// <summary>优先级：已保存文件 → appsettings → 开发默认值。</summary>
    /// <remarks>
    /// <para>
    /// 用 <see cref="JsonDocument"/> 逐字段取而非反序列化成强类型：
    /// 本文件以后可能加字段，强类型反序列化会因结构差异整体失败，
    /// 连本来能读出来的地址也一并丢掉。
    /// </para>
    /// <para>
    /// 任何读取失败都回落而不抛出：地址读不出来最多是连不上宿主，
    /// 而抛异常会让整个 Web 应用起不来——那是更糟的结果。
    /// </para>
    /// </remarks>
    public string LoadAddress()
    {
        const string fallback = "http://localhost:5000";

        try
        {
            string path = WebPaths.SettingsFile;
            if (File.Exists(path))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));

                if (doc.RootElement.TryGetProperty("HostAddress", out JsonElement el))
                {
                    string? saved = el.GetString();

                    // 空字符串视为未配置，继续往下回落
                    if (!string.IsNullOrWhiteSpace(saved))
                        return saved.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            // 文件损坏或无读取权限：记一条警告后回落，不阻断启动
            _log.Warn("Settings", "读取 settings.json 失败，回落配置: " + ex.Message);
        }

        return _config["EngineHostingServiceApp:Address"] ?? fallback;
    }

    /// <summary>保存 Host 地址。</summary>
    /// <param name="address">目标地址，例如 <c>http://192.168.1.10:5000</c>。</param>
    /// <exception cref="ArgumentException">地址为空白。</exception>
    /// <remarks>
    /// 保存成功才记日志：落盘失败时已经记过警告，
    /// 再记一条"已保存"会让日志自相矛盾。
    /// </remarks>
    public void SaveAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("地址不能为空。", nameof(address));

        string normalized = address.Trim();
        lock (_lock)
        {
            // 原子写：settings.json 虽小，被截断后同样会让下次启动读不出地址
            if (!JsonFileStore.SaveObject(
                    WebPaths.SettingsFile, new { HostAddress = normalized }, out string error))
            {
                _log.Warn("Settings", "保存 Host 地址失败: " + error);
                return;
            }
        }
        _log.Info("Settings", "已保存 Host 地址: " + normalized);
    }

    /// <summary>EngineHostingServiceApp 占用的端口，Web 禁止绑在上面。</summary>
    public const int HostPort = 5000;

    /// <summary>允许的 Web 端口：1024–65535，且不能是宿主的 5000。</summary>
    public static bool IsAllowedPort(int port) =>
        port is >= 1024 and <= 65535 && port != HostPort;

    /// <summary>
    /// 解析本次启动应监听的端口。
    /// 优先级：web-listen.json（设置页）→ ASPNETCORE_URLS（命令行 / VS）→ appsettings Web:ListenPort → 64000。
    /// 任何一层若是 5000 都跳过，避免再去抢宿主。
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
                "端口必须在 1024–65535，且不能使用 5000（那是 EngineHostingServiceApp 的）。");

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
