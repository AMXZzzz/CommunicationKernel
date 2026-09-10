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

    /// <summary>
    /// 把配置文件的绝对路径缩成「上级目录/文件名」，例如 <c>config/web-lines.json</c>。
    /// </summary>
    /// <param name="path">完整路径。</param>
    /// <returns>短路径；传空串时原样返回。</returns>
    /// <remarks>
    /// 界面上要告诉操作员「这一页的配置落在哪个文件」，但开发机上那种一百多字符的
    /// 绝对路径塞不进侧栏，硬塞会把旁边的东西挤走。真正要辨认的是「是哪个 json」，
    /// 不是它在哪个盘——完整路径交给 title，鼠标一停就有。
    /// <para>
    /// 用 <see cref="Path"/> 的方法而不是自己按分隔符切：Windows 上是反斜杠、
    /// 树莓派上是正斜杠，硬编码任何一种都会在另一个平台上原样返回整条路径。
    /// </para>
    /// </remarks>
    public static string Short(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        string file = Path.GetFileName(path);
        string? dir = Path.GetDirectoryName(path);
        string parent = string.IsNullOrEmpty(dir) ? string.Empty : Path.GetFileName(dir);

        return parent.Length == 0 ? file : parent + "/" + file;
    }

    /// <summary>本端保存的 Web 监听端口。</summary>
    public static string ListenFile => Path.Combine(Root, "web-listen.json");

    /// <summary>本端设备配置（宿主重启后据此重新注册路由）。</summary>
    public static string DevicesFile => Path.Combine(Root, "web-devices.json");

    /// <summary>本端变量表。</summary>
    public static string VariablesFile => Path.Combine(Root, "web-variables.json");

    /// <summary>设备功能模板库（名称 / 类型 / 备注，不含地址）。</summary>
    public static string TemplatesFile => Path.Combine(Root, "web-templates.json");

    /// <summary>产线编排：工站顺序、点位选择、报警规则。只引用设备与变量的标识。</summary>
    public static string LinesFile => Path.Combine(Root, "web-lines.json");

    /// <summary>反向代理（公网中转）设置。</summary>
    public static string ProxyFile => Path.Combine(Root, "web-proxy.json");

    /// <summary>登录口令（PBKDF2 哈希，非明文）。文件不存在即表示不需要登录。</summary>
    public static string AuthFile => Path.Combine(Root, "web-auth.json");

    /// <summary>内网穿透（frpc）设置。</summary>
    public static string TunnelFile => Path.Combine(Root, "web-tunnel.json");
}
