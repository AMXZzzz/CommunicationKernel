#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/GrpcDeviceService.cs
// 层级: UI 层 — WPF 服务实现
// 作用: IDeviceService 的 gRPC 实现，封装路由注册/查询/状态流，并作为 IRouteReconciler 恢复丢失路由。
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
    /// 通过 <see cref="HostClient"/> 与 Host.App 通信，
    /// 将路由信息映射为本地 <see cref="DeviceInfo"/> 对象。
    /// </summary>
    public sealed class GrpcDeviceService : IDeviceService, IRouteReconciler
    {
        // ============================================================================
        // 私有字段
        // ============================================================================

        /// <summary>gRPC 客户端，用于调用 RegisterRoute / QueryRoutes / WatchRouteStatus。</summary>
        private readonly HostClient _client;

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
        /// 设备配置的本地持久化存储，Key = RouteId。
        /// </summary>
        /// <remarks>
        /// 它同时承担两个职责：
        /// <list type="number">
        ///   <item>
        ///     留存 gRPC 路由模型里没有的展示元数据（Name / Model / 轨道）。
        ///     不留存的话，注册成功后 Load() 回填时名称会被 RouteId 覆盖。
        ///   </item>
        ///   <item>
        ///     作为重新注册的依据。宿主重启后其内存路由全部消失，
        ///     只有这份配置能把设备重新推回去。
        ///   </item>
        /// </list>
        /// </remarks>
        private readonly DeviceConfigStore _config;

        /// <summary>
        /// 重注册闸门：合并同一路由上的并发请求，并施加最小重试间隔。
        /// 时序逻辑本身与界面无关，已下沉到客户端层以便直接测试。
        /// </summary>
        private readonly RouteReconcileGate _reconcileGate
            = new RouteReconcileGate(TimeSpan.FromSeconds(5));

        // ============================================================================
        // 事件
        // ============================================================================

        /// <inheritdoc />
        public event Action<string> OperationFailed;

        // ============================================================================
        // 公开属性
        // ============================================================================

        /// <summary>
        /// 当前设备列表，ObservableCollection 自动通知 WPF 列表控件刷新。
        /// 所有修改操作均通过 UI 线程 Dispatcher 执行。
        /// </summary>
        public ObservableCollection<DeviceInfo> Devices { get; }
            = new ObservableCollection<DeviceInfo>();

        // ============================================================================
        // 构造函数
        // ============================================================================

        /// <summary>
        /// 初始化 GrpcDeviceService。
        /// </summary>
        /// <param name="client">已初始化的 gRPC 客户端。</param>
        /// <param name="log">可选日志记录器，为 null 时不记录日志。</param>
        public GrpcDeviceService(HostClient client, IAppLogger log = null)
        {
            // gRPC 客户端必填；日志可空；本地配置用于名称还原与宿主重启后重注册
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _log    = log;
            _config = new DeviceConfigStore(log);

            // 先用本地配置把设备列表填满，再由 Load() 与服务端对账。
            //
            // 顺序很重要：宿主没起来时 QueryRoutes 会失败，若等它返回再填列表，
            // 界面就会是空的，操作员看不出「设备还在、只是宿主连不上」。
            // 此处构造发生在 DI 建容器时（UI 线程），可直接操作 ObservableCollection。
            foreach (DeviceConfigStore.DeviceRecord record in _config.GetAll())
                Devices.Add(ToDeviceInfo(record));
        }

        /// <summary>把持久化记录还原成界面用的设备对象（状态一律从离线起步）。</summary>
        private static DeviceInfo ToDeviceInfo(DeviceConfigStore.DeviceRecord record)
        {
            return new DeviceInfo
            {
                Id                = record.Id,
                Name              = string.IsNullOrWhiteSpace(record.Name) ? record.Id : record.Name,
                Model             = record.Model ?? string.Empty,

                // Lane 由 IsDualLane 派生，只读，无需也不能单独还原
                IsDualLane        = record.IsDualLane,
                Protocol          = record.Protocol ?? string.Empty,
                Ip                = record.Ip ?? string.Empty,
                Port              = record.Port,
                Station           = record.Station ?? string.Empty,
                StationNo         = record.StationNo,
                SerialPort        = record.SerialPort ?? string.Empty,
                BaudRate          = record.BaudRate,
                TransportKind     = record.TransportKind ?? string.Empty,
                ExtraSettingsJson = record.ExtraSettingsJson ?? string.Empty,

                // 运行期状态不持久化：显示一个从未验证过的连接状态比不显示更糟
                StatusType        = DeviceStatusType.Offline,
                IsConnected       = false
            };
        }

        // ============================================================================
        // IDeviceService 实现
        // ============================================================================

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
            // 后台拉取，避免 QueryRoutes 阻塞设备页
            Task.Run(async () =>
            {
                try
                {
                    // 向 Host.App 查询当前内存中的全部路由
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

                        // 1. 处理本地有、服务端没有的路由。
                        //
                        //    只有「本地也没有持久化配置」的条目才真正删除——
                        //    那是上一轮从服务端同步来的临时条目，服务端删了就该消失。
                        //
                        //    若本地存有配置，说明这是操作员配置过的设备，宿主重启
                        //    丢了自己的内存路由而已。此前这里一律删除，导致宿主一重启
                        //    界面上的设备就全部消失，只能手工重录。现在保留并标记离线，
                        //    等下一次读写触发 EnsureRouteAsync 自动重新注册。
                        for (int i = Devices.Count - 1; i >= 0; i--)
                        {
                            DeviceInfo local = Devices[i];
                            // 服务端仍有此路由：保留，后面再更新元数据
                            if (serverIds.Contains(local.Id)) continue;

                            // 本地也无配置：属于临时同步条目，删除
                            if (_config.Get(local.Id) == null)
                            {
                                Devices.RemoveAt(i);
                                continue;
                            }

                            // 操作员配置过的设备：宿主丢了路由，保留卡片并标离线
                            local.IsConnected = false;
                            local.StatusType  = DeviceStatusType.Offline;
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
                                DeviceConfigStore.DeviceRecord meta = _config.Get(r.RouteId);

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
                    // 宿主不可达时保留本地列表，只记日志
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
            // 空对象无法注册路由
            if (info == null) return;

            // 未指定 Id 时生成 Guid，作为 RegisterRoute 的 RouteId
            string routeId = string.IsNullOrWhiteSpace(info.Id)
                ? Guid.NewGuid().ToString("N")
                : info.Id;

            // 先留存本地元数据，Load() 回填时据此还原名称，避免被 RouteId 覆盖
            SaveMeta(routeId, info);

            // 后台注册，成功后 Load() 刷新卡片
            Task.Run(() => RegisterAsync(routeId, info, "添加设备"));
        }

        /// <summary>
        /// 以新参数重新注册已有路由（Host.App 支持幂等 RegisterRoute），
        /// 完成后刷新本地设备列表。
        /// </summary>
        /// <param name="info">已修改参数的设备信息，Id 须与现有设备匹配。</param>
        public void Update(DeviceInfo info)
        {
            // 更新必须带已有 RouteId
            if (info == null || string.IsNullOrWhiteSpace(info.Id)) return;

            // 先更新本地配置，即使后续 RPC 失败名称也不会丢
            SaveMeta(info.Id, info);

            // Host.App 的 RegisterRoute 拒绝重复 RouteId，因此改参数必须先注销再注册。
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

            // 向 Host.App 注册路由（协议、介质、地址、站号、串口参数）
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
                // 注册失败：记日志并弹给操作员，避免设备「悄无声息地不出现」
                _log?.Error("Device",
                    string.Format("{0}失败 route={1} protocol={2} code={3}: {4}",
                        actionLabel, routeId, info.Protocol, code, msg));
                RaiseFailure(string.Format("{0}失败：{1}", actionLabel,
                    string.IsNullOrWhiteSpace(msg) ? code : msg));
                return;
            }

            // 成功后刷新列表，合并服务端路由与本地元数据
            _log?.Info("Device", string.Format("{0}成功: {1}", actionLabel, info.Name ?? routeId));
            Load();
        }

        /// <summary>切回 UI 线程触发 <see cref="OperationFailed"/>，供界面弹出提示。</summary>
        private void RaiseFailure(string message)
        {
            // 应用正在退出时 Dispatcher 可能已空
            Application app = Application.Current;
            if (app == null) return;

            // 切回 UI 线程，订阅方（设备页）可直接弹框
            app.Dispatcher.InvokeAsync(() => OperationFailed?.Invoke(message));
        }

        /// <summary>
        /// 把设备配置写入本地持久化存储。
        /// </summary>
        /// <remarks>
        /// 存两类东西：gRPC 路由模型没有的展示元数据（名称/型号/轨道），
        /// 以及重新注册这条路由所需的全部连接参数。后者是宿主重启后
        /// 能自动恢复的前提——宿主侧路由是纯内存的。
        /// </remarks>
        private void SaveMeta(string routeId, DeviceInfo info)
        {
            // 写入 devices.json，供 Load 还原名称以及 EnsureRouteAsync 重注册
            _config.Save(routeId, info);
        }

        // ============================================================================
        // IRouteReconciler 实现
        // ============================================================================

        /// <inheritdoc />
        public Task<bool> EnsureRouteAsync(string routeId, CancellationToken ct)
        {
            // 空 ID 无法对账
            if (string.IsNullOrWhiteSpace(routeId))
                return Task.FromResult(false);

            // 分支1：本地没有这台设备的配置——可能是操作员刚删掉的，不要复活它
            DeviceConfigStore.DeviceRecord record = _config.Get(routeId);
            if (record == null)
                return Task.FromResult(false);

            // 并发合并与重试节流都由闸门负责：一台设备上挂几十个变量时，
            // 宿主重启会让它们同时收到 RouteNotFound，逐个发起会打爆宿主。
            return _reconcileGate.RunAsync(routeId, () => ReconcileCoreAsync(routeId, record, ct));
        }

        /// <summary>实际执行一次重新注册。</summary>
        private async Task<bool> ReconcileCoreAsync(
            string routeId, DeviceConfigStore.DeviceRecord record, CancellationToken ct)
        {
            try
            {
                // 空介质按 Tcp；站号优先用字符串，否则回落 StationNo
                string transportKind = string.IsNullOrWhiteSpace(record.TransportKind)
                    ? "Tcp"
                    : record.TransportKind.Trim();

                string station = !string.IsNullOrWhiteSpace(record.Station)
                    ? record.Station.Trim()
                    : (record.StationNo > 0 ? record.StationNo.ToString() : string.Empty);

                // 用本地留存的连接参数重新 RegisterRoute
                (bool success, string code, string msg, string _) =
                    await _client.RegisterRouteAsync(
                        routeId,
                        record.Protocol ?? string.Empty,
                        transportKind,
                        record.Ip ?? string.Empty,
                        record.Port,
                        station,
                        record.SerialPort ?? string.Empty,
                        record.BaudRate
                    ).ConfigureAwait(false);

                if (!success)
                {
                    // 重注册失败（宿主未起或 PLC 不可达）：调用方继续退避
                    _log?.Warn("Device", string.Format(
                        "路由 {0} 自动重新注册失败 code={1}: {2}", routeId, code, msg));
                    return false;
                }

                _log?.Info("Device", string.Format(
                    "路由 {0} 已自动重新注册（宿主侧此前不存在该路由）", routeId));

                // 刻意不在这里改 StatusType：重新注册只证明「路由存在」，
                // 不证明「与 PLC 通讯正常」。连接状态一律由 WatchRouteStatus
                // 依据实际链路事件驱动，此处越权设置会显示出未经验证的在线状态。
                return true;
            }
            catch (OperationCanceledException)
            {
                // 轮询任务已取消，不再重试
                return false;
            }
            catch (Exception ex)
            {
                // 宿主整个连不上时会走到这里；调用方按失败处理并继续退避即可
                _log?.Warn("Device", string.Format(
                    "路由 {0} 自动重新注册异常: {1}", routeId, ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 删除指定路由：
        /// 1. 停止本地状态监听（取消 WatchRouteStatus 流）；
        /// 2. 通过 gRPC RemoveRoute 通知 Host.App 注销路由并断开 PLC 连接；
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
                    // 服务端未能注销：绝不本地删除。
                    // 否则界面上设备消失、服务端却仍持有该路由与 PLC 连接，
                    // 两侧状态从此分叉，且该 RouteId 再也无法重新注册。
                    _log?.Error("Device",
                        string.Format("删除设备失败 route={0} code={1}: {2}", id, code, msg));
                    RaiseFailure(string.Format("删除设备失败：{0}",
                        string.IsNullOrWhiteSpace(msg) ? code : msg));
                    return;
                }

                // 3. 服务端已注销，切回 UI 线程移除本地条目
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    DeviceInfo target = FindDevice(id);
                    if (target != null)
                        Devices.Remove(target);
                });

                // 配置随设备一并清除：既避免同 RouteId 复用时残留旧名称，
                // 也确保 EnsureRouteAsync 不会把操作员刚删掉的设备又注册回去
                _config.Delete(id);
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
            // 若该路由已有状态流，先取消旧的，避免双流争写同一 DeviceInfo
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

            // 把外部取消与本连接生命周期绑在一起
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_watchLock)
            {
                _watchTasks[id] = cts;
            }

            // 卡片先显示「连接中」，真正结果由状态流推送
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                DeviceInfo dev = FindDevice(id);
                if (dev != null)
                {
                    dev.StatusType  = DeviceStatusType.Connecting;
                    dev.IsConnected = false;
                }
            });

            // 后台消费 WatchRouteStatus 流
            _ = Task.Run(async () =>
            {
                await _client.WatchRouteStatusAsync(id, async dto =>
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        DeviceInfo dev = FindDevice(dto.RouteId);
                        // 设备可能已被删除
                        if (dev == null) return;

                        if (dto.Online)
                        {
                            // 在线：绿灯
                            dev.IsConnected = true;
                            dev.StatusType  = DeviceStatusType.Success;
                        }
                        else
                        {
                            // 离线：无错误码视为正常断开，有错误码标红
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

                        // 流中断：标红，等待客户端重连后再推真实状态
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
            // 取出并移除该路由的状态流取消源
            CancellationTokenSource cts = null;
            lock (_watchLock)
            {
                if (_watchTasks.TryGetValue(id, out cts))
                    _watchTasks.Remove(id);
            }

            if (cts != null)
            {
                // 取消 WatchRouteStatus 并释放令牌
                cts.Cancel();
                cts.Dispose();
            }

            // 切回 UI 线程把卡片置为离线
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

        // ============================================================================
        // 私有辅助方法
        // ============================================================================

        /// <summary>
        /// 在 Devices 集合中查找指定 ID 的设备。
        /// 必须在 UI 线程上调用。
        /// </summary>
        private DeviceInfo FindDevice(string id)
        {
            foreach (DeviceInfo d in Devices)
            {
                // RouteId 精确匹配
                if (d.Id == id)
                    return d;
            }
            // 未找到（可能刚被删除）
            return null;
        }
    }
}
