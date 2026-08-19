using System;
using System.IO;
using System.Text.Json;

namespace CommunicationDebuggingTools.WebUI.Services;

/// <summary>
/// WebUI 本地配置持久化。
/// </summary>
public sealed class WebUiSettingsStore {
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CommunicationDebuggingTools", "webui.settings.json");

    /// <summary>读取配置。</summary>
    public WebUiSettings Load () {
        try {
            if (!File.Exists(FilePath)) return new WebUiSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<WebUiSettings>(json) ?? new WebUiSettings();
        } catch {
            return new WebUiSettings();
        }
    }

    /// <summary>保存配置。</summary>
    public void Save (WebUiSettings settings) {
        ArgumentNullException.ThrowIfNull(settings);
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(dir)) {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions {
            WriteIndented = true
        }));
    }
}

/// <summary>
/// WebUI 配置模型。
/// </summary>
public sealed class WebUiSettings {
    /// <summary>EngineHost 地址。</summary>
    public string HostAddress { get; set; } = "http://127.0.0.1:5100";
}
