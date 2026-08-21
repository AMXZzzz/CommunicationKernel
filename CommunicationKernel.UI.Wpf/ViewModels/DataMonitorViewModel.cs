// -----------------------------------------------------------------------------
// 文件: ViewModels/DataMonitorViewModel.cs
// 层级: UI 层 — MES 监控页 ViewModel
// 作用: 从 IDeviceService.Devices 同步设备列表，供 DataMonitorPage 的
//       ItemsControl 通过数据绑定动态生成 MesDeviceCard 卡片。
//       订阅 CollectionChanged，在后台线程安全地切回 UI 线程更新 MonitoredDevices。
// 调用链:
//   IDeviceService.Devices（ObservableCollection）
//     → CollectionChanged 事件
//       → DataMonitorViewModel.RebuildMonitoredDevices()
//         → MonitoredDevices（ObservableCollection）
//           → DataMonitorPage ItemsControl
//             → MesDeviceCard（DeviceName / DeviceStatusKind 等 DP 绑定）
// -----------------------------------------------------------------------------

using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.ViewModels;

/// <summary>
/// MES 数据监控页（DataMonitorPage）的 ViewModel。
/// 监听 <see cref="IDeviceService.Devices"/> 集合变化，
/// 同步维护供 UI 绑定的 <see cref="MonitoredDevices"/> 集合。
/// </summary>
public sealed class DataMonitorViewModel : ViewModelBase {

    // -------------------------------------------------------------------------
    // 私有字段
    // -------------------------------------------------------------------------

    /// <summary>设备管理服务，提供实时的 Devices 集合。</summary>
    private readonly IDeviceService _deviceService;

    // -------------------------------------------------------------------------
    // 公开属性
    // -------------------------------------------------------------------------

    /// <summary>
    /// 供 DataMonitorPage 的 ItemsControl 绑定的设备列表。
    /// 内容来自 <see cref="IDeviceService.Devices"/>，在 UI 线程同步更新。
    /// </summary>
    public ObservableCollection<DeviceInfo> MonitoredDevices { get; }
        = new ObservableCollection<DeviceInfo>();

    // -------------------------------------------------------------------------
    // 命令
    // -------------------------------------------------------------------------

    /// <summary>手动刷新命令：触发 IDeviceService.Load() 重新从 EngineHost 拉取路由列表。</summary>
    public ICommand RefreshCommand { get; }

    // -------------------------------------------------------------------------
    // 构造函数
    // -------------------------------------------------------------------------

    /// <param name="deviceService">设备管理服务，必须非 null。</param>
    public DataMonitorViewModel(IDeviceService deviceService) {
        // 保存服务引用并校验非空
        _deviceService = deviceService
            ?? throw new ArgumentNullException(nameof(deviceService));

        // 订阅设备集合变化事件，任意线程均可触发，内部切回 UI 线程
        _deviceService.Devices.CollectionChanged += (_, __) => RebuildMonitoredDevices();

        // 绑定刷新命令：触发 gRPC 重新加载路由
        RefreshCommand = new RelayCommand(() => _deviceService.Load());

        // 初始化时同步一次当前设备列表（可能 App 启动时已调用 Load() 加载完毕）
        RebuildMonitoredDevices();
    }

    // -------------------------------------------------------------------------
    // 私有方法
    // -------------------------------------------------------------------------

    /// <summary>
    /// 将 <see cref="IDeviceService.Devices"/> 的当前快照同步到 <see cref="MonitoredDevices"/>。
    /// 使用增量对比（移除已删除、追加新增）以减少 UI 刷新量。
    /// 若当前不在 UI 线程，使用 Dispatcher.InvokeAsync 切换。
    /// </summary>
    private void RebuildMonitoredDevices() {
        // 获取 Dispatcher：若 Application 未初始化（如单元测试）则直接同步执行
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) {
            // 已在 UI 线程或无 Dispatcher，直接执行
            DoRebuild();
        } else {
            // 从后台线程切回 UI 线程
            dispatcher.InvokeAsync(DoRebuild);
        }
    }

    /// <summary>
    /// 实际执行设备列表同步，必须在 UI 线程调用。
    /// </summary>
    private void DoRebuild() {
        // 快照当前服务中的设备 ID 集合，用于差量对比
        var sourceIds = new System.Collections.Generic.HashSet<string>();
        foreach (DeviceInfo d in _deviceService.Devices) {
            if (d != null && !string.IsNullOrEmpty(d.Id))
                sourceIds.Add(d.Id);
        }

        // 移除 MonitoredDevices 中已不存在的设备（从后往前遍历，避免索引偏移）
        for (int i = MonitoredDevices.Count - 1; i >= 0; i--) {
            DeviceInfo existing = MonitoredDevices[i];
            if (existing == null || !sourceIds.Contains(existing.Id)) {
                // 此设备已从服务中删除，同步移除显示列表
                MonitoredDevices.RemoveAt(i);
            }
        }

        // 构建已显示的 ID 集合，用于判断是否需要新增
        var displayedIds = new System.Collections.Generic.HashSet<string>();
        foreach (DeviceInfo d in MonitoredDevices) {
            if (d != null && !string.IsNullOrEmpty(d.Id))
                displayedIds.Add(d.Id);
        }

        // 追加服务中新增但尚未显示的设备
        foreach (DeviceInfo d in _deviceService.Devices) {
            if (d == null || string.IsNullOrEmpty(d.Id))
                continue;

            if (!displayedIds.Contains(d.Id)) {
                // 新设备，追加到列表末尾
                MonitoredDevices.Add(d);
            }
        }
    }
}
