// -----------------------------------------------------------------------------
// 文件: Services/WebPaths.cs
// 层级: UI 层 — Blazor Server
// 作用: 集中 Web UI 本地持久化路径，与 WPF 共用 %APPDATA%/CommunicationKernel。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.Web.Services;

/// <summary>Web UI 本地文件路径。与 WPF 共用同一配置目录，Host 地址两边可互通。</summary>
internal static class WebPaths
{
    /// <summary>配置根目录：Windows 为 %APPDATA%\CommunicationKernel，Linux 为 ~/.config/CommunicationKernel。</summary>
    public static string Root
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CommunicationKernel");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Host 地址，字段 HostAddress，与 WPF SettingsViewModel 同一文件。</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// Web 自己的监听端口。单独成文件，避免 WPF 保存 settings.json 时把端口冲掉。
    /// </summary>
    public static string ListenFile => Path.Combine(Root, "web-listen.json");

    /// <summary>Web 侧设备配置（宿主重启后据此重新注册路由）。</summary>
    public static string DevicesFile => Path.Combine(Root, "web-devices.json");

    /// <summary>Web 侧变量表。</summary>
    public static string VariablesFile => Path.Combine(Root, "web-variables.json");
}
