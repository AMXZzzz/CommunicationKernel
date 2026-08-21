// 文件: Services/GrpcDeviceService.cs
// 层级: UI层 — 服务实现
// 作用: IDeviceService 的 gRPC 实现，封装 EngineHostGrpcClient 的路由管理调用。
//       维护内存中的 ObservableCollection<DeviceInfo>，所有集合修改均切回 UI 线程。
//       ConnectAsync 为每个路由启动独立的 WatchRouteStatus 后台任务，
//       Disconnect 通过 CancellationTokenSource 停止对应的后台任务。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
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

        /// <summary>
        /// 每个路由的状态监听取消令牌源，Key = RouteId。
        /// 调用 Disconnect 时通过此字典找到对应的 CTS 并取消。
        /// </summary>
        private readonly Dictionary<string, CancellationTokenSource> _watchTasks
            = new Dictionary<string, CancellationTokenSource>();

        /// <summary>用于保护 _watchTasks 字典的同步锁。</summary>
        private readonly object _watchLock = new object();

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
        public GrpcDeviceService(EngineHostGrpcClient client)
        {
            // 保存 gRPC 客户端引用
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        // -------------------------------------------------------------------------
        // IDeviceService 实现
        // -------------------------------------------------------------------------

        /// <summary>
        /// 从 gRPC 后端加载路由列表并重建本地 Devices 集合。
        /// 在后台线程发起 QueryRoutes，收到结果后切回 UI 线程更新集合。
        /// </summary>
        public void Load()
        {
            // 在后台线程发起 gRPC 查询，不阻塞调用方
            Task.Run(async () =>
            {
                try
                {
                    // 调用 QueryRoutes，无过滤条件 = 返回所有路由
                    IReadOnlyList<RouteDto> routes = await _client
                        .QueryRoutesAsync()
                        .ConfigureAwait(false);

                    // 切回 UI 线程更新 ObservableCollection（WPF 要求）
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        // 清空旧列表
                        Devices.Clear();

                        // 将 gRPC RouteDto 映射为本地 DeviceInfo
                        foreach (RouteDto r in routes)
                        {
                            // 构造 DeviceInfo 并填充来自 gRPC 的字段
                            Devices.Add(new DeviceInfo
                            {
                                Id            = r.RouteId,
                                Name          = r.RouteId,   // gRPC 无 Name 字段，默认用 Id
                                Protocol      = r.ProtocolId,
                                Ip            = r.Address,
                                Port          = r.Port,
                                Station       = r.Station,
                                TransportKind = r.TransportKind,
                                StatusType    = DeviceStatusType.Offline,
                                IsConnected   = false,
                            });
                        }
                    });
                }
                catch (Exception)
                {
                    // 加载失败时静默处理，不中断 UI 线程
                    // 上层 ViewModel 若需感知失败可订阅 IAppLogger
                }
            });
        }

        /// <summary>
        /// 向 gRPC 后端注册新路由，完成后刷新本地设备列表。
        /// </summary>
        /// <param name="info">包含连接参数的新设备信息。</param>
        public void Add(DeviceInfo info)
        {
            // 若未指定 Id，自动生成 Guid 作为路由 ID
            string routeId = string.IsNullOrWhiteSpace(info.Id)
                ? Guid.NewGuid().ToString("N")
                : info.Id;

            // 在后台线程执行 gRPC 注册，避免阻塞 UI
            Task.Run(async () =>
            {
                await _client.RegisterRouteAsync(
                    routeId,
                    info.Protocol        ?? string.Empty,
                    info.TransportKind   ?? "TCP",
                    info.Ip              ?? string.Empty,
                    info.Port,
                    info.Station         ?? info.StationNo.ToString()
                ).ConfigureAwait(false);

                // 注册完成后重新加载列表以同步最新状态
                Load();
            });
        }

        /// <summary>
        /// 以新参数重新注册已有路由（EngineHost 支持幂等 RegisterRoute），
        /// 完成后刷新本地设备列表。
        /// </summary>
        /// <param name="info">已修改参数的设备信息，Id 须与现有设备匹配。</param>
        public void Update(DeviceInfo info)
        {
            // 在后台线程执行 gRPC 重新注册
            Task.Run(async () =>
            {
                await _client.RegisterRouteAsync(
                    info.Id              ?? string.Empty,
                    info.Protocol        ?? string.Empty,
                    info.TransportKind   ?? "TCP",
                    info.Ip              ?? string.Empty,
                    info.Port,
                    info.Station         ?? info.StationNo.ToString()
                ).ConfigureAwait(false);

                // 更新完成后重新加载列表
                Load();
            });
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

            // 2. 在后台线程向 EngineHost 发送 RemoveRoute，然后更新本地集合
            Task.Run(async () =>
            {
                // 通知服务端注销路由；Unimplemented 时客户端内部已视为成功
                (bool success, string code, string msg) =
                    await _client.RemoveRouteAsync(id).ConfigureAwait(false);

                if (!success)
                {
                    // 服务端返回业务失败（非 Unimplemented）：仍继续本地删除，但记录警告
                    System.Diagnostics.Debug.WriteLine(
                        $"[GrpcDeviceService] RemoveRoute 失败 route={id} code={code} msg={msg}");
                }

                // 3. 切回 UI 线程更新 ObservableCollection
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    DeviceInfo target = null;
                    foreach (DeviceInfo d in Devices)
                    {
                        if (d.Id == id)
                        {
                            target = d;
                            break;
                        }
                    }

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
            // 若已有监听任务在运行，先取消旧任务
            CancellationTokenSource oldCts = null;
            lock (_watchLock)
            {
                if (_watchTasks.TryGetValue(id, out oldCts))
                {
                    // 取消旧的监听任务
                    oldCts.Cancel();
                    _watchTasks.Remove(id);
                }
            }

            // 等待旧的 CTS 释放（简单处理，不等待后台任务真正结束）
            oldCts?.Dispose();

            // 创建新的 CTS，将外部 ct 链接进来以支持级联取消
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_watchLock)
            {
                // 注册新的监听任务 CTS
                _watchTasks[id] = cts;
            }

            // 将设备状态置为 Connecting，UI 可显示"连接中"
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                DeviceInfo dev = FindDevice(id);
                if (dev != null)
                {
                    // 标记为正在连接
                    dev.StatusType  = DeviceStatusType.Connecting;
                    dev.IsConnected = false;
                }
            });

            // 在后台线程中运行状态流监听，不阻塞调用方
            _ = Task.Run(async () =>
            {
                await _client.WatchRouteStatusAsync(id, async dto =>
                {
                    // 每收到一个状态事件，切回 UI 线程更新属性
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        DeviceInfo dev = FindDevice(dto.RouteId);
                        if (dev == null)
                        {
                            // 未找到对应设备，忽略此事件
                            return;
                        }

                        if (dto.Online)
                        {
                            // 设备上线：更新为成功状态
                            dev.IsConnected = true;
                            dev.StatusType  = DeviceStatusType.Success;
                        }
                        else
                        {
                            // 设备离线：检查是否有错误信息
                            dev.IsConnected = false;
                            dev.StatusType  = string.IsNullOrEmpty(dto.ErrorCode)
                                ? DeviceStatusType.Offline   // 正常下线
                                : DeviceStatusType.Error;    // 有错误码，标记为错误
                        }
                    });
                }, cts.Token).ConfigureAwait(false);
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
                // 从字典中取出并移除对应的 CTS
                if (_watchTasks.TryGetValue(id, out cts))
                    _watchTasks.Remove(id);
            }

            if (cts != null)
            {
                // 取消后台监听任务
                cts.Cancel();
                cts.Dispose();
            }

            // 切回 UI 线程更新设备状态
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                DeviceInfo dev = FindDevice(id);
                if (dev != null)
                {
                    // 标记为已断开
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
        /// <param name="id">路由 ID。</param>
        /// <returns>找到的 DeviceInfo，未找到则返回 null。</returns>
        private DeviceInfo FindDevice(string id)
        {
            // 遍历查找，集合通常较小无需索引
            foreach (DeviceInfo d in Devices)
            {
                if (d.Id == id)
                    return d;
            }
            // 未找到返回 null
            return null;
        }
    }
}
