#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/DeviceConfigStore.cs
// 层级: UI 层 — WPF 服务实现
// 作用: 把设备配置持久化到磁盘，作为上位机侧的唯一事实来源，供路由对账恢复。
//
// 为什么需要它：
//   Hosting.App 的路由是纯内存对象，进程重启即全部丢失。此前上位机没有任何
//   本地留存，于是宿主一重启：
//     · Load() 发现服务端没有这些路由，把本地设备列表整个清空；
//     · 已配置的变量继续对着不存在的路由轮询，永远收到 RouteNotFound；
//     · 操作员只能把每台设备重新手工录一遍。
//   业务配置属于上位机（宿主只是无状态的通讯引擎），因此配置必须落在这里，
//   并由 IRouteReconciler 据此把路由重新推给宿主。
//
// 存储位置：本 exe 旁 config\devices.json，不与 Web 共用。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using CommunicationKernel.UI.Wpf.Core.Logging;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// 设备配置的磁盘持久化。线程安全：所有公开方法内部加锁。
    /// </summary>
    public sealed class DeviceConfigStore
    {
        // ============================================================================
        // 常量与字段
        // ============================================================================

        /// <summary>配置文件完整路径。本端独立目录，见 <see cref="WpfPaths"/>。</summary>
        private static readonly string FilePath = WpfPaths.DevicesFile;

        /// <summary>保护 <see cref="_records"/> 与落盘的互斥锁。</summary>
        private readonly object _lock = new object();

        /// <summary>日志记录器，可为 null（未注入时静默）。</summary>
        private readonly IAppLogger _log;

        /// <summary>
        /// 内存副本，Key = RouteId。磁盘只是它的镜像，
        /// 读路径一律走内存，避免每次查名称都碰一次文件系统。
        /// </summary>
        private readonly Dictionary<string, DeviceRecord> _records
            = new Dictionary<string, DeviceRecord>(StringComparer.OrdinalIgnoreCase);

        // ============================================================================
        // 记录类型
        // ============================================================================

        /// <summary>
        /// 一台设备的可持久化配置。
        /// </summary>
        /// <remarks>
        /// 只存"重新注册这条路由所需的参数"加上"gRPC 不传输的本地元数据"。
        /// IsConnected / StatusType 这类运行期状态一律不存——
        /// 存了会在下次启动时显示成一个从未验证过的连接状态，比不显示更糟。
        /// </remarks>
        public sealed class DeviceRecord
        {
            // 注册路由所需

            /// <summary>路由 ID，同时是本记录在字典中的键。</summary>
            public string Id            { get; set; }

            /// <summary>协议标识（插件选择键）。宿主据此挑协议插件，本层不解析其含义。</summary>
            public string Protocol      { get; set; }

            /// <summary>传输介质标识，Tcp 或 Serial。</summary>
            public string TransportKind { get; set; }

            /// <summary>目标 IP，串口路由为空。</summary>
            public string Ip            { get; set; }

            /// <summary>目标端口，串口路由为 0。</summary>
            public int    Port          { get; set; }

            /// <summary>站号的字符串形式，注册路由时实际传的是它。</summary>
            public string Station       { get; set; }

            /// <summary>站号的数值形式，供界面回填输入框。与 <see cref="Station"/> 同步写入。</summary>
            public int    StationNo     { get; set; }

            /// <summary>串口设备名，TCP 路由为空。注意这是<b>宿主机器</b>上的设备名。</summary>
            public string SerialPort    { get; set; }

            /// <summary>波特率，TCP 路由为 0。</summary>
            public int    BaudRate      { get; set; }

            // gRPC 路由模型里没有、必须本地留存的展示元数据

            /// <summary>设备显示名。宿主只认路由 ID，这个名字只存在于本地。</summary>
            public string Name              { get; set; }

            /// <summary>设备型号，仅用于显示。</summary>
            public string Model             { get; set; }

            /// <summary>是否双轨设备。</summary>
            /// <remarks>
            /// 不存 Lane——它是从本属性派生的只读值，存了两份就会出现互相矛盾的可能。
            /// </remarks>
            public bool   IsDualLane        { get; set; }

            /// <summary>预留的扩展配置 JSON，本期界面不编辑，仅原样保留。</summary>
            public string ExtraSettingsJson { get; set; }
        }

        // ============================================================================
        // 构造
        // ============================================================================

        /// <summary>构造并立即从磁盘载入既有配置。</summary>
        /// <param name="log">可选日志记录器。</param>
        public DeviceConfigStore(IAppLogger log = null)
        {
            _log = log;
            // 启动即载入，保证设备页构造时本地列表已有内容
            LoadFromDisk();
        }

        // ============================================================================
        // 公开方法
        // ============================================================================

        /// <summary>返回全部已持久化的设备配置快照。</summary>
        /// <returns>内部集合的副本，调用方可安全遍历。</returns>
        public IReadOnlyList<DeviceRecord> GetAll()
        {
            lock (_lock)
            {
                // 返回副本，避免调用方在锁外改内部字典
                return new List<DeviceRecord>(_records.Values);
            }
        }

        /// <summary>按路由 ID 取配置；不存在返回 null。</summary>
        /// <param name="routeId">路由 ID。</param>
        /// <returns>配置记录，或 null。</returns>
        public DeviceRecord Get(string routeId)
        {
            // 空 ID 无法对账，直接视为没有配置
            if (string.IsNullOrWhiteSpace(routeId)) return null;

            lock (_lock)
            {
                // 命中返回记录，未命中返回 null（调用方据此决定是否重注册）
                return _records.TryGetValue(routeId, out DeviceRecord record) ? record : null;
            }
        }

        /// <summary>写入（或覆盖）一台设备的配置并落盘。</summary>
        /// <param name="routeId">路由 ID。为空则忽略本次调用。</param>
        /// <param name="info">设备信息。运行期状态（连接与否）不会被存下来。</param>
        public void Save(string routeId, DeviceInfo info)
        {
            // 缺 ID 或设备对象则无法持久化
            if (string.IsNullOrWhiteSpace(routeId) || info == null) return;

            lock (_lock)
            {
                // 用当前 DeviceInfo 覆盖内存记录（含 gRPC 没有的名称/型号/轨道）
                _records[routeId] = new DeviceRecord
                {
                    Id                = routeId,
                    Protocol          = info.Protocol,
                    TransportKind     = info.TransportKind,
                    Ip                = info.Ip,
                    Port              = info.Port,
                    Station           = info.Station,
                    StationNo         = info.StationNo,
                    SerialPort        = info.SerialPort,
                    BaudRate          = info.BaudRate,
                    Name              = info.Name,
                    Model             = info.Model,
                    IsDualLane        = info.IsDualLane,
                    ExtraSettingsJson = info.ExtraSettingsJson
                };

                // 立即落盘，避免进程异常退出丢失刚保存的设备
                FlushToDisk();
            }
        }

        /// <summary>删除一台设备的配置并落盘。</summary>
        /// <param name="routeId">路由 ID。不存在时不做任何事，也不写盘。</param>
        public void Delete(string routeId)
        {
            // 空 ID 无需处理
            if (string.IsNullOrWhiteSpace(routeId)) return;

            lock (_lock)
            {
                // 真正删掉才写盘，避免无谓 I/O
                if (_records.Remove(routeId))
                    FlushToDisk();
            }
        }

        // ============================================================================
        // 磁盘 I/O
        // ============================================================================

        /// <summary>从磁盘载入；文件缺失或损坏时以空配置起步，不抛异常。</summary>
        /// <remarks>
        /// 落盘细节在 Hosting.Sdk 的 <see cref="JsonFileStore"/> 里，与 Web 端共用同一份实现。
        /// 收敛的直接起因：Web 端此前是非原子写，掉电会丢掉整份设备配置，
        /// 而 WPF 这边一直是对的——同样的代码写两遍，只有一遍带防护。
        /// </remarks>
        private void LoadFromDisk()
        {
            List<DeviceRecord> loaded = JsonFileStore.Load<DeviceRecord>(FilePath, out string error);

            if (error != null)
            {
                // 配置损坏不应阻止程序启动：以空配置继续，用户重新录入即可覆盖。
                // 损坏文件刻意保留在磁盘上，便于事后排查。
                _log?.Error("Device", "载入本地设备配置失败，将以空配置启动: " + error);
                return;
            }

            foreach (DeviceRecord record in loaded)
            {
                // 跳过损坏条目（缺 Id 无法作为字典键）
                if (record != null && !string.IsNullOrWhiteSpace(record.Id))
                    _records[record.Id] = record;
            }

            // 记录载入条数，便于启动排查
            _log?.Info("Device", string.Format("已从本地载入 {0} 台设备配置", _records.Count));
        }

        /// <summary>
        /// 写回磁盘。调用方必须已持有 <see cref="_lock"/>。
        /// </summary>
        /// <remarks>
        /// <see cref="JsonFileStore.Save{T}"/> 会先写临时文件再替换：
        /// 直接覆盖时若在写入中途断电或崩溃，会留下一个被截断的 JSON，
        /// 下次启动直接丢失全部设备配置。
        /// </remarks>
        private void FlushToDisk()
        {
            if (!JsonFileStore.Save(FilePath, _records.Values, out string error))
                // 落盘失败只影响下次启动的恢复能力，不应中断当前操作
                _log?.Error("Device", "保存本地设备配置失败: " + error);
        }
    }
}
