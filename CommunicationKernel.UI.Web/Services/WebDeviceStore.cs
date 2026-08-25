// -----------------------------------------------------------------------------
// 文件: Services/WebDeviceStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 持久化设备注册参数。Host.App 路由是内存态，重启即丢，必须由上位机留底。
// -----------------------------------------------------------------------------

using System.Text.Json;

using CommunicationKernel.Host.Sdk;

namespace CommunicationKernel.UI.Web.Services;

/// <summary>一台设备重新注册路由所需的全部参数（不含运行期在线状态）。</summary>
public sealed class WebDeviceRecord
{
    public string RouteId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProtocolId { get; set; } = string.Empty;
    public string TransportKind { get; set; } = "Tcp";
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } = 502;
    public string Station { get; set; } = string.Empty;
    public string SerialPort { get; set; } = string.Empty;
    public int BaudRate { get; set; }
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
public sealed class WebDeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, WebDeviceRecord> _records =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AppLogStore _log;

    public WebDeviceStore(AppLogStore log)
    {
        _log = log;
        Load();
    }

    public IReadOnlyList<WebDeviceRecord> GetAll()
    {
        lock (_lock)
            return _records.Values.Select(Clone).ToList();
    }

    public WebDeviceRecord? Get(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId)) return null;
        lock (_lock)
            return _records.TryGetValue(routeId, out WebDeviceRecord? r) ? Clone(r) : null;
    }

    public void Upsert(WebDeviceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.RouteId))
            throw new ArgumentException("RouteId 不能为空。", nameof(record));

        lock (_lock)
        {
            _records[record.RouteId] = Clone(record);
            Persist_NoLock();
        }
    }

    public void Remove(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId)) return;
        lock (_lock)
        {
            if (_records.Remove(routeId))
                Persist_NoLock();
        }
    }

    private void Load()
    {
        // 读写都走 Host.Sdk 的 JsonFileStore：与 WPF 端共用同一套原子落盘实现
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
