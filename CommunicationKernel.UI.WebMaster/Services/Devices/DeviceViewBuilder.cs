// -----------------------------------------------------------------------------
// 文件: Services/DeviceViewBuilder.cs
// 层级: UI 层 — WebMaster 服务
// 作用: 把「宿主路由」与「本地设备配置」合并成一份统一的展示视图。
//
// 为什么要抽出来:
//   MES 监控页与设备管理页都要做同一件事——把两个来源拼成设备卡片。
//   此前两页各有一份逐行雷同的实现（Index.ToCard / DevicesPage.ToRow），
//   只差站号与字节序两个字段。
//   这类重复的失败方式很隐蔽：以后改端点拼法（例如新增一种传输介质），
//   漏改一处的表现是<b>两个页面对同一台设备显示不同的连接信息</b>，
//   不抛异常、不报错，只能靠人肉比对发现。
// -----------------------------------------------------------------------------

using CommunicationKernel.Hosting.Sdk;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>
/// 一台设备的合并展示视图。
/// </summary>
/// <param name="RouteId">路由 Id，各页面据此查在线状态与发起操作。</param>
/// <param name="Name">显示名；本地未配名字时回落为 RouteId。</param>
/// <param name="ProtocolId">协议 Id，用于色条与副标题。</param>
/// <param name="TransportKind">传输介质，决定 <paramref name="Endpoint"/> 的含义。</param>
/// <param name="Station">站号；无站号协议为空字符串。</param>
/// <param name="Endpoint">端点描述：TCP 为「地址:端口」，串口为设备名。</param>
/// <param name="ByteOrder">字节序，仅存在于本地配置；缺省 ABCD。</param>
/// <param name="OnHost">是否已在宿主注册；false 表示已配置但未连接。</param>
public sealed record DeviceView(
    string RouteId,
    string Name,
    string ProtocolId,
    string TransportKind,
    string Station,
    string Endpoint,
    string ByteOrder,
    bool OnHost);

/// <summary>
/// 设备展示视图的构造。纯函数，不触碰存储与网络。
/// </summary>
public static class DeviceViewBuilder
{
    /// <summary>本地配置缺省的字节序。三个协议插件上抛的都是大端。</summary>
    private const string DefaultByteOrder = "ABCD";

    /// <summary>串口介质的取值，与 <c>TransportKind</c> 约定一致。</summary>
    private const string SerialKind = "Serial";

    /// <summary>
    /// 合并宿主路由与本地配置，构造设备视图列表。
    /// </summary>
    /// <param name="hostRoutes">宿主当前登记的路由；可为 null。</param>
    /// <param name="localDevices">本地设备配置；可为 null。</param>
    /// <returns>合并后的视图，保持「先宿主、后本地」的稳定顺序。</returns>
    /// <remarks>
    /// <para>
    /// 两个来源都要，各自覆盖一种现实情况：
    /// 宿主路由是<b>已连接</b>的设备，本地配置里还有<b>已配好但未连接</b>的。
    /// 只取前者的话，刚添加的设备根本不出现在列表里，操作员会以为没添加成功——
    /// 而「添加设备只写配置、不建连接」正是既定行为。
    /// </para>
    /// <para>
    /// <b>顺序即优先级</b>：先放宿主的（<c>OnHost = true</c>），
    /// 本地那轮跳过已存在的键，因此运行中的设备不会被未连接的定义覆盖掉。
    /// </para>
    /// </remarks>
    public static List<DeviceView> Build(
        IEnumerable<RouteDto>? hostRoutes,
        IEnumerable<WebDeviceRecord>? localDevices)
    {
        // 按 RouteId 索引本地配置，供第一轮配对取显示名与字节序。
        // OrdinalIgnoreCase 与设备库的比较器一致，否则大小写不同会配不上对
        Dictionary<string, WebDeviceRecord> local = new(StringComparer.OrdinalIgnoreCase);
        foreach (WebDeviceRecord d in localDevices ?? Enumerable.Empty<WebDeviceRecord>())
        {
            if (d is null || string.IsNullOrWhiteSpace(d.RouteId)) continue;
            local[d.RouteId] = d;
        }

        List<DeviceView> views = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        // 来源一：宿主上真正跑着的路由
        foreach (RouteDto r in hostRoutes ?? Enumerable.Empty<RouteDto>())
        {
            if (r is null || string.IsNullOrWhiteSpace(r.RouteId)) continue;

            seen.Add(r.RouteId);
            local.TryGetValue(r.RouteId, out WebDeviceRecord? rec);
            views.Add(Compose(r, rec, onHost: true));
        }

        // 来源二：已配置但未连接的设备，补进来显示为离线
        foreach (WebDeviceRecord rec in local.Values)
        {
            if (seen.Contains(rec.RouteId)) continue;
            views.Add(Compose(null, rec, onHost: false));
        }

        return views;
    }

    /// <summary>
    /// 把一条路由和／或一条本地配置合成一份视图。
    /// </summary>
    /// <param name="r">宿主路由；设备未连接时为 null。</param>
    /// <param name="rec">本地配置；宿主上有、本地却没留底时为 null。</param>
    /// <param name="onHost">该设备当前是否已在宿主注册。</param>
    /// <remarks>
    /// 两个入参不会同时为 null。取值一律「宿主优先、本地兜底」——
    /// 宿主返回的是运行中的真实参数，本地的可能是改了还没重连的草稿。
    /// 两个例外：显示名与字节序宿主都不存，只能取自本地。
    /// </remarks>
    private static DeviceView Compose(RouteDto? r, WebDeviceRecord? rec, bool onHost)
    {
        string id = r?.RouteId ?? rec?.RouteId ?? string.Empty;

        // 名字只有本地才有；没配名字就回落到 RouteId，避免卡片标题空白
        string name = rec is not null && !string.IsNullOrWhiteSpace(rec.Name) ? rec.Name : id;

        string protocol = r?.ProtocolId ?? rec?.ProtocolId ?? string.Empty;
        string transport = r?.TransportKind ?? rec?.TransportKind ?? string.Empty;
        string station = r?.Station ?? rec?.Station ?? string.Empty;

        // 串口与 TCP 的「端点」是完全不同的东西，不能用同一套字段拼
        bool serial = string.Equals(transport, SerialKind, StringComparison.OrdinalIgnoreCase);
        string endpoint = serial
            ? (r?.SerialPort ?? rec?.SerialPort ?? "串口")
            : (r is not null ? r.Address + ":" + r.Port : (rec?.Address ?? "") + ":" + (rec?.Port ?? 0));

        // 字节序只存在于本地配置：宿主的 RouteDto 不带它（引擎不解释数值语义）
        string byteOrder = string.IsNullOrWhiteSpace(rec?.ByteOrder) ? DefaultByteOrder : rec!.ByteOrder;

        return new DeviceView(id, name, protocol, transport, station, endpoint, byteOrder, onHost);
    }
}
