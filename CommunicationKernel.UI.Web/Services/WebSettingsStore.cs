// -----------------------------------------------------------------------------
// 文件: Services/WebSettingsStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 读写 Host.App 地址；文件格式与 WPF settings.json 对齐。
// -----------------------------------------------------------------------------

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
            File.WriteAllText(
                WebPaths.SettingsFile,
                JsonSerializer.Serialize(new { HostAddress = normalized }, JsonOptions));
        }
        _log.Info("Settings", "已保存 Host 地址: " + normalized);
    }
}
