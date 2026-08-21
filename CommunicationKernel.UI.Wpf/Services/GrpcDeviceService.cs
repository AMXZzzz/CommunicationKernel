// -----------------------------------------------------------------------------
// 文件: Services/GrpcDeviceService.cs
// 层级: UI层 — 服务实现
// 作用: IDeviceService 的 gRPC 实现，封装 EngineHostGrpcClient 的路由管理调用。
//       维护内存中的 ObservableCollection<DeviceInfo>，所有集合修改均切回 UI 线程。
//       Load() 采用合并策略：保留已连接设备的连接状态，只新增/删除差量条目。
//       ConnectAsync 为每个路由启动独立的 WatchRouteStatus 后台任务，
//       Disconnect 通过 CancellationTokenSource 停止对应的后台任务。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Logging;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// <see cref="IDeviceService"/> 的 gRPC 实现。
    /// 通过 <see cref="EngineHostGrpcClient"/> 与 EngineHost 通信，
    /// 将路由信息映射为本地 <see cref="DeviceInfo"/> 对象。
    /// </summary>
    public sealed class GrpcDeviceService : IDeviceService
    {
        // -------------------------------------------------------------------------
        // 私有字段
        // -------------------------------------------------------------------------

        /// <summary>gRPC 客户端，用于调用 RegisterRoute / QueryRoutes / WatchRouteStatus。</summary>
        private readonly EngineHostGrpcClient _client;

        /// <summary>应用日志记录器，可为 null（此时不记录日志）。</summary>
        private readonly IAppLogger _log;

        /// <summary>
        /// 每个路由的状态监听取消令牌源，Key = RouteId。
        /// 调用 Disconnect 时通过此字典找到对应的 CTS 并取消。
        /// </summary>
        private readonly Dictionary<string, CancellationTokenSource> _watchTasks
            = new Dictionary<string, CancellationTokenSource>();

        /// <summary>用于保护 _watchTasks 字典的同步锁。</summary>
        private readonly object _watchLock = new object();

        /// <summary>
        /// 设备显示名等本地元数据，Key = RouteId。
        /// EngineHost 的路由模型没有 Name / Model 字段，若不在本地留存，
        /// 注册成功后 Load() 回填时名称会被 RouteId 覆盖。
        /// </summary>
        private readonly Dictionary<string, LocalDeviceMeta> _localMeta
            = new Dictionary<string, LocalDeviceMeta>(StringComparer.OrdinalIgnoreCase);

        /// <summary>保护 _localMeta 的同步锁。</summary>
        private readonly object _metaLock = new object();

        /// <summary>不随 gRPC 传输的本地设备元数据。</summary>
        private sealed class LocalDeviceMeta
        {
            public string Name  { get; set; }
            public string Model { get; set; }
            public bool   IsDualLane { get; set; }
        }

        // -------------------------------------------------------------------------
        // 事件
        // -------------------------------------------------------------------------

        /// <inheritdoc />
        public event Action<string> OperationFailed;

        // -------------------------------------------------------------------------
        // 公开属性
        // -------------------------------------------------------------------------

        /// <summary>
        /// 当前设备列表，ObservableCollection 自动通知 WPF 列表控件刷新。
        /// 所有修改操作均通过 UI 线程 Dispatcher 执行。
        /// </summary>
        public ObservableCollection<DeviceInfo> Devices { get; }
            = new ObservableCollection<DeviceInfo>();

        // -------------------------------------------------------------------------
        // 构造函数
        // -------------------------------------------------------------------------

        /// <summary>
        /// 初始化 GrpcDeviceService。
        /// </summary>
        /// <param name="client">已初始化的 gRPC 客户端。</param>
        /// <param name="log">可选日志记录器，为 null 时不记录日志。</param>
        public GrpcDeviceService(EngineHostGrpcClient client, IAppLogger log = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _log    = log;
        }

        // -------------------------------------------------------------------------
        // IDeviceService 实现
        // -------------------------------------------------------------------------

        /// <summary>
        /// 从 gRPC 后端加载路由列表并合并到本地 Devices 集合。
        /// 合并策略：
        ///   • 已有且服务端仍存在的路由 → 更新元数据（IP/Port 等），保留连接状态；
        ///   • 服务端新增的路由 → 追加为 Offline；
        ///   • 本地有但服务端已删除的路由 → 从本地移除。
        /// 合并完成后已连接设备的 WatchRouteStatus 流不受影响。
        /// </summary>
        public void Load()
        {
            Task.Run(async () =>
            {
                try
                {
                    IReadOnlyList<RouteDto> routes = await _client
                        .QueryRoutesAsync()
                        .ConfigureAwait(false);

                    // 切回 UI 线程执行集合合并（ObservableCollection 要求 UI 线程访问）
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        // 建立服务端路由 ID 集合，用于 O(1) 查找
                        var serverIds = new HashSet<string>(
                            routes.Select(r => r.RouteId),
                            StringComparer.OrdinalIgnoreCase);

                        // 1. 移除本地有但服务端已删除的路由
                        for (int i = Devices.Count - 1; i >= 0; i--)
                        {
                            if (!serverIds.Contains(Devices[i].Id))
                                Devices.RemoveAt(i);
                        }

                        // 2. 对每条服务端路由执行更新或新增
                        foreach (RouteDto r in routes)
                        {
                            DeviceInfo existing = FindDevice(r.RouteId);
                            if (existing != null)
                            {
                                // 已有：仅更新可变元数据，保留 IsConnected / StatusType 不变
                                existing.Protocol      = r.ProtocolId;
                                existing.Ip            = r.Address;
                                existing.Port          = r.Port;
                                existing.Station       = r.Station;
                                existing.SerialPort    = r.SerialPort;
                                existing.BaudRate      = r.BaudRate;
                                existing.TransportKind = r.TransportKind;
                            }
                            else
                            {
                                // 新增：从服务端同步过来的路由默认为离线。
                                // 名称等本地元数据优先取本地留存值，避免被 RouteId 覆盖。
                                LocalDeviceMeta meta = GetMeta(r.RouteId);

                                Devices.Add(new DeviceInfo {
                                    Id            = r.RouteId,
                                    Name          = meta != null && !string.IsNullOrWhiteSpace(meta.Name)
                                        ? meta.Name
                                        : r.RouteId,
                                    Model         = meta != null ? (meta.Model ?? "") : "",
                                    IsDualLane    = meta != null && meta.IsDualLane,
                                    Protocol      = r.ProtocolId,
                                    Ip            = r.Address,
                                    Port          = r.Port,
                                    Station       = r.Station,
                                    SerialPort    = r.SerialPort,
                                    BaudRate      = r.BaudRate,
                                    TransportKind = r.TransportKind,
                                    StatusType    = DeviceStatusType.Offline,
                                    IsConnected   = false,
                                });
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    _log?.Error("Device", "加载设备列表失败", ex);
                }
            });
        }

        /// <summary>
        /// 向 gRPC 后端注册新路由，完成后刷新本地设备列表。
        /// </summary>
        /// <param name="info">包含连接参数的新设备信息。</param>
        public void Add(DeviceInfo info)
        {
            if (info == null) return;

            string routeId = string.IsNullOrWhiteSpace(info.Id)
                ? Guid.NewGuid().ToString("N")
                : info.Id;

            // 先留存本地元数据，Load() 回填时据此还原名称，避免被 RouteId 覆盖
            SaveMeta(routeId, info);

            Task.Run(() => RegisterAsync(routeId, info, "添加设备"));
        }

        /// <summary>
        /// 以新参数重新注册已有路由（EngineHost 支持幂等 RegisterRoute），
        /// 完成后刷新本地设备列表。
        /// </summary>
        /// <param name="info">已修改参数的设备信息，Id 须与现有设备匹配。</param>
        public void Update(DeviceInfo info)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.Id)) return;

            SaveMeta(info.Id, info);

            // EngineHost 的 RegisterRoute 拒绝重复 RouteId，因此改参数必须先注销再注册。
            Task.Run(async () =>
            {
                (bool removed, string rmCode, string rmMsg) =
                    await _client.RemoveRouteAsync(info.Id).ConfigureAwait(false);

                if (!removed)
                {
                    // 注销失败则不再尝试注册，否则必然撞上 "route_id already registered"
                    _log?.Warn("Device",
                        string.Format("更新设备前注销旧路由失败 route={0} code={1}: {2}",
                            info.Id, rmCode, rmMsg));
                    RaiseFailure(string.Format("更新设备失败：无法注销原有连接（{0}）", rmMsg));
                    return;
                }

                await RegisterAsync(info.Id, info, "更新设备").ConfigureAwait(false);
            });
        }

        /// <summary>
        /// 执行一次 RegisterRoute 并处理结果。
        /// 成功则刷新列表；失败则记录日志并通过 <see cref="OperationFailed"/> 上报，
        /// 避免注册失败后设备"悄无声息地不出现"。
        /// </summary>
        /// <param name="routeId">路由 ID。</param>
        /// <param name="info">设备参数。</param>
        /// <param name="actionLabel">用于错误提示的操作名称，如"添加设备"。</param>
        private async Task RegisterAsync(string routeId, DeviceInfo info, string actionLabel)
        {
            // 传输介质：空值一律按 Tcp 处理。
            // 注意必须用 IsNullOrWhiteSpace 判断——DeviceInfo 的字符串字段默认是
            // string.Empty 而非 null，用 ?? 兜底会失效（历史缺陷即源于此）。
            string transportKind = string.IsNullOrWhiteSpace(info.TransportKind)
                ? "Tcp"
                : info.TransportKind.Trim();

            // 站号：Station 为空时回落到 StationNo，两者都为空则传空串
            string station = !string.IsNullOrWhiteSpace(info.Station)
                ? info.Station.Trim()
                : (info.StationNo > 0 ? info.StationNo.ToString() : string.Empty);

            (bool success, string code, string msg, string _) =
                await _client.RegisterRouteAsync(
                    routeId,
                    info.Protocol ?? string.Empty,
                    transportKind,
                    info.Ip ?? string.Empty,
                    info.Port,
                    station,
                    info.SerialPort ?? string.Empty,
                    info.BaudRate
                ).ConfigureAwait(false);

            if (!success)
            {
                _log?.Error("Device",
                    string.Format("{0}失败 route={1} protocol={2} code={3}: {4}",
                        actionLabel, routeId, info.Protocol, code, msg));
                RaiseFailure(string.Format("{0}失败：{1}", actionLabel,
                    string.IsNullOrWhiteSpace(msg) ? code : msg));
                return;
            }

            _log?.Info("Device", string.Format("{0}成功: {1}", actionLabel, info.Name ?? routeId));
            Load();
        }

        /// <summary>切回 UI 线程触发 <see cref="OperationFailed"/>，供界面弹出提示。</summary>
        private void RaiseFailure(string message)
        {
            Application app = Application.Current;
            if (app == null) return;

            app.Dispatcher.InvokeAsync(() => OperationFailed?.Invoke(message));
        }

        /// <summary>留存不随 gRPC 传输的本地元数据（名称 / 型号 / 轨道）。</summary>
        private void SaveMeta(string routeId, DeviceInfo info)
        {
            lock (_metaLock)
            {
                _localMeta[routeId] = new LocalDeviceMeta {
                    Name       = info.Name,
                    Model      = info.Model,
                    IsDualLane = info.IsDualLane
                };
            }
        }

        /// <summary>读取本地元数据；不存在返回 null。</summary>
        private LocalDeviceMeta GetMeta(string routeId)
        {
            lock (_metaLock)
            {
                return _localMeta.TryGetValue(routeId, out LocalDeviceMeta meta) ? meta : null;
            }
        }

        /// <summary>
        /// 删除指定路由：
        /// 1. 停止本地状态监听（取消 WatchRouteStatus 流）；
        /// 2. 通过 gRPC RemoveRoute 通知 EngineHost 注销路由并断开 PLC 连接；
        ///    服务端返回 Unimplemented 时优雅降级，仅执行本地删除；
        /// 3. 从本地 Devices 集合移除对应条目。
        /// </summary>
        /// <param name="id">要移除的路由 ID。</param>
        public void Remove(string id)
        {
            // 1. 先停止该设备的状态流监听
            Disconnect(id);

            Task.Run(async () =>
            {
                // 2. 通知服务端注销路由；Unimplemented 时客户端内部已视为成功
                (bool success, string code, string msg) =
                    await _client.RemoveRouteAsync(id).ConfigureAwait(false);

                if (!success)
                {
                    // 服务端返回业务失败（非 Unimplemented）：仍继续本地删除，但记录警告
                    _log?.Warn("Device",
                        string.Format("RemoveRoute 返回失败 route={0} code={1}: {2}", id, code, msg));
                }

                // 3. 切回 UI 线程更新 ObservableCollection
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    DeviceInfo target = FindDevice(id);
                    if (target != null)
                        Devices.Remove(target);
                });
            });
        }

        /// <summary>
        /// 启动对指定设备的状态监听。
        /// 先将设备状态置为 Connecting，然后在后台任务中调用 WatchRouteStatus 流，
        /// 收到每个状态事件后切回 UI 线程更新对应 DeviceInfo 的属性。
        /// </summary>
        /// <param name="id">目标路由 ID。</param>
        /// <param name="ct">外部取消令牌，取消后停止监听。</param>
        public async Task ConnectAsync(string id, CancellationToken ct)
        {
            CancellationTokenSource oldCts = null;
            lock (_watchLock)
            {
                if (_watchTasks.TryGetValue(id, out oldCts))
                {
                    oldCts.Cancel();
                    _watchTasks.Remove(id);
                }
            }
            oldCts?.Dispose();

            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_watchLock)
            {
                _watchTasks[id] = cts;
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                DeviceInfo dev = FindDevice(id);
                if (dev != null)
                {
                    dev.StatusType  = DeviceStatusType.Connecting;
                    dev.IsConnected = false;
                }
            });

            _ = Task.Run(async () =>
            {
                await _client.WatchRouteStatusAsync(id, async dto =>
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        DeviceInfo dev = FindDevice(dto.RouteId);
                        if (dev == null) return;

                        if (dto.Online)
                        {
                            dev.IsConnected = true;
                            dev.StatusType  = DeviceStatusType.Success;
                        }
                        else
                        {
                            dev.IsConnected = false;
                            dev.StatusType  = string.IsNullOrEmpty(dto.ErrorCode)
                                ? DeviceStatusType.Offline
                                : DeviceStatusType.Error;
                        }
                    });
                },
                // 流中断回调：立刻把设备置为错误态，避免界面残留虚假的"已连接"绿灯。
                // 客户端会自动退避重连，恢复后状态流会推来真实状态。
                onDisconnected: async () =>
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        DeviceInfo dev = FindDevice(id);
                        if (dev == null) return;

                        dev.IsConnected = false;
                        dev.StatusType  = DeviceStatusType.Error;
                    });
                },
                ct: cts.Token).ConfigureAwait(false);
            }, cts.Token);
        }

        /// <summary>
        /// 断开对指定设备的状态监听，将其状态置为离线。
        /// </summary>
        /// <param name="id">目标路由 ID。</param>
        public void Disconnect(string id)
        {
            CancellationTokenSource cts = null;
            lock (_watchLock)
            {
                if (_watchTasks.TryGetValue(id, out cts))
                    _watchTasks.Remove(id);
            }

            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                DeviceInfo dev = FindDevice(id);
                if (dev != null)
                {
                    dev.IsConnected = false;
                    dev.StatusType  = DeviceStatusType.Offline;
                }
            });
        }

        // -------------------------------------------------------------------------
        // 私有辅助方法
        // -------------------------------------------------------------------------

        /// <summary>
        /// 在 Devices 集合中查找指定 ID 的设备。
        /// 必须在 UI 线程上调用。
        /// </summary>
        private DeviceInfo FindDevice(string id)
        {
            foreach (DeviceInfo d in Devices)
            {
                if (d.Id == id)
                    return d;
            }
            return null;
        }
    }
}
