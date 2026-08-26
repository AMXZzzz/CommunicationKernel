// -----------------------------------------------------------------------------
// 文件: Services/WebPaths.cs
// 层级: UI 层 — Blazor Server
// 作用: Web 上位机运行时配置，全部在本 exe 旁边的 config 目录，不与 WPF 共用。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>Web UI 本地文件路径。跟 exe 走，换机器拷贝整个目录即可。</summary>
internal static class WebPaths
{
    /// <summary>exe 所在目录下的 <c>config</c>，例如 <c>…\net8.0\config\</c>。</summary>
    public static string Root
    {
        get
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "config");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>本端保存的 Host 地址，字段 HostAddress。</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>本端保存的 Web 监听端口。</summary>
    public static string ListenFile => Path.Combine(Root, "web-listen.json");

    /// <summary>本端设备配置（宿主重启后据此重新注册路由）。</summary>
    public static string DevicesFile => Path.Combine(Root, "web-devices.json");

    /// <summary>本端变量表。</summary>
    public static string VariablesFile => Path.Combine(Root, "web-variables.json");
}
