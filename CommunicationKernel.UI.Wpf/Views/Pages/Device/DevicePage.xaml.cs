#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Device/DevicePage.xaml.cs
// 层级: UI 层 — WPF Views
// 作用: 设备管理页 UI 路由；业务在 DevicePageViewModel，本页只做弹层与卡片注入。
// -----------------------------------------------------------------------------

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

        /// <summary>页面 ViewModel，单例——切页不重建，订阅必须成对进出。</summary>
        private readonly DevicePageViewModel _vm;
        /// <summary>协议清单来源；一律取自宿主已加载的插件，UI 不内置协议名。</summary>
        private readonly IProtocolResolver _protocols;

        /// <summary>串口清单提供者；清单来自宿主机器而非本机。</summary>
        private readonly ISerialPortProvider _serialPorts;

        /// <summary>是否已订阅单例 ViewModel 的事件，保证订阅/退订幂等。</summary>
        private bool _vmWired;

        /// <param name="vm">页面 ViewModel，必填。</param>
        /// <param name="protocols">协议清单解析器。</param>
        /// <param name="serialPorts">
        /// 串口清单提供者，可为 null（此时编辑面板退化为手工输入串口名）。
        /// </param>
        public DevicePage (DevicePageViewModel vm, IProtocolResolver protocols, ISerialPortProvider serialPorts = null) {
            _serialPorts = serialPorts;
            if (vm == null)
                throw new ArgumentNullException(nameof(vm));

            _vm = vm;
            _protocols = protocols;
            _vmWired = false;

            // 解析 XAML 并把 DisplayList 绑到卡片列表
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

            // 面板依赖的注入统一在 WireEditPanel 内完成（上面已调用），
            // 此处不再重复赋值。
        }

        // ============================================================================
        // 卡片注入
        // ============================================================================

        /// <summary>
        /// 设备列表变化后重新给卡片注入服务。
        /// </summary>
        /// <remarks>
        /// 延到 <see cref="DispatcherPriority.Loaded"/> 再做：集合变更事件触发时，
        /// ItemsControl 还没有为新项生成容器，此刻遍历可视树找不到那张新卡片，
        /// 它就会一直缺服务——表现为新加的设备点连接没反应。
        /// </remarks>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
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

        // ============================================================================
        // ViewModel 订阅（单例必须成对退订）
        // ============================================================================

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

        /// <summary>ViewModel 请求打开新增面板。</summary>
        private void OnRequestOpenAdd () => ShowEditPanel(true, null);

        /// <summary>ViewModel 请求打开编辑面板并回填。</summary>
        private void OnRequestOpenEdit (DeviceInfo info) => ShowEditPanel(false, info);

        private void OnRequestShowError (string msg) => ShowWarning("提示", msg ?? "");

        /// <summary>响应 ViewModel 的属性变化，目前用于同步卡片的多选模式。</summary>
        private void OnViewModelPropertyChanged (object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName == nameof(DevicePageViewModel.DeviceCount) && toolBar != null)
                toolBar.SetCount(_vm.DeviceCount);

            if (e.PropertyName == nameof(DevicePageViewModel.IsSelectMode) && toolBar != null) {
                toolBar.SetSelectMode(_vm.IsSelectMode);
                ApplySelectModeToCards(_vm.IsSelectMode);
            }
        }

        // ============================================================================
        // 工具栏 / 编辑面板 / 消息框
        // ============================================================================

        /// <summary>把工具栏按钮接到 ViewModel 命令上。</summary>
        /// <remarks>
        /// 每个回调都先问 <c>CanExecute</c>：工具栏按钮没有绑定命令的禁用态，
        /// 不问就会在引擎未连接时也把命令发出去。
        /// </remarks>
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

        /// <summary>订阅编辑面板的保存/删除/关闭事件。面板与本页同生共死，无需退订。</summary>
        private void WireEditPanel () {
            if (editPanel == null) return;

            editPanel.ProtocolResolver  = _protocols;
            editPanel.SerialPortProvider = _serialPorts;

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

        /// <summary>订阅通用消息框的按钮事件。</summary>
        private void WireMessageDialog () {
            if (msgDialog == null) return;
            msgDialog.CloseRequested += CloseMessageDialog;
            msgDialog.PrimaryRequested += CloseMessageDialog;
            msgDialog.SecondaryRequested += CloseMessageDialog;
        }

        // ============================================================================
        // 编辑面板
        // ============================================================================

        /// <summary>供外部（如「添加设备」占位卡片）请求打开新增面板。</summary>
        public void OpenAddDevice () => _vm.OpenAdd();

        /// <summary>供外部（卡片双击等）请求编辑某台设备。转交 ViewModel 决定是否放行。</summary>
        public void OpenEditDevice (DeviceInfo info) => _vm.OpenEdit(info);

        /// <summary>展开编辑面板。</summary>
        /// <param name="isNew">true 为新增，false 为编辑。</param>
        /// <param name="info">编辑模式下用于回填的设备；新增时为 null。</param>
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

        /// <summary>收起编辑面板，并在没有其它弹层时一并收起遮罩。</summary>
        private void CloseEditPanel () {
            if (editPanel != null)
                editPanel.Visibility = Visibility.Collapsed;
            HideOverlayIfIdle();
        }

        // ============================================================================
        // 主题消息框
        // ============================================================================

        /// <summary>弹出警告消息框。</summary>
        /// <param name="title">标题。</param>
        /// <param name="message">正文。</param>
        private void ShowWarning (string title, string message) {
            ShowMessage(AppMessageKind.Warning, title, message);
        }

        /// <summary>弹出通用消息框。</summary>
        /// <param name="kind">消息种类，决定配色与图标。</param>
        /// <param name="title">标题。</param>
        /// <param name="message">正文。</param>
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

        /// <summary>关闭消息框并收起遮罩。</summary>
        private void CloseMessageDialog () {
            HideMessageDialogOnly();
            HideOverlayIfIdle();
        }

        /// <summary>只收起消息框，保留遮罩——编辑面板可能还开着。</summary>
        private void HideMessageDialogOnly () {
            if (msgDialog != null)
                msgDialog.Visibility = Visibility.Collapsed;
        }

        /// <summary>仅当编辑面板与消息框都已收起时才隐藏遮罩。</summary>
        /// <remarks>无条件隐藏会在两个弹层交替时闪一下露出底下的卡片列表。</remarks>
        private void HideOverlayIfIdle () {
            bool busy =
                (editPanel != null && editPanel.Visibility == Visibility.Visible) ||
                (msgDialog != null && msgDialog.Visibility == Visibility.Visible);

            if (!busy && editOverlay != null)
                editOverlay.Visibility = Visibility.Collapsed;
        }

        /// <summary>点击遮罩空白处：收起编辑面板与消息框。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void EditOverlay_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) {
            if (editPanel != null)
                editPanel.Visibility = Visibility.Collapsed;
            HideMessageDialogOnly();
            if (editOverlay != null)
                editOverlay.Visibility = Visibility.Collapsed;
        }

        /// <summary>吞掉面板内的点击，避免冒泡到遮罩把面板关掉。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void Panel_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) {
            e.Handled = true; // 点击面板本身不关闭遮罩
        }

        // ============================================================================
        // 多选
        // ============================================================================

        /// <summary>把多选模式同步到所有卡片，顺便补一次服务注入。</summary>
        /// <param name="selectMode">true 进入多选模式（卡片显示勾选框）。</param>
        private void ApplySelectModeToCards (bool selectMode) {
            foreach (DeviceCard card in FindVisualChildren<DeviceCard>(deviceList)) {
                card.ConnectDevice = _vm.ConnectDevice;
                card.DisconnectDevice = _vm.DisconnectDevice;
                card.SetSelectionMode(selectMode);
            }
        }

        /// <summary>收集当前被勾选的设备 Id。</summary>
        /// <returns>设备 Id 列表；没有勾选时返回空列表，不返回 null。</returns>
        private List<string> CollectSelectedIds () {
            var ids = new List<string>();
            foreach (DeviceCard card in FindVisualChildren<DeviceCard>(deviceList)) {
                if (card.IsSelected && card.Device != null && !string.IsNullOrEmpty(card.Device.Id))
                    ids.Add(card.Device.Id);
            }
            return ids;
        }

        /// <summary>进入可视树：订阅 ViewModel 并补一次卡片服务注入。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void DevicePage_Loaded (object sender, RoutedEventArgs e) {
            // 恢复订阅（首次为初次订阅），再刷新卡片上的服务注入
            WireViewModel();
            InjectServicesToCards();
        }

        /// <summary>
        /// 离开可视树：立即退订。
        /// </summary>
        /// <remarks>
        /// ViewModel 是单例，页面每次导航都会重建。不退订的话，
        /// 单例会攒下一串指向已废弃页面的事件订阅——既泄漏，
        /// 又会让一次数据变更被多个死页面各响应一遍。
        /// </remarks>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void DevicePage_Unloaded (object sender, RoutedEventArgs e) {
            // 离开可视树立即退订，防止单例 ViewModel 持有已废弃的页面实例
            UnwireViewModel();
        }

        /// <summary>深度优先遍历可视树，找出全部指定类型的后代。</summary>
        /// <typeparam name="T">目标类型。</typeparam>
        /// <param name="parent">起点；为 null 时返回空序列。</param>
        /// <returns>惰性序列，按可视树顺序产出。</returns>
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
