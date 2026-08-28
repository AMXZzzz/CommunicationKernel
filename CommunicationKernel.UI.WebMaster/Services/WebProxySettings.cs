// -----------------------------------------------------------------------------
// 文件: Services/WebProxySettings.cs
// 层级: UI 层 — WebMaster 配置
// 作用: 反向代理（公网中转）相关设置的读写。
//
// 为什么需要:
//   外地访问的典型拓扑是——公网服务器上的 1Panel / 宝塔 / nginx 做 HTTPS 终止，
//   经 frp 之类的隧道转到车间这台机器的 :64000。
//   这条链路上 WebMaster 收到的请求<b>永远是明文 http</b>，
//   它并不知道用户浏览器那一侧其实是 https。后果有两个，都致命：
//
//     1) Blazor Server 靠 WebSocket 维持界面。协商时它按<b>当前请求的协议</b>
//        生成回连地址，于是在 https 页面里给出 ws:// —— 浏览器按混合内容拦掉，
//        线路永远连不上。表现是页面能打开、但所有按钮都没反应，
//        过一会儿弹「界面循环出现未处理异常」。
//     2) 页面里生成的绝对链接、以及日志里的客户端 IP，都会是代理那一侧的值。
//
//   解决办法是让应用信任代理送来的 X-Forwarded-Proto / X-Forwarded-Host
//   （见 Main.cs 里的 UseForwardedHeaders）。本文件负责把「信不信、信谁、
//   要不要强制 https」这几件事做成可配置的，而不是写死。
//
// 安全前提（务必读）:
//   Web UI 有一道口令门槛（见 WebAuthStore），但 gRPC 的 :5000 <b>没有任何认证</b>。
//   把界面暴露到公网时：
//     · 必须先设访问口令；
//     · 反代上再叠一层 Basic Auth / OAuth / 来源 IP 白名单；
//     · 确认只把 Web 口转出去，:5000 绝不能一起暴露。
//   本文件的设置解决「能不能正常显示」与「走不走 https」，不解决「谁能访问」。
// -----------------------------------------------------------------------------

using CommunicationKernel.Hosting.Sdk;
using System.Text.Json;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>反向代理设置。</summary>
/// <param name="Enabled">
/// 是否信任反向代理送来的转发头。
/// <b>默认关闭</b>——只在确实有反代时才开，理由见 <see cref="TrustedProxies"/>。
/// </param>
/// <param name="TrustedProxies">
/// 可信代理的 IP 列表，空表示信任任意来源。
/// </param>
/// <param name="PathBase">
/// 子路径前缀，例如反代到 <c>https://x.com/ck/</c> 时填 <c>/ck</c>；
/// 直接反代到根路径时留空。
/// </param>
/// <param name="ForceHttps">
/// 是否把 http 访问强制跳到 https，并下发 HSTS。
/// </param>
public sealed record WebProxySettings(
    bool Enabled,
    IReadOnlyList<string> TrustedProxies,
    string PathBase,
    bool ForceHttps)
{
    /// <summary>默认：不启用反代支持，本机/局域网直连场景无需任何配置。</summary>
    public static WebProxySettings Disabled { get; } =
        new(false, Array.Empty<string>(), string.Empty, false);

    /// <summary>
    /// 强制 HTTPS 是否真正生效。
    /// </summary>
    /// <remarks>
    /// <b>必须同时启用反代支持。</b>证书在公网机上，WebMaster 自己只监听明文 http，
    /// 没有 HTTPS 侦听器。不开反代就没有 X-Forwarded-Proto，
    /// <c>Request.IsHttps</c> 永远是 false，于是每个请求都被重定向到 https，
    /// 而 https 又连不上——浏览器上表现为无限重定向或直接连接失败，
    /// 且这台机器<b>再也打不开设置页去关掉这个开关</b>，只能去删配置文件。
    /// 因此这里做成硬联锁，而不是只在界面上提示。
    /// </remarks>
    public bool HttpsRedirectActive => Enabled && ForceHttps;

    /// <summary>
    /// 规范化子路径：统一成 <c>/xxx</c> 形式，无前缀时返回空串。
    /// </summary>
    /// <remarks>
    /// ASP.NET Core 的 <c>UsePathBase</c> 要求以 '/' 开头且不以 '/' 结尾，
    /// 写成 <c>ck/</c> 或 <c>/ck/</c> 都会导致路由匹配不上，
    /// 而症状只是「页面 404」，看不出是这里格式不对。
    /// </remarks>
    public static string NormalizePathBase(string? raw)
    {
        string s = (raw ?? string.Empty).Trim();
        if (s.Length == 0 || s == "/") return string.Empty;

        if (!s.StartsWith('/')) s = "/" + s;
        return s.TrimEnd('/');
    }
}

/// <summary>读写 exe 旁 <c>config/web-proxy.json</c>。</summary>
public sealed class WebProxySettingsStore
{
    /// <summary>保护写入的互斥锁。</summary>
    private readonly object _lock = new();

    /// <summary>应用日志。</summary>
    private readonly AppLogStore _log;

    /// <param name="log">应用日志，用于上报读写失败。</param>
    public WebProxySettingsStore(AppLogStore log) => _log = log;

    /// <summary>配置文件完整路径，供设置页展示。</summary>
    public string FilePath => WebPaths.ProxyFile;

    /// <summary>
    /// 读取反代设置。
    /// </summary>
    /// <remarks>
    /// 任何读取失败都回落到 <see cref="WebProxySettings.Disabled"/>：
    /// 配置坏了最多是「公网访问显示不正常」，而抛异常会让整个应用起不来——
    /// 那连局域网也用不了，是更糟的结果。
    /// <para>
    /// 本方法是 <c>static</c>：Program.cs 需要在 DI 容器建好<b>之前</b>
    /// 就拿到设置来配置中间件，那时还没有 <see cref="AppLogStore"/> 可注入。
    /// </para>
    /// </remarks>
    public static WebProxySettings Load()
    {
        try
        {
            string path = WebPaths.ProxyFile;
            if (!File.Exists(path)) return WebProxySettings.Disabled;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;

            bool enabled = root.TryGetProperty("Enabled", out JsonElement e) && e.ValueKind == JsonValueKind.True;

            List<string> proxies = new();
            if (root.TryGetProperty("TrustedProxies", out JsonElement tp) && tp.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in tp.EnumerateArray())
                {
                    string? v = item.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) proxies.Add(v.Trim());
                }
            }

            string pathBase = root.TryGetProperty("PathBase", out JsonElement pb)
                ? WebProxySettings.NormalizePathBase(pb.GetString())
                : string.Empty;

            bool forceHttps = root.TryGetProperty("ForceHttps", out JsonElement fh) && fh.ValueKind == JsonValueKind.True;

            return new WebProxySettings(enabled, proxies, pathBase, forceHttps);
        }
        catch
        {
            // 文件损坏或无权读取：按未启用处理，局域网访问不受影响
            return WebProxySettings.Disabled;
        }
    }

    /// <summary>保存反代设置。下次启动生效——中间件管线在启动时就装配好了。</summary>
    /// <exception cref="InvalidOperationException">落盘失败。</exception>
    public void Save(WebProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_lock)
        {
            var payload = new
            {
                settings.Enabled,
                TrustedProxies = settings.TrustedProxies,
                PathBase = WebProxySettings.NormalizePathBase(settings.PathBase),
                settings.ForceHttps,
            };

            if (!JsonFileStore.SaveObject(WebPaths.ProxyFile, payload, out string error))
            {
                _log.Warn("Settings", "保存反代设置失败: " + error);
                throw new InvalidOperationException("保存反代设置失败: " + error);
            }
        }

        _log.Info("Settings",
            "已保存反代设置（启用=" + settings.Enabled + "，强制HTTPS=" + settings.ForceHttps + "，子路径=" +
            (settings.PathBase.Length == 0 ? "根" : settings.PathBase) + "），下次启动生效");
    }
}
