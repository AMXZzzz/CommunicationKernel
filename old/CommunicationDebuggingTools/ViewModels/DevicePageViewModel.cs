using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Logging;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Views.Pages.Device;

namespace CommunicationDebuggingTools.ViewModels {

    /// <summary>
    /// 设备管理页 ViewModel。
    ///
    /// 封装原则：
    ///   View 通过命令和委托与 VM 交互，不持有 IDeviceService 引用。
    ///   DeviceCard 通过 ConnectDevice / DisconnectDevice 委托触发连接，
    ///   VM 负责调用服务；View 只关心 UI 状态。
    /// </summary>
    public sealed class DevicePageViewModel : ViewModelBase {

        private readonly IDeviceService _devices;
        private readonly IAppLogger     _log;

        // ── 展示 ──────────────────────────────────────
        public ObservableCollection<object> DisplayList { get; } =
            new ObservableCollection<object>();

        private bool _isSelectMode;
        public bool IsSelectMode {
            get => _isSelectMode;
            set => SetField(ref _isSelectMode, value);
        }

        private int _deviceCount;
        public int DeviceCount {
            get => _deviceCount;
            private set => SetField(ref _deviceCount, value);
        }

        // ── 命令（工具栏 / 多选确认）─────────────────────
        public ICommand ConnectAllCommand { get; }
        public ICommand DisconnectAllCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand EnterSelectModeCommand { get; }
        public ICommand ConfirmDeleteCommand { get; }
        public ICommand CancelSelectCommand { get; }

        // ── 委托（DeviceCard 注入后调用，替代直接持有 IDeviceService）─
        /// <summary>
        /// 连接单台设备。DevicePage 将此委托注入每个 DeviceCard。
        /// Card 触发后调用 VM 执行，VM 调用 IDeviceService——View 不感知服务接口。
        /// </summary>
        public Func<string, CancellationToken, Task> ConnectDevice { get; }

        /// <summary>断开单台设备。同上。</summary>
        public Action<string> DisconnectDevice { get; }

        // ── 事件（VM → View，驱动面板显示/隐藏）────────
        public event Action             RequestOpenAdd;
        public event Action<DeviceInfo> RequestOpenEdit;
        public event Action<string>     RequestShowError;

        // ── 构造 ──────────────────────────────────────
        public DevicePageViewModel (IDeviceService devices, IAppLogger logger = null) {
            _devices = devices ?? throw new ArgumentNullException(nameof(devices));
            _log = logger;

            // 命令
            ConnectAllCommand = new RelayCommand(async () => await ConnectAllAsync());
            DisconnectAllCommand = new RelayCommand(DisconnectAll);
            RefreshCommand = new RelayCommand(Refresh);
            EnterSelectModeCommand = new RelayCommand(() => IsSelectMode = true);
            ConfirmDeleteCommand = new RelayCommand<IEnumerable<string>>(ConfirmDelete);
            CancelSelectCommand = new RelayCommand(() => IsSelectMode = false);

            // 委托——封装服务调用，Card 只拿委托不拿服务接口
            ConnectDevice = (id, ct) => ConnectDeviceAsync(id, ct);
            DisconnectDevice = id => _devices.Disconnect(id);

            _devices.Devices.CollectionChanged += (_, __) => RebuildDisplayList();
            RebuildDisplayList();
        }

        // ── 公开操作（View 通过事件或直接调用）──────────
        public void OpenAdd () => RequestOpenAdd?.Invoke();

        public void OpenEdit (DeviceInfo info) {
            if (info != null) RequestOpenEdit?.Invoke(info);
        }

        public void SaveDevice (DeviceInfo info, bool isNew) {
            if (info == null) return;
            if (string.IsNullOrWhiteSpace(info.Name) || string.IsNullOrWhiteSpace(info.Protocol)) {
                RequestShowError?.Invoke("名称和协议不能为空");
                return;
            }
            try {
                if (isNew) _devices.Add(info);
                else _devices.Update(info);
                _log?.Info("Device", (isNew ? "新增" : "更新") + "设备: " + info.Name);
            } catch (Exception ex) {
                RequestShowError?.Invoke(ex.Message);
                _log?.Error("Device", "保存设备失败", ex);
            }
        }

        public void RemoveDevice (string id) {
            if (string.IsNullOrEmpty(id)) return;
            try {
                _devices.Remove(id);
                _log?.Info("Device", "删除设备: " + id);
            } catch (Exception ex) {
                RequestShowError?.Invoke(ex.Message);
                _log?.Error("Device", "删除设备失败", ex);
            }
        }

        // ── 内部操作 ──────────────────────────────────
        private async Task ConnectAllAsync () {
            var list = _devices.Devices
                .Where(d => d != null && !d.IsConnected)
                .ToList();
            foreach (DeviceInfo d in list) {
                d.StatusType = DeviceStatusType.Connecting;
                d.IsConnected = false;
            }
            foreach (DeviceInfo d in list) {
                try {
                    await _devices.ConnectAsync(d.Id, CancellationToken.None);
                } catch (Exception ex) {
                    d.IsConnected = false;
                    d.StatusType = DeviceStatusType.Error;
                    _log?.Error("Device", "连接失败: " + d.Name, ex);
                }
            }
        }

        private async Task ConnectDeviceAsync (string id, CancellationToken ct) {
            if (string.IsNullOrEmpty(id)) return;
            try {
                await _devices.ConnectAsync(id, ct);
            } catch (Exception ex) {
                _log?.Error("Device", "单台连接失败: " + id + " — " + ex.Message, ex);
            }
        }

        private void DisconnectAll () {
            foreach (DeviceInfo d in _devices.Devices.ToList()) {
                if (d == null || string.IsNullOrEmpty(d.Id)) continue;
                _devices.Disconnect(d.Id);
            }
        }

        private void Refresh () {
            _devices.Load();
            RebuildDisplayList();
            _log?.Info("Device", "已刷新设备列表");
        }

        /// <summary>问题 11 修复：批量删除失败时上报错误，不再吞异常。</summary>
        private void ConfirmDelete (IEnumerable<string> ids) {
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
            IsSelectMode = false;
            if (errors.Length > 0)
                RequestShowError?.Invoke("部分设备删除失败：\n" + errors.ToString().TrimEnd());
        }

        private void RebuildDisplayList () {
            DisplayList.Clear();
            foreach (DeviceInfo d in _devices.Devices)
                DisplayList.Add(d);
            DisplayList.Add(AddDeviceMarker.Instance);
            DeviceCount = _devices.Devices.Count;
        }
    }
}