// -----------------------------------------------------------------------------
// 文件: Services/WebSettingsStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 读写 Host.App 地址；文件格式与 WPF settings.json 对齐。
// -----------------------------------------------------------------------------

using CommunicationKernel.Host.Sdk;
using System.Text.Json;

namespace CommunicationKernel.UI.Web.Services;

/// <summary>持久化 Web / WPF 共用的 Host 地址。</summary>
/// <remarks>
/// 两个上位机共用同一个 <c>%APPDATA%/CommunicationKernel/settings.json</c>：
/// 现场往往先用 WPF 调通地址，再开 Web 端给操作员用，
/// 分两份配置会让人在一端改了地址、另一端还连着旧地址而不知道。
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

    /// <summary>优先级：已保存文件 → appsettings → 开发默认值。</summary>
    /// <remarks>
    /// <para>
    /// 用 <see cref="JsonDocument"/> 逐字段取而非反序列化成强类型：
    /// 这个文件是 WPF 与 Web 共用的，另一端可能写入本端还不认识的字段，
    /// 强类型反序列化会因未知字段或结构差异整体失败，
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

        return _config["Host.App:Address"] ?? fallback;
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
}
