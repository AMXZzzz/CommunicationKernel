#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Models;
using CommunicationKernel.UI.Wpf.ViewModels;
using CommunicationKernel.UI.Wpf.Views.Controls;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Device {

    /// <summary>
    /// 设备管理页：UI 路由；业务在 <see cref="DevicePageViewModel"/>。
    /// DataTemplate 生成的 DeviceCard 在可视树就绪后注入 DeviceService。
    /// 提示使用 <see cref="AppMessageDialog"/>，与变量配置页一致。
    /// </summary>
    public partial class DevicePage : Page {

        private readonly DevicePageViewModel _vm;
        private readonly IProtocolResolver _protocols;

        /// <summary>是否已订阅单例 ViewModel 的事件，保证订阅/退订幂等。</summary>
        private bool _vmWired;

        public DevicePage (DevicePageViewModel vm, IProtocolResolver protocols) {
            if (vm == null)
                throw new ArgumentNullException(nameof(vm));

            _vm = vm;
            _protocols = protocols;
            _vmWired = false;

            InitializeComponent();

            DataContext = _vm;
            deviceList.ItemsSource = _vm.DisplayList;

            WireViewModel();
            WireToolbar();
            WireEditPanel();
            WireMessageDialog();

            if (toolBar != null)
                toolBar.SetCount(_vm.DeviceCount);

            // Loaded / Unloaded 成对管理单例 ViewModel 的订阅：
            // 页面重新进入可视树时恢复订阅，离开时立即退订，杜绝重复处理器累积。
            Loaded   += DevicePage_Loaded;
            Unloaded += DevicePage_Unloaded;
            editPanel.ProtocolResolver = _protocols;
        }

        private void DisplayList_CollectionChanged (object sender, NotifyCollectionChangedEventArgs e) {
            Dispatcher.BeginInvoke(
                new Action(InjectServicesToCards),
                DispatcherPriority.Loaded);
        }

        /// <summary>给当前可视树中的 DeviceCard 注入服务并同步多选。</summary>
        private void InjectServicesToCards () {
            if (deviceList == null || _vm == null)
                return;

            foreach (DeviceCard card in FindVisualChildren<DeviceCard>(deviceList)) {
                card.ConnectDevice = _vm.ConnectDevice;
                card.DisconnectDevice = _vm.DisconnectDevice;
                card.SetSelectionMode(_vm.IsSelectMode);
            }
        }

        /// <summary>
        /// 订阅 ViewModel 事件。幂等：重复调用不会造成重复订阅。
        /// </summary>
        /// <remarks>
        /// ViewModel 是单例而本页是瞬态（每次导航新建实例）。
        /// 若只订阅不退订，来回切页 N 次后单例会持有 N 个页面实例，
        /// 一次 RequestShowError 会弹出 N 个对话框——因此必须使用具名方法
        /// 配合 <see cref="UnwireViewModel"/> 成对使用，匿名 lambda 无法退订。
        /// </remarks>
        private void WireViewModel () {
            if (_vmWired) return;
            _vmWired = true;

            _vm.RequestOpenAdd   += OnRequestOpenAdd;
            _vm.RequestOpenEdit  += OnRequestOpenEdit;
            _vm.RequestShowError += OnRequestShowError;
            _vm.PropertyChanged  += OnViewModelPropertyChanged;
            _vm.DisplayList.CollectionChanged += DisplayList_CollectionChanged;
        }

        /// <summary>退订 ViewModel 事件。幂等：未订阅时调用无副作用。</summary>
        private void UnwireViewModel () {
            if (!_vmWired) return;
            _vmWired = false;

            _vm.RequestOpenAdd   -= OnRequestOpenAdd;
            _vm.RequestOpenEdit  -= OnRequestOpenEdit;
            _vm.RequestShowError -= OnRequestShowError;
            _vm.PropertyChanged  -= OnViewModelPropertyChanged;
            _vm.DisplayList.CollectionChanged -= DisplayList_CollectionChanged;
        }

        private void OnRequestOpenAdd () => ShowEditPanel(true, null);

        private void OnRequestOpenEdit (DeviceInfo info) => ShowEditPanel(false, info);

        private void OnRequestShowError (string msg) => ShowWarning("提示", msg ?? "");

        private void OnViewModelPropertyChanged (object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName == nameof(DevicePageViewModel.DeviceCount) && toolBar != null)
                toolBar.SetCount(_vm.DeviceCount);

            if (e.PropertyName == nameof(DevicePageViewModel.IsSelectMode) && toolBar != null) {
                toolBar.SetSelectMode(_vm.IsSelectMode);
                ApplySelectModeToCards(_vm.IsSelectMode);
            }
        }

        private void WireToolbar () {
            if (toolBar == null) return;

            toolBar.ConnectAllClicked += () => {
                if (_vm.ConnectAllCommand.CanExecute(null))
                    _vm.ConnectAllCommand.Execute(null);
            };
            toolBar.DisconnectAllClicked += () => {
                if (_vm.DisconnectAllCommand.CanExecute(null))
                    _vm.DisconnectAllCommand.Execute(null);
            };
            toolBar.RefreshClicked += () => {
                if (_vm.RefreshCommand.CanExecute(null))
                    _vm.RefreshCommand.Execute(null);
            };
            toolBar.DeleteClicked += () => {
                if (_vm.EnterSelectModeCommand.CanExecute(null))
                    _vm.EnterSelectModeCommand.Execute(null);
                ApplySelectModeToCards(true);
            };
            toolBar.ConfirmDeleteClicked += () => {
                var ids = CollectSelectedIds();
                if (_vm.ConfirmDeleteCommand.CanExecute(ids))
                    _vm.ConfirmDeleteCommand.Execute(ids);
                ApplySelectModeToCards(false);
            };
            toolBar.CancelSelectClicked += () => {
                if (_vm.CancelSelectCommand.CanExecute(null))
                    _vm.CancelSelectCommand.Execute(null);
                ApplySelectModeToCards(false);
            };
        }

        private void WireEditPanel () {
            if (editPanel == null) return;

            editPanel.ProtocolResolver = _protocols;

            editPanel.CloseRequested += CloseEditPanel;
            editPanel.SaveRequested += () => {
                DeviceInfo info = editPanel.BuildDeviceInfo();
                _vm.SaveDevice(info, editPanel.IsNew);
                CloseEditPanel();
            };
            editPanel.DeleteRequested += () => {
                if (!editPanel.IsNew) {
                    DeviceInfo info = editPanel.BuildDeviceInfo();
                    if (info != null && !string.IsNullOrEmpty(info.Id))
                        _vm.RemoveDevice(info.Id);
                }
                CloseEditPanel();
            };
        }

        private void WireMessageDialog () {
            if (msgDialog == null) return;
            msgDialog.CloseRequested += CloseMessageDialog;
            msgDialog.PrimaryRequested += CloseMessageDialog;
            msgDialog.SecondaryRequested += CloseMessageDialog;
        }

        // ── 编辑面板 ──────────────────────────────────

        public void OpenAddDevice () => _vm.OpenAdd();

        public void OpenEditDevice (DeviceInfo info) => _vm.OpenEdit(info);

        private void ShowEditPanel (bool isNew, DeviceInfo info) {
            if (editPanel == null || editOverlay == null) return;

            HideMessageDialogOnly();

            if (isNew)
                editPanel.LoadData(new DeviceInfo(), true);
            else
                editPanel.LoadData(info, false);

            editPanel.Visibility = Visibility.Visible;
            editOverlay.Visibility = Visibility.Visible;
        }

        private void CloseEditPanel () {
            if (editPanel != null)
                editPanel.Visibility = Visibility.Collapsed;
            HideOverlayIfIdle();
        }

        // ── 主题消息框────────────────

        private void ShowWarning (string title, string message) {
            ShowMessage(AppMessageKind.Warning, title, message);
        }

        private void ShowInfo (string title, string message) {
            ShowMessage(AppMessageKind.Info, title, message);
        }

        private void ShowMessage (AppMessageKind kind, string title, string message) {
            if (msgDialog == null || editOverlay == null) return;

            // 与变量页一致：先收起其它面板，再显示消息
            if (editPanel != null)
                editPanel.Visibility = Visibility.Collapsed;

            msgDialog.Setup(
                kind,
                title,
                message,
                detail: null,
                primaryText: "确定",
                secondaryText: null,
                showSecondary: false);

            msgDialog.Visibility = Visibility.Visible;
            editOverlay.Visibility = Visibility.Visible;
        }

        private void CloseMessageDialog () {
            HideMessageDialogOnly();
            HideOverlayIfIdle();
        }

        private void HideMessageDialogOnly () {
            if (msgDialog != null)
                msgDialog.Visibility = Visibility.Collapsed;
        }

        private void HideOverlayIfIdle () {
            bool busy =
                (editPanel != null && editPanel.Visibility == Visibility.Visible) ||
                (msgDialog != null && msgDialog.Visibility == Visibility.Visible);

            if (!busy && editOverlay != null)
                editOverlay.Visibility = Visibility.Collapsed;
        }

        private void EditOverlay_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) {
            if (editPanel != null)
                editPanel.Visibility = Visibility.Collapsed;
            HideMessageDialogOnly();
            if (editOverlay != null)
                editOverlay.Visibility = Visibility.Collapsed;
        }

        private void Panel_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) {
            e.Handled = true; // 点击面板本身不关闭遮罩
        }

        // ── 多选 ──────────────────────────────────────

        private void ApplySelectModeToCards (bool selectMode) {
            foreach (DeviceCard card in FindVisualChildren<DeviceCard>(deviceList)) {
                card.ConnectDevice = _vm.ConnectDevice;
                card.DisconnectDevice = _vm.DisconnectDevice;
                card.SetSelectionMode(selectMode);
            }
        }

        private List<string> CollectSelectedIds () {
            var ids = new List<string>();
            foreach (DeviceCard card in FindVisualChildren<DeviceCard>(deviceList)) {
                if (card.IsSelected && card.Device != null && !string.IsNullOrEmpty(card.Device.Id))
                    ids.Add(card.Device.Id);
            }
            return ids;
        }

        private void DevicePage_Loaded (object sender, RoutedEventArgs e) {
            // 恢复订阅（首次为初次订阅），再刷新卡片上的服务注入
            WireViewModel();
            InjectServicesToCards();
        }

        private void DevicePage_Unloaded (object sender, RoutedEventArgs e) {
            // 离开可视树立即退订，防止单例 ViewModel 持有已废弃的页面实例
            UnwireViewModel();
        }

        private static IEnumerable<T> FindVisualChildren<T> (DependencyObject parent)
            where T : DependencyObject {
            if (parent == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++) {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                T match = child as T;
                if (match != null)
                    yield return match;
                foreach (T nested in FindVisualChildren<T>(child))
                    yield return nested;
            }
        }
    }
}