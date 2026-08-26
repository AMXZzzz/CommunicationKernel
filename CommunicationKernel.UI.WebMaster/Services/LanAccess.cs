using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace CommunicationKernel.UI.WebMaster.Services;

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

    /// <summary>
    /// 本机浏览器该打开的地址。0.0.0.0 换成 localhost，绝不指向 Host 的 5000。
    /// </summary>
    public static string? LocalBrowserUrl(IServer? server)
    {
        ICollection<string>? addresses = server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0)
            return null;

        string url = addresses.FirstOrDefault(
            a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? addresses.First();

        url = url.Replace("://0.0.0.0", "://localhost", StringComparison.OrdinalIgnoreCase)
                 .Replace("://[::]", "://localhost", StringComparison.OrdinalIgnoreCase);

        if (ContainsPort(url, WebSettingsStore.HostPort))
            url = "http://localhost:" + DefaultPort;

        return url;
    }

    /// <summary>用系统默认浏览器打开本机界面；打不开就静默。</summary>
    public static void OpenBrowser(IServer? server, string? path = null)
    {
        try
        {
            string? url = LocalBrowserUrl(server);
            if (string.IsNullOrEmpty(url))
                return;
            if (!string.IsNullOrEmpty(path))
                url = url.TrimEnd('/') + path;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 无桌面会话时必然失败，服务本身不受影响
        }
    }

    /// <summary>判断 URL 里是否含指定端口，避免 :5000 误匹配 :50000。</summary>
    public static bool ContainsPort(string urls, int port)
    {
        string token = ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int start = 0;
        while (start < urls.Length)
        {
            int i = urls.IndexOf(token, start, StringComparison.Ordinal);
            if (i < 0)
                return false;
            int after = i + token.Length;
            if (after >= urls.Length || !char.IsDigit(urls[after]))
                return true;
            start = after;
        }
        return false;
    }
}
