#nullable enable

// -----------------------------------------------------------------------------
// 文件: Services/WpfAppSettings.cs
// 层级: UI 层 — WPF
// 作用: 解析 EngineHostingServiceApp 地址。exe 旁 config 已保存的优先，否则用自己的 appsettings.json。
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace CommunicationKernel.UI.Wpf.Services;

/// <summary>WPF 侧 Host 地址的读取顺序，避免 App 与设置页各写一套回落。</summary>
internal static class WpfAppSettings
{
    /// <summary>两层都没有时的最后回落，与 Web 出厂值一致。</summary>
    public const string FallbackAddress = "http://localhost:5000";

    /// <summary>本端操作员配置：exe 旁 config/settings.json。</summary>
    public static string SettingsFile => WpfPaths.SettingsFile;

    /// <summary>本项目 appsettings.json，跟 exe 放在一起。</summary>
    public static string AppSettingsFile => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>
    /// 优先级：exe 旁 config/settings.json → 本项目 appsettings.json → 本机 5000。
    /// </summary>
    public static string ReadAddress(IConfiguration? config)
    {
        string? saved = TryReadSavedHostAddress();
        if (!string.IsNullOrWhiteSpace(saved))
            return saved.Trim();

        string? fromConfig = config?["EngineHostingServiceApp:Address"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return fromConfig.Trim();

        return FallbackAddress;
    }

    /// <summary>只读 exe 旁 config/settings.json；没有或坏文件返回 null。</summary>
    public static string? TryReadSavedHostAddress()
    {
        try
        {
            string path = SettingsFile;
            if (!File.Exists(path))
                return null;

            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("HostAddress", out JsonElement el))
                return null;
            string? addr = el.GetString();
            return string.IsNullOrWhiteSpace(addr) ? null : addr.Trim();
        }
        catch
        {
            return null;
        }
    }
}
