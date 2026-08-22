#nullable disable

// -----------------------------------------------------------------------------
// 文件: ViewModels/DevicePageViewModel.cs
// 层级: UI 层 — 设备管理页 ViewModel
// 作用: 封装设备列表的增删改查、连接/断开、批量选中删除。
//       View 通过命令和委托与 VM 交互，不持有 IDeviceService 引用。
//       DeviceCard 通过 ConnectDevice / DisconnectDevice 委托触发连接，
//       VM 负责调用服务层；View 只关心 UI 状态。
// 调用链:
//   DevicePage → DevicePageViewModel
//     → IDeviceService.Add/Update/Remove/ConnectAsync/Disconnect
//       → GrpcDeviceService → HostClient
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Logging;
using CommunicationKernel.UI.Wpf.Core.Models;
using CommunicationKernel.UI.Wpf.Views.Pages.Device;

namespace CommunicationKernel.UI.Wpf.ViewModels;

/// <summary>
/// 设备管理页（DevicePage）的 ViewModel。
/// 负责设备列表展示、新设备添加、连接/断开操作，以及状态监听的生命周期。
/// </summary>
public sealed class DevicePageViewModel : ViewModelBase {

    // -------------------------------------------------------------------------
    // 私有字段
    // -------------------------------------------------------------------------

    /// <summary>设备管理服务，封装 gRPC 路由管理操作。</summary>
    private readonly IDeviceService _devices;

    /// <summary>应用日志记录器，可为 null（此时不记录日志）。</summary>
    private readonly IAppLogger _log;

    private bool _isSelectMode;

    private int _deviceCount;

    // -------------------------------------------------------------------------
    // 公开属性
    // -------------------------------------------------------------------------

    /// <summary>
    /// 展示列表，包含 DeviceInfo 条目和末尾的 AddDeviceMarker 占位符。
    /// DevicePage.xaml 的 ItemsControl 绑定此列表，DataTemplate 区分两种类型。
    /// </summary>
    public ObservableCollection<object> DisplayList { get; } =
        new ObservableCollection<object>();

    /// <summary>是否处于多选模式（批量删除）。</summary>
    public bool IsSelectMode {
        get => _isSelectMode;
        set => SetField(ref _isSelectMode, value);
    }

    /// <summary>当前设备总数，绑定工具栏计数显示。</summary>
    public int DeviceCount {
        get => _deviceCount;
        private set => SetField(ref _deviceCount, value);
    }

    // -------------------------------------------------------------------------
    // 命令（工具栏 / 多选确认）
    // -------------------------------------------------------------------------

    /// <summary>全部连接命令。</summary>
    public ICommand ConnectAllCommand { get; }

    /// <summary>全部断开命令。</summary>
    public ICommand DisconnectAllCommand { get; }

    /// <summary>刷新列表命令，触发 IDeviceService.Load()。</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>进入多选模式命令。</summary>
    public ICommand EnterSelectModeCommand { get; }

    /// <summary>确认批量删除命令，参数为选中 ID 集合。</summary>
    public ICommand ConfirmDeleteCommand { get; }

    /// <summary>取消多选模式命令。</summary>
    public ICommand CancelSelectCommand { get; }

    // -------------------------------------------------------------------------
    // 委托（DeviceCard 注入后调用，替代直接持有 IDeviceService）
    // -------------------------------------------------------------------------

    /// <summary>
    /// 连接单台设备。DevicePage 将此委托注入每个 DeviceCard。
    /// Card 触发后调用 VM 执行，VM 调用 IDeviceService——View 不感知服务接口。
    /// </summary>
    public Func<string, CancellationToken, Task> ConnectDevice { get; }

    /// <summary>断开单台设备。同上。</summary>
    public Action<string> DisconnectDevice { get; }

    // -------------------------------------------------------------------------
    // 事件（VM → View，驱动面板显示/隐藏）
    // -------------------------------------------------------------------------

    /// <summary>请求打开新增面板。</summary>
    public event Action RequestOpenAdd;

    /// <summary>请求打开编辑面板并传入当前设备信息。</summary>
    public event Action<DeviceInfo> RequestOpenEdit;

    /// <summary>请求显示错误消息对话框。</summary>
    public event Action<string> RequestShowError;

    // -------------------------------------------------------------------------
    // 构造函数
    // -------------------------------------------------------------------------

    /// <param name="devices">设备管理服务（必须非 null）。</param>
    /// <param name="logger">可选日志记录器，为 null 时不记录日志。</param>
    public DevicePageViewModel(IDeviceService devices, IAppLogger logger = null) {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _log     = logger;

        // ── 命令绑定 ──────────────────────────────────
        // ConnectAllCommand：async 委托通过 RelayCommand(Action) 承载（Task 被忽略属预期行为）
        ConnectAllCommand    = new RelayCommand(async () => await ConnectAllAsync());
        DisconnectAllCommand = new RelayCommand(DisconnectAll);
        RefreshCommand       = new RelayCommand(Refresh);

        // 进入/退出多选模式
        EnterSelectModeCommand = new RelayCommand(() => IsSelectMode = true);
        CancelSelectCommand    = new RelayCommand(() => IsSelectMode = false);

        // 批量删除：参数为 IEnumerable<string>（选中 ID 集合）
        ConfirmDeleteCommand = new RelayCommand<IEnumerable<string>>(ConfirmDelete);

        // ── 委托绑定（封装服务调用，Card 只拿委托不拿服务接口）──
        ConnectDevice    = (id, ct) => ConnectDeviceAsync(id, ct);
        DisconnectDevice = id => _devices.Disconnect(id);

        // ── 订阅设备列表变更，同步 DisplayList ──────────
        _devices.Devices.CollectionChanged += (_, __) => RebuildDisplayList();

        // ── 订阅后台操作失败，转为界面错误提示 ──────────
        // Add / Update 是即发即忘的，失败只能通过此事件让用户看到原因
        _devices.OperationFailed += msg => {
            _log?.Error("Device", msg);
            RequestShowError?.Invoke(msg);
        };

        RebuildDisplayList();
    }

    // -------------------------------------------------------------------------
    // 公开操作（View 通过事件或直接调用）
    // -------------------------------------------------------------------------

    /// <summary>触发"打开新增面板"事件。</summary>
    public void OpenAdd() => RequestOpenAdd?.Invoke();

    /// <summary>触发"打开编辑面板"事件，并传入目标设备信息。</summary>
    public void OpenEdit(DeviceInfo info) {
        if (info != null)
            RequestOpenEdit?.Invoke(info);
    }

    /// <summary>
    /// 保存设备（新增或更新）。
    /// 校验失败时触发 <see cref="RequestShowError"/>，不抛出异常。
    /// </summary>
    /// <param name="info">待保存的设备信息。</param>
    /// <param name="isNew">true = 新增；false = 更新。</param>
    public void SaveDevice(DeviceInfo info, bool isNew) {
        if (info == null) return;

        // 基础校验：名称和协议不能为空
        if (string.IsNullOrWhiteSpace(info.Name) || string.IsNullOrWhiteSpace(info.Protocol)) {
            RequestShowError?.Invoke("名称和协议不能为空");
            return;
        }

        try {
            // 根据操作类型选择添加或更新
            if (isNew) _devices.Add(info);
            else       _devices.Update(info);
            _log?.Info("Device", (isNew ? "新增" : "更新") + "设备: " + info.Name);
        } catch (Exception ex) {
            // 捕获服务层异常，通过事件通知 View 显示错误框
            RequestShowError?.Invoke(ex.Message);
            _log?.Error("Device", "保存设备失败", ex);
        }
    }

    /// <summary>删除指定 ID 的设备。</summary>
    public void RemoveDevice(string id) {
        if (string.IsNullOrEmpty(id)) return;
        try {
            _devices.Remove(id);
            _log?.Info("Device", "删除设备: " + id);
        } catch (Exception ex) {
            RequestShowError?.Invoke(ex.Message);
            _log?.Error("Device", "删除设备失败", ex);
        }
    }

    // -------------------------------------------------------------------------
    // 内部操作
    // -------------------------------------------------------------------------

    /// <summary>连接所有未连接设备（串行）。</summary>
    private async Task ConnectAllAsync() {
        // 快照未连接设备列表，避免迭代中集合变化
        List<DeviceInfo> list = _devices.Devices
            .Where(d => d != null && !d.IsConnected)
            .ToList();

        // 先将所有目标设备标记为"连接中"
        foreach (DeviceInfo d in list) {
            d.StatusType  = DeviceStatusType.Connecting;
            d.IsConnected = false;
        }

        // 依次连接；单台失败不影响其他设备
        foreach (DeviceInfo d in list) {
            try {
                await _devices.ConnectAsync(d.Id, CancellationToken.None);
            } catch (Exception ex) {
                d.IsConnected = false;
                d.StatusType  = DeviceStatusType.Error;
                _log?.Error("Device", "连接失败: " + d.Name, ex);
            }
        }
    }

    /// <summary>连接单台设备（供 DeviceCard 委托调用）。</summary>
    private async Task ConnectDeviceAsync(string id, CancellationToken ct) {
        if (string.IsNullOrEmpty(id)) return;
        try {
            await _devices.ConnectAsync(id, ct);
        } catch (Exception ex) {
            _log?.Error("Device", "单台连接失败: " + id + " — " + ex.Message, ex);
        }
    }

    /// <summary>断开所有已连接设备。</summary>
    private void DisconnectAll() {
        foreach (DeviceInfo d in _devices.Devices.ToList()) {
            if (d == null || string.IsNullOrEmpty(d.Id)) continue;
            _devices.Disconnect(d.Id);
        }
    }

    /// <summary>从服务层重新拉取设备列表并重建 DisplayList。</summary>
    private void Refresh() {
        _devices.Load();
        RebuildDisplayList();
        _log?.Info("Device", "已刷新设备列表");
    }

    /// <summary>批量删除选中设备，失败时汇总错误并上报。</summary>
    private void ConfirmDelete(IEnumerable<string> ids) {
        if (ids == null) return;

        var errors = new StringBuilder();
        foreach (string id in ids.ToList()) {
            try {
                _devices.Remove(id);
            } catch (Exception ex) {
                _log?.Error("Device", "批量删除失败: " + id + " — " + ex.Message, ex);
                errors.AppendLine(ex.Message);
            }
        }

        // 退出多选模式
        IsSelectMode = false;

        // 有失败条目时弹出错误摘要
        if (errors.Length > 0)
            RequestShowError?.Invoke("部分设备删除失败：\n" + errors.ToString().TrimEnd());
    }

    /// <summary>
    /// 重建 DisplayList：设备条目 + 末尾添加标记占位符。
    /// 每次设备集合变化时调用，使 UI 自动感知增删。
    /// </summary>
    private void RebuildDisplayList() {
        DisplayList.Clear();
        foreach (DeviceInfo d in _devices.Devices)
            DisplayList.Add(d);

        // 末尾添加"添加设备"占位符卡片
        DisplayList.Add(AddDeviceMarker.Instance);
        DeviceCount = _devices.Devices.Count;
    }
}
