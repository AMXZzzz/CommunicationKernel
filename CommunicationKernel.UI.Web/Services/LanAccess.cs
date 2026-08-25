// -----------------------------------------------------------------------------
// 文件: Services/LanAccess.cs
// 层级: UI 层 — Web
// 作用: 列出本机可被同一 WiFi 访问的 IPv4，供启动日志和设置页共用。
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CommunicationKernel.UI.Web.Services;

/// <summary>当前网卡上手机打得通的 IPv4 地址。</summary>
internal static class LanAccess
{
    /// <summary>未配置时的 Web 端口，与 appsettings.json 的 Kestrel 默认一致。</summary>
    public const int DefaultPort = 64000;

    /// <summary>已启用网卡上的非回环、非 169.254 的 IPv4。</summary>
    public static IReadOnlyList<string> EnumerateIPv4()
    {
        List<string> result = new();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                continue;

            foreach (UnicastIPAddressInformation uni in nic.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(uni.Address))
                    continue;
                byte[] bytes = uni.Address.GetAddressBytes();
                if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
                    continue;
                string ip = uni.Address.ToString();
                if (!result.Contains(ip, StringComparer.Ordinal))
                    result.Add(ip);
            }
        }
        return result;
    }

    /// <summary>拼出手机浏览器该打开的 http 地址。</summary>
    public static IReadOnlyList<string> PhoneUrls(int port)
    {
        IReadOnlyList<string> ips = EnumerateIPv4();
        string[] urls = new string[ips.Count];
        for (int i = 0; i < ips.Count; i++)
            urls[i] = "http://" + ips[i] + ":" + port;
        return urls;
    }
}
