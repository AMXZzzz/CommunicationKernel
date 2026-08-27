// -----------------------------------------------------------------------------
// 文件: Services/WebDeviceStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 持久化设备注册参数。Hosting.App 路由是内存态，重启即丢，必须由上位机留底。
// -----------------------------------------------------------------------------

using System.Text.Json;

using CommunicationKernel.Hosting.Sdk;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>一台设备重新注册路由所需的全部参数（不含运行期在线状态）。</summary>
public sealed class WebDeviceRecord
{
    /// <summary>路由 Id，全局唯一，作为后续所有读写的句柄。</summary>
    public string RouteId { get; set; } = string.Empty;

    /// <summary>面向操作员的显示名，例如「一号线主控」；可为空，此时界面回落显示 RouteId。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 协议插件的选择键，例如 <c>modbus-tcp</c>。
    /// </summary>
    /// <remarks>
    /// 对本层是<b>不透明字符串</b>——取值必须来自 <c>QueryProtocols</c>，
    /// UI 不得内置任何协议名清单，那会随插件增减而失真。
    /// </remarks>
    public string ProtocolId { get; set; } = string.Empty;

    /// <summary>传输介质：<c>Tcp</c> 或 <c>Serial</c>。</summary>
    public string TransportKind { get; set; } = "Tcp";

    /// <summary>TCP 路由的 IP 或主机名；串口路由不使用此字段。</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>TCP 端口。默认 502 是 Modbus TCP 的标准端口。</summary>
    public int Port { get; set; } = 502;

    /// <summary>站号 / 从站 Id。是否必填由协议描述符的 <c>RequiresStation</c> 决定。</summary>
    public string Station { get; set; } = string.Empty;

    /// <summary>
    /// 串口设备名，例如 <c>COM3</c> 或 <c>/dev/ttyUSB0</c>。
    /// </summary>
    /// <remarks>
    /// 这是<b>宿主所在机器</b>上的串口，不是浏览器所在机器的。
    /// 宿主跑在树莓派时，这里要填树莓派的 /dev/ttyUSB0。
    /// </remarks>
    public string SerialPort { get; set; } = string.Empty;

    /// <summary>串口波特率；TCP 路由为 0。</summary>
    public int BaudRate { get; set; }

    /// <summary>
    /// 同一路由两次 I/O 之间的最小间隔（毫秒）。
    /// </summary>
    /// <remarks>
    /// 串口共享总线时需要它来满足从站的帧间静默要求；
    /// 设得过小会让从站把两帧粘成一帧而不响应。
    /// </remarks>
    public int MinIoIntervalMs { get; set; } = 15;

    /// <summary>
    /// 多字节数值在该设备寄存器里的排列方式，取值见 <see cref="ByteOrder"/>。
    /// </summary>
    /// <remarks>
    /// 按设备配而非按协议写死：Modbus 规范只规定 16 位寄存器内部是大端，
    /// 跨寄存器的 32 位值怎么摆完全没规定，同样是 Modbus，
    /// 不同品牌的变频器/PLC 可能是 ABCD 也可能是 CDAB。
    /// 默认 ABCD（大端）——三个协议插件出来的字节都已经是大端。
    /// </remarks>
    public string ByteOrder { get; set; } = "ABCD";
}

/// <summary>设备配置磁盘镜像。线程安全。</summary>
/// <remarks>
/// 单例。Hosting.App 的路由表是内存态，宿主重启即清空，
/// 因此"有哪些设备、参数是什么"必须由上位机留底，
/// 宿主恢复后再由 <see cref="EngineSession"/> 按本表对账重新注册。
/// </remarks>
public sealed class WebDeviceStore
{
    /// <summary>
    /// 序列化选项。
    /// </summary>
    /// <remarks>
    /// 仅供 <c>Clone</c> 使用；落盘的选项在 <see cref="JsonFileStore"/> 里统一定义。
    /// <c>PropertyNameCaseInsensitive</c> 让手工编辑过的配置文件也能读回来。
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>保护 <see cref="_records"/> 与落盘动作的互斥锁。</summary>
    private readonly object _lock = new();

    /// <summary>
    /// 按 RouteId 索引的设备配置。
    /// </summary>
    /// <remarks>
    /// 比较器用 <c>OrdinalIgnoreCase</c>：操作员录入的 RouteId 大小写难以保证一致，
    /// 但 "PLC-1" 与 "plc-1" 显然应当算同一台设备——
    /// 否则会出现两条配置指向同一个物理设备，注册时才报冲突。
    /// </remarks>
    private readonly Dictionary<string, WebDeviceRecord> _records =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>应用日志。</summary>
    private readonly AppLogStore _log;

    /// <param name="log">应用日志，用于上报载入与落盘失败。</param>
    /// <remarks>
    /// 构造即载入：本类是单例，载入一次即可；
    /// 且设备页首次渲染就需要这份数据，延迟载入会让页面先闪一下空列表。
    /// </remarks>
    public WebDeviceStore(AppLogStore log)
    {
        _log = log;
        Load();
    }

    /// <summary>取全部设备配置的副本。</summary>
    /// <remarks>
    /// 返回<b>克隆</b>而非内部对象：<see cref="WebDeviceRecord"/> 是可变类，
    /// 直接交出引用会让调用方（表单绑定）绕过锁改到内部状态，
    /// 且改动不会落盘——界面显示已改、重启后却复原，这类问题极难查。
    /// </remarks>
    public IReadOnlyList<WebDeviceRecord> GetAll()
    {
        lock (_lock)
            return _records.Values.Select(Clone).ToList();
    }

    /// <summary>按路由 Id 取单台设备配置；不存在时返回 null。</summary>
    public WebDeviceRecord? Get(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId)) return null;

        lock (_lock)
            return _records.TryGetValue(routeId, out WebDeviceRecord? r) ? Clone(r) : null;
    }

    /// <summary>新增或覆盖一台设备的配置，立即落盘。</summary>
    /// <param name="record">设备配置；<c>RouteId</c> 必填。</param>
    /// <exception cref="ArgumentException"><c>RouteId</c> 为空。</exception>
    /// <remarks>
    /// RouteId 为空属于调用方的编程错误而非用户输入问题（表单已先行校验），
    /// 因此这里抛异常而不是静默忽略——静默忽略会让"保存成功"的提示与实际不符。
    /// </remarks>
    public void Upsert(WebDeviceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.RouteId))
            throw new ArgumentException("RouteId 不能为空。", nameof(record));

        lock (_lock)
        {
            // 存克隆：调用方之后继续改动那个对象不应影响已保存的配置
            _records[record.RouteId] = Clone(record);
            Persist_NoLock();
        }
    }

    /// <summary>删除一台设备的配置。不存在时静默返回。</summary>
    /// <remarks>
    /// 仅在确有删除时才落盘，避免删不存在的条目也触发一次磁盘写。
    /// </remarks>
    public void Remove(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId)) return;

        lock (_lock)
        {
            if (_records.Remove(routeId))
                Persist_NoLock();
        }
    }

    /// <summary>从磁盘载入。文件缺失或损坏时以空表起步，不阻断启动。</summary>
    private void Load()
    {
        // 读写都走 Hosting.Sdk 的 JsonFileStore：与 WPF 端共用同一套原子落盘实现
        List<WebDeviceRecord> list = JsonFileStore.Load<WebDeviceRecord>(
            WebPaths.DevicesFile, out string error);

        if (error is not null)
        {
            // 配置损坏不阻止启动，以空配置继续；损坏文件保留在磁盘上便于排查
            _log.Warn("Devices", "载入 web-devices.json 失败: " + error);
            return;
        }

        lock (_lock)
        {
            _records.Clear();
            foreach (WebDeviceRecord r in list)
            {
                // 跳过损坏条目：缺 RouteId 无法作为字典键
                if (string.IsNullOrWhiteSpace(r.RouteId)) continue;
                _records[r.RouteId] = r;
            }
        }

        _log.Info("Devices", "已载入 " + _records.Count + " 台设备配置");
    }

    /// <summary>
    /// 写回磁盘。调用方必须已持有 <see cref="_lock"/>。
    /// </summary>
    /// <remarks>
    /// 此前是直接 File.WriteAllText 覆写，写入中途掉电会留下截断的 JSON，
    /// 下次启动整份设备配置全部丢失。现改为经 JsonFileStore 原子替换。
    /// </remarks>
    private void Persist_NoLock()
    {
        if (!JsonFileStore.Save(WebPaths.DevicesFile, _records.Values.ToList(), out string error))
            _log.Warn("Devices", "写入 web-devices.json 失败: " + error);
    }

    /// <summary>
    /// 深拷贝一条设备配置。
    /// </summary>
    /// <remarks>
    /// 逐字段手写而非反射/序列化：这是 <see cref="GetAll"/> 的热路径，
    /// 设备页每次重绘都会调用；反射拷贝在几十台设备的规模下会拖慢渲染。
    /// 代价是新增字段必须记得同步——见方法体末尾的注记。
    /// </remarks>
    private static WebDeviceRecord Clone(WebDeviceRecord r) => new()
    {
        RouteId = r.RouteId,
        Name = r.Name,
        ProtocolId = r.ProtocolId,
        TransportKind = r.TransportKind,
        Address = r.Address,
        Port = r.Port,
        Station = r.Station,
        SerialPort = r.SerialPort,
        BaudRate = r.BaudRate,
        MinIoIntervalMs = r.MinIoIntervalMs,
        // 新增字段务必同步加到这里：Clone 用于进出存储的深拷贝，
        // 漏一个字段的表现是「改了能保存、一读回来又变回默认值」，且不报任何错
        ByteOrder = r.ByteOrder,
    };
}
