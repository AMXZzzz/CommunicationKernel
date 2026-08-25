#nullable enable

// -----------------------------------------------------------------------------
// 文件: Services/WpfPaths.cs
// 层级: UI 层 — WPF
// 作用: WPF 上位机运行时配置，全部在本 exe 旁边的 config 目录，不与 Web 共用。
// -----------------------------------------------------------------------------

using System;
using System.IO;

namespace CommunicationKernel.UI.Wpf.Services;

/// <summary>WPF 本地文件路径。跟 exe 走，换机器拷贝整个目录即可。</summary>
internal static class WpfPaths
{
    /// <summary>exe 所在目录下的 <c>config</c>，例如 <c>…\net8.0-windows\config\</c>。</summary>
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

    /// <summary>本端设备配置。</summary>
    public static string DevicesFile => Path.Combine(Root, "devices.json");

    /// <summary>本端变量表。</summary>
    public static string VariablesFile => Path.Combine(Root, "variables.json");

    /// <summary>本端协议清单离线缓存。</summary>
    public static string ProtocolsCacheFile => Path.Combine(Root, "protocols.cache.json");
}
