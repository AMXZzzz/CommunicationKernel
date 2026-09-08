#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/GrpcDeviceService.cs
// 层级: UI 层 — WPF 服务实现
// 作用: IDeviceService 的 gRPC 实现，封装路由注册/查询/状态流，并作为 IRouteReconciler 恢复丢失路由。
//
// 本文件<b>不引用 System.Windows</b>，也不持有任何界面集合。
//   此前它持有 ObservableCollection<DeviceInfo>，并在 9 处用
//   Application.Current.Dispatcher.InvokeAsync 切回 UI 线程改集合与改属性。
//   现在改为：只产出数据、发事件，切线程由订阅方（ViewModels/DeviceListModel）负责。
//
//   除了分层洁癖，这样做还修掉一个真实故障：Application.Current 在应用退出途中
//   会变成 null，那几处 InvokeAsync 会被整段跳过且不留任何日志——
//   表现是关闭过程中最后一批状态更新静默丢失。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Logging;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// <see cref="IDeviceService"/> 的 gRPC 实现。
    /// 通过 <see cref="HostingClient"/> 与 Hosting.App 通信，
    /// 将路由信息映射为本地 <see cref="DeviceInfo"/> 对象。
    /// </summary>
    public sealed class GrpcDeviceService : IDeviceService, IRouteReconciler
    {
        // ============================================================================
        // 私有字段
        // ============================================================================

        /// <summary>gRPC 客户端，用于调用 RegisterRoute / QueryRoutes / WatchRouteStatus。</summary>
        private readonly HostingClient _client;

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

        /// <inheritdoc />
        public event Action<IReadOnlyList<DeviceListItem>> DevicesChanged;

        /// <inheritdoc />
        public event Action<DeviceListItem> DeviceUpserted;

        /// <inheritdoc />
        public event Action<string> DeviceRemoved;

        /// <inheritdoc />
        public event Action<DeviceStatusUpdate> DeviceStatusChanged;

        // ============================================================================
        // 构造函数
        // ============================================================================

        /// <summary>
        /// 初始化 GrpcDeviceService。
        /// </summary>
        /// <param name="client">已初始化的 gRPC 客户端。</param>
        /// <param name="config">设备配置存储（必须非 null）。</param>
        /// <param name="log">可选日志记录器，为 null 时不记录日志。</param>
        /// <remarks>
        /// <paramref name="config"/> 由 DI 注入而不是在这里 <c>new</c>：
        /// 变量轮询与写入也要按 RouteId 查设备的字节序，各自再造一个实例
        /// 会得到两份内存镜像抢同一个 devices.json——一侧的保存会被另一侧覆盖，
        /// 且不报任何错。
        /// </remarks>
        public GrpcDeviceService(HostingClient client, DeviceConfigStore config, IAppLogger log = null)
        {
            // gRPC 客户端与配置存储必填；日志可空
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _log    = log;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 未与宿主对账过，因此一律按「宿主侧没有」给出：订阅方据此把设备显示为离线。
        /// 订阅方应当先取本快照做初始填充，再订阅事件——
        /// 宿主没起来时 <see cref="Load"/> 会失败，若等它返回才填列表，
        /// 界面就是空的，操作员看不出「设备还在、只是宿主连不上」。
        /// </remarks>
        public IReadOnlyList<DeviceListItem> Snapshot()
            => _config.GetAll()
                .Select(record => new DeviceListItem {
                    Device = ToDeviceInfo(record), PresentOnHost = false,
                })
                .ToList();

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

                // 漏掉这一行的后果是隐性的：卡片建出来时是 0，操作员改个名字触发
                // Update，就把现场调好的帧间静默悄悄写回 0 了，且不报任何错
                MinIoIntervalMs   = record.MinIoIntervalMs,
                ByteOrder         = record.ByteOrder,

                // 运行期状态不持久化：显示一个从未验证过的连接状态比不显示更糟
                StatusType        = DeviceStatusType.Offline,
                IsConnected       = false
            };
        }

        // ============================================================================
        // IDeviceService 实现
        // ============================================================================

        /// <summary>
        /// 从 gRPC 后端拉取路由列表，与本地配置合并后经 <see cref="DevicesChanged"/> 发布。
        /// </summary>
        /// <remarks>
        /// 立即返回，实际工作在后台线程；事件也在该后台线程触发。
        /// 宿主不可达时只记日志、不发事件——保留界面上的现有列表，
        /// 清空它会让操作员以为设备丢了。
        /// </remarks>
        public void Load()
        {
            // 后台拉取，避免 QueryRoutes 阻塞设备页
            Task.Run(async () =>
            {
                try
                {
                    // 向 Hosting.App 查询当前内存中的全部路由
                    IReadOnlyList<RouteDto> routes = await _client
                        .QueryRoutesAsync()
                        .ConfigureAwait(false);

                    // 合并成纯数据快照后发事件；落到界面集合是订阅方的事
                    DevicesChanged?.Invoke(BuildSnapshot(routes));
                }
                catch (Exception ex)
                {
                    // 宿主不可达时保留本地列表，只记日志
                    _log?.Error("Device", "加载设备列表失败", ex);
                }
            });
        }

        /// <summary>
        /// 把「宿主侧路由」与「本地配置」合并成当前应有的设备集合。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 结果 = 宿主侧路由 ∪ 本地配置。两个来源都要保留，理由不同：
        /// </para>
        /// <list type="bullet">
        ///   <item>
        ///     只在宿主侧的路由：别的上位机注册的，或本机配置被手工删过。
        ///     显示出来才能让操作员看到"这条链路确实存在"。
        ///   </item>
        ///   <item>
        ///     只在本地配置的设备：宿主重启丢了内存路由。此前的实现直接删掉，
        ///     导致宿主一重启界面上的设备全部消失、只能手工重录。
        ///     现在保留并标 PresentOnHost=false，等下一次读写触发对账自动补注册。
        ///   </item>
        /// </list>
        /// <para>
        /// 两边都没有的条目自然不在结果里，订阅方据此移除——
        /// 那是上一轮从宿主同步来的临时条目，宿主删了就该消失。
        /// </para>
        /// </remarks>
        private List<DeviceListItem> BuildSnapshot(IReadOnlyList<RouteDto> routes)
        {
            var result = new List<DeviceListItem>();
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 宿主侧路由：元数据以宿主为准，名称等本地专有字段从配置补
            foreach (RouteDto r in routes)
            {
                DeviceConfigStore.DeviceRecord meta = _config.Get(r.RouteId);

                result.Add(new DeviceListItem {
                    PresentOnHost = true,
                    Device = new DeviceInfo {
                        Id            = r.RouteId,
                        Name          = meta != null && !string.IsNullOrWhiteSpace(meta.Name)
                            ? meta.Name
                            : r.RouteId,
                        Model         = meta != null ? (meta.Model ?? string.Empty) : string.Empty,
                        IsDualLane    = meta != null && meta.IsDualLane,
                        Protocol      = r.ProtocolId,
                        Ip            = r.Address,
                        Port          = r.Port,
                        Station       = r.Station,
                        SerialPort    = r.SerialPort,
                        BaudRate      = r.BaudRate,
                        TransportKind = r.TransportKind,

                        // 从本地记录取，不从 RouteDto 取：gRPC 的路由模型里没有这一项。
                        // 漏了会把现场调好的帧间静默在下次 Update 时悄悄写回 0
                        MinIoIntervalMs = meta != null ? meta.MinIoIntervalMs : 0,

                        // 同上：字节序也只在本地记录里，RouteDto 不带它
                        ByteOrder     = meta != null ? meta.ByteOrder : "ABCD",

                        // 状态一律从离线起步；真实状态只由 WatchRouteStatus 流推送。
                        // 订阅方对已存在的条目会保留其当前状态，不会被这里覆盖
                        StatusType    = DeviceStatusType.Offline,
                        IsConnected   = false,
                    },
                });
                seen.Add(r.RouteId);
            }

            // 2. 仅存在于本地配置的设备：宿主重启丢了路由，保留并标为不在宿主侧
            foreach (DeviceConfigStore.DeviceRecord record in _config.GetAll())
            {
                if (record.Id == null || seen.Contains(record.Id)) continue;

                result.Add(new DeviceListItem {
                    Device = ToDeviceInfo(record), PresentOnHost = false,
                });
            }

            return result;
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
        /// 以新参数重新注册已有路由（Hosting.App 支持幂等 RegisterRoute），
        /// 完成后刷新本地设备列表。
        /// </summary>
        /// <param name="info">已修改参数的设备信息，Id 须与现有设备匹配。</param>
        public void Update(DeviceInfo info)
        {
            // 更新必须带已有 RouteId
            if (info == null || string.IsNullOrWhiteSpace(info.Id)) return;

            // 先更新本地配置，即使后续 RPC 失败名称也不会丢
            SaveMeta(info.Id, info);

            // Hosting.App 的 RegisterRoute 拒绝重复 RouteId，因此改参数必须先注销再注册。
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

            // 向 Hosting.App 注册路由（协议、介质、地址、站号、串口参数）
            //
            // minIoIntervalMs 必须显式传：省略会落到 SDK 签名上的默认值 100，
            // 而引擎只要收到正数就原样采用，TCP 路由也会被塞进 100ms 帧间静默，
            // 把链路限死在 10 次 I/O／秒。传 0 才是"按介质取引擎默认"。
            (bool success, string code, string msg, string _) =
                await _client.RegisterRouteAsync(
                    routeId,
                    info.Protocol ?? string.Empty,
                    transportKind,
                    info.Ip ?? string.Empty,
                    info.Port,
                    station,
                    info.SerialPort ?? string.Empty,
                    info.BaudRate,
                    info.MinIoIntervalMs
                ).ConfigureAwait(false);

            if (!success)
            {
                // 注册会真的去建连接。PLC 没上电、Modbus RTU 的 TCP 转串口网关
                // 还没接、设备临时拔线——这些情况下连接必然失败，
                // 但操作员填的配置完全正确。
                //
                // 配置在 Add/Update 里已先行落盘，这里只需把设备补进当前列表并置为离线；
                // 否则界面上「什么都没发生」，要重启上位机才看得到那台设备，
                // 操作员只会以为自己没保存成功，于是反复重录。
                if (RegisterFailure.ShouldKeepConfiguration(code))
                {
                    _log?.Warn("Device", string.Format(
                        "{0}：{1} 暂时连不上，配置已保留，将由对账自动补注册 [{2}] {3}",
                        actionLabel, routeId, code, msg));
                    RaiseFailure(string.Format(
                        "{0}：目标暂时连不上（{1}）。配置已保存，设备以离线显示，可达后自动接入。",
                        actionLabel, string.IsNullOrWhiteSpace(msg) ? code : msg));
                    ShowOfflinePlaceholder(routeId);
                    return;
                }

                // 配置本身有问题（协议不存在、介质不支持、参数非法）：
                // 保存下来只会得到一个永远起不来的条目，因此连带清掉本地配置
                _log?.Error("Device",
                    string.Format("{0}失败 route={1} protocol={2} code={3}: {4}",
                        actionLabel, routeId, info.Protocol, code, msg));
                RaiseFailure(string.Format("{0}失败：{1}", actionLabel,
                    string.IsNullOrWhiteSpace(msg) ? code : msg));
                _config.Delete(routeId);
                return;
            }

            // 成功后刷新列表，合并服务端路由与本地元数据
            _log?.Info("Device", string.Format("{0}成功: {1}", actionLabel, info.Name ?? routeId));
            Load();
        }

        /// <summary>
        /// 把一台「配置已保存、但宿主侧尚未注册成功」的设备补进列表并置为离线。
        /// </summary>
        /// <remarks>
        /// 目标不可达时走这条路径。设备必须当场出现在界面上——
        /// 界面上什么都不发生的话，操作员只会以为没保存成功，于是反复重录同一台设备。
        /// 幂等：已在列表里就只更新状态，不重复添加。
        /// </remarks>
        private void ShowOfflinePlaceholder(string routeId)
        {
            DeviceConfigStore.DeviceRecord record = _config.Get(routeId);
            if (record == null) return;

            // PresentOnHost=false：宿主侧注册没成功，订阅方据此把它显示为离线。
            // 幂等由订阅方保证——它按 Id 决定是新增还是就地更新
            DeviceUpserted?.Invoke(new DeviceListItem {
                Device = ToDeviceInfo(record), PresentOnHost = false,
            });
        }

        /// <summary>发布一条面向操作员的失败描述。</summary>
        /// <remarks>
        /// 在当前（后台）线程直接触发，不做线程切换——切回 UI 线程是订阅方的责任。
        /// 此前这里用 Application.Current.Dispatcher 转发，且在 Application.Current
        /// 为 null（应用退出途中）时直接 return，导致最后一批错误提示被静默吞掉。
        /// </remarks>
        private void RaiseFailure(string message) => OperationFailed?.Invoke(message);

        /// <summary>发布一台设备的连接状态变化。</summary>
        private void RaiseStatus(string routeId, bool connected, DeviceStatusType status)
            => DeviceStatusChanged?.Invoke(new DeviceStatusUpdate {
                RouteId = routeId, IsConnected = connected, Status = status,
            });

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

                // 用本地留存的连接参数重新 RegisterRoute。
                //
                // ct 必须传到底：注册会真的去建连接，串口打不开或 TCP 握手超时
                // 时这一步能耗掉数秒。不传的话，应用退出或对账被取消时这里
                // 仍在阻塞，关停要多等一条路由的建连超时——几十台设备就是几十倍。
                //
                // minIoIntervalMs 不传会落到 SDK 默认 100，理由同 RegisterAsync。
                (bool success, string code, string msg, string _) =
                    await _client.RegisterRouteAsync(
                        routeId,
                        record.Protocol ?? string.Empty,
                        transportKind,
                        record.Ip ?? string.Empty,
                        record.Port,
                        station,
                        record.SerialPort ?? string.Empty,
                        record.BaudRate,
                        record.MinIoIntervalMs,
                        ct
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
        /// 2. 通过 gRPC RemoveRoute 通知 Hosting.App 注销路由并断开 PLC 连接；
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

                // 3. 配置随设备一并清除：既避免同 RouteId 复用时残留旧名称，
                //    也确保 EnsureRouteAsync 不会把操作员刚删掉的设备又注册回去。
                //
                //    必须排在发事件之前：订阅方收到 DeviceRemoved 后可能立刻
                //    重新取快照，那时配置若还在，删掉的设备会当场复活
                _config.Delete(id);

                // 4. 通知订阅方移除界面条目
                DeviceRemoved?.Invoke(id);
            });
        }

        /// <summary>
        /// 启动对指定设备的状态监听。
        /// 先将设备状态置为 Connecting，然后在后台任务中调用 WatchRouteStatus 流，
        /// 收到每个状态事件后切回 UI 线程更新对应 DeviceInfo 的属性。
        /// </summary>
        /// <param name="id">目标路由 ID。</param>
        /// <param name="ct">外部取消令牌，取消后停止监听。</param>
        /// <remarks>
        /// 同步完成：本方法只负责挂起状态流并发一条「连接中」，不等待任何 I/O。
        /// 去掉 Dispatcher 之后这里已无 await，保留 async 只会得到 CS1998。
        /// 真正的连接结果由 <see cref="DeviceStatusChanged"/> 异步推送。
        /// </remarks>
        public Task ConnectAsync(string id, CancellationToken ct)
        {
            // 若该路由已有状态流，先取消旧的，避免两条流争相推送同一设备的状态
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
            RaiseStatus(id, connected: false, DeviceStatusType.Connecting);

            // 后台消费 WatchRouteStatus 流
            _ = Task.Run(async () =>
            {
                await _client.WatchRouteStatusAsync(id, dto =>
                {
                    // 在线绿灯；离线时无错误码视为正常断开，有错误码标红
                    RaiseStatus(
                        dto.RouteId,
                        dto.Online,
                        dto.Online
                            ? DeviceStatusType.Success
                            : string.IsNullOrEmpty(dto.ErrorCode)
                                ? DeviceStatusType.Offline
                                : DeviceStatusType.Error);

                    return Task.CompletedTask;
                },
                // 流中断回调：立刻把设备置为错误态，避免界面残留虚假的"已连接"绿灯。
                // 客户端会自动退避重连，恢复后状态流会推来真实状态。
                onDisconnected: () =>
                {
                    RaiseStatus(id, connected: false, DeviceStatusType.Error);
                    return Task.CompletedTask;
                },
                ct: cts.Token).ConfigureAwait(false);
            }, cts.Token);

            return Task.CompletedTask;
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

            // 发布离线状态；落到卡片上由订阅方在 UI 线程完成
            RaiseStatus(id, connected: false, DeviceStatusType.Offline);
        }
    }
}
