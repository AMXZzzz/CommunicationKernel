// -----------------------------------------------------------------------------
// 文件: Services/WebSettingsStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 读写 Host.App 地址；文件格式与 WPF settings.json 对齐。
// -----------------------------------------------------------------------------

using CommunicationKernel.Host.Sdk;
using System.Text.Json;

namespace CommunicationKernel.UI.Web.Services;

/// <summary>持久化 Web / WPF 共用的 Host 地址。</summary>
public sealed class WebSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly IConfiguration _config;
    private readonly AppLogStore _log;

    public WebSettingsStore(IConfiguration config, AppLogStore log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>settings.json 完整路径，供设置页展示。</summary>
    public string FilePath => WebPaths.SettingsFile;

    /// <summary>优先级：已保存文件 → appsettings → 开发默认值。</summary>
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
                    if (!string.IsNullOrWhiteSpace(saved))
                        return saved.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Settings", "读取 settings.json 失败，回落配置: " + ex.Message);
        }

        return _config["Host.App:Address"] ?? fallback;
    }

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
