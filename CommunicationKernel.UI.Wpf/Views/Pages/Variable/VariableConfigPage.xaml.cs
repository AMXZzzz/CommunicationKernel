#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Variable/VariableConfigPage.xaml.cs
// 层级: UI 层 — WPF Views
// 作用: 变量配置页；只负责弹层 Visibility、子控件注入与事件路由，CRUD 在 ViewModel。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunicationKernel.UI.Wpf.Core.Models;
using CommunicationKernel.UI.Wpf.ViewModels;
using CommunicationKernel.UI.Wpf.Views.Controls;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Variable {
    /// <summary>
    /// 变量配置页：只负责弹层 Visibility、子控件服务注入与事件路由。
    /// 业务（CRUD / 写入）全部在 <see cref="VariablePageViewModel"/>。
    /// </summary>
    public partial class VariableConfigPage : Page {
        /// <summary>
        /// 消息框的待办动作。
        /// </summary>
        /// <remarks>
        /// 主题消息框是异步的（点确定才回调），确认后要做什么必须先记下来。
        /// 只有一个待办槽位——同时只允许一个弹层，不会有第二件事在排队。
        /// </remarks>
        private enum MsgPending {

            /// <summary>无待办，点确定只是关掉。</summary>
            None,

            /// <summary>确认后清空当前设备的变量再导入。</summary>
            ImportClear
        }

        /// <summary>页面 ViewModel，单例——切页不重建，因此订阅必须成对退订。</summary>
        private readonly VariablePageViewModel _vm;
        /// <summary>最近一次导出的文件路径，供成功提示里的「打开目录」使用。</summary>
        private string _lastExportPath;
        /// <summary>
        /// 通用消息框当前代表哪一个待决操作。
        /// </summary>
        /// <remarks>
        /// 同一个消息框被导入确认、导出成功等多处复用，
        /// 按钮回调必须靠本字段判断"这次点的确定是要干什么"，
        /// 否则点确定会执行上一次遗留的动作。关闭弹层时务必清回 None。
        /// </remarks>
        private MsgPending _msgPending;

        /// <summary>是否已订阅单例 ViewModel 的事件，保证订阅/退订幂等。</summary>
        private bool _vmWired;

        /// <param name="viewModel">页面 ViewModel，由 DI 注入。</param>
        /// <remarks>
        /// 子控件拿不到 DI 容器，服务由本页统一注入下去（<c>InjectServicesIntoControls</c>）。
        /// 事件订阅分两处：<c>WireEvents</c> 挂子控件（生命周期与本页一致，构造时挂即可），
        /// <c>WireViewModel</c> 挂单例 ViewModel（必须随 Loaded/Unloaded 成对进出）。
        /// </remarks>
        public VariableConfigPage (VariablePageViewModel viewModel) {
            _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            // 解析 XAML 并把 ViewModel 挂到 DataContext
            InitializeComponent();
            DataContext = _vm;

            // 子控件拿不到 DI，由本页把服务注入进去
            InjectServicesIntoControls();
            WireViewModel();
            WireEvents();

            // Loaded / Unloaded 成对管理单例 ViewModel 的订阅
            Loaded   += VariableConfigPage_Loaded;
            Unloaded += VariableConfigPage_Unloaded;

            // 进入页面立即刷左侧设备列表
            deviceList.Reload();
        }

        /// <summary>XAML 子控件属性注入（服务来自 ViewModel）。</summary>
        private void InjectServicesIntoControls () {
            // 变量表需要 CRUD / 轮询开关
            if (variableTable != null)
                variableTable.VariableService = _vm.VariableService;

            // 左侧列表同时要设备清单和变量计数
            if (deviceList != null) {
                deviceList.DeviceService = _vm.DeviceService;
                deviceList.VariableService = _vm.VariableService;
            }

            if (deviceHeader != null)
                deviceHeader.DeviceService = _vm.DeviceService;

            if (exportPanel != null)
                exportPanel.VariableService = _vm.VariableService;

            // 导入要校验 DeviceId，设备服务一并注入
            if (importPanel != null) {
                importPanel.VariableService = _vm.VariableService;
                importPanel.DeviceService = _vm.DeviceService;
            }
        }

        /// <summary>
        /// 订阅单例 ViewModel 的事件。幂等：重复调用不会造成重复订阅。
        /// </summary>
        /// <remarks>
        /// ViewModel 是单例而本页是瞬态（每次导航新建实例）。
        /// 若只订阅不退订，来回切页 N 次后一次 RequestShowInfo 会弹出 N 个对话框，
        /// 且所有历史页面实例被单例永久持有。必须用具名方法成对订阅/退订。
        /// </remarks>
        private void WireViewModel () {
            // 已订阅则跳过，避免切页后重复挂接
            if (_vmWired) return;
            _vmWired = true;

            // 刷新表/列表 + 主题提示，都用具名方法以便 Unloaded 时退订
            _vm.RequestRefresh  += RefreshList;
            _vm.RequestShowInfo += OnRequestShowInfo;
        }

        /// <summary>退订单例 ViewModel 的事件。幂等：未订阅时调用无副作用。</summary>
        private void UnwireViewModel () {
            // 未订阅则跳过，保证幂等
            if (!_vmWired) return;
            _vmWired = false;

            // 成对退订，避免单例持有已卸载的页面实例
            _vm.RequestRefresh  -= RefreshList;
            _vm.RequestShowInfo -= OnRequestShowInfo;
        }

        /// <summary>ViewModel 请求弹提示时，走本页的主题对话框。</summary>
        /// <param name="title">标题。</param>
        /// <param name="msg">正文。</param>
        private void OnRequestShowInfo (string title, string msg) => ShowInfo(title, msg);

        /// <summary>进入可视树：恢复订阅（首次即初次订阅）。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void VariableConfigPage_Loaded (object sender, RoutedEventArgs e) => WireViewModel();

        /// <summary>离开可视树：立即退订。ViewModel 是单例，不退订会导致切页后同一提示弹多次。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void VariableConfigPage_Unloaded (object sender, RoutedEventArgs e) => UnwireViewModel();

        /// <summary>
        /// 订阅各子控件的事件。
        /// </summary>
        /// <remarks>
        /// 子控件与本页同生共死，构造时挂一次即可，不需要退订。
        /// 单例 ViewModel 的订阅不在这里——那部分见 <c>WireViewModel</c>。
        /// </remarks>
        private void WireEvents () {
            // 左侧选设备 → 刷标题栏和变量表
            deviceList.DeviceSelected += OnDeviceSelected;
            // 变量增删后刷新左侧计数
            variableTable.VariablesChanged += () => deviceList.Reload();
            variableTable.EditRequested += OpenEdit;
            variableTable.WriteRequested += OnVariableWriteRequested;

            deviceHeader.AddClicked += OpenAdd;
            deviceHeader.BatchAddClicked += OpenBatch;

            // 顶栏导入/导出打开对应弹层
            if (toolBar != null) {
                toolBar.ImportClicked += OpenImport;
                toolBar.ExportClicked += OpenExport;
            }

            if (editPanel != null) {
                editPanel.CloseRequested += CloseEdit;
                editPanel.SaveRequested += SaveEdit;
                editPanel.DeleteRequested += DeleteEdit;
                editPanel.InfoRequested += OnPanelInfo;
            }

            if (batchPanel != null) {
                batchPanel.CloseRequested += CloseBatch;
                batchPanel.BatchSaveRequested += SaveBatch;
                batchPanel.InfoRequested += OnPanelInfo;
            }

            // 导出/导入/消息框：先退订再订阅，避免设计器或重复进入时叠处理器
            if (exportPanel != null) {
                exportPanel.CloseRequested -= CloseExport;
                exportPanel.ExportSucceeded -= OnExportSucceeded;
                exportPanel.InfoRequested -= OnPanelInfo;
                exportPanel.CloseRequested += CloseExport;
                exportPanel.ExportSucceeded += OnExportSucceeded;
                exportPanel.InfoRequested += OnPanelInfo;
            }

            if (importPanel != null) {
                importPanel.CloseRequested -= CloseImport;
                importPanel.ConfirmClearRequested -= OnImportConfirmClear;
                importPanel.ImportSucceeded -= OnImportSucceeded;
                importPanel.InfoRequested -= OnPanelInfo;
                importPanel.CloseRequested += CloseImport;
                importPanel.ConfirmClearRequested += OnImportConfirmClear;
                importPanel.ImportSucceeded += OnImportSucceeded;
                importPanel.InfoRequested += OnPanelInfo;
            }

            if (msgDialog != null) {
                msgDialog.CloseRequested -= OnMsgClose;
                msgDialog.PrimaryRequested -= OnMsgPrimary;
                msgDialog.SecondaryRequested -= OnMessageSecondary;
                msgDialog.CloseRequested += OnMsgClose;
                msgDialog.PrimaryRequested += OnMsgPrimary;
                msgDialog.SecondaryRequested += OnMessageSecondary;
            }
        }

        // ============================================================================
        // 设备选择
        // ============================================================================

        /// <summary>左侧列表选中设备：同步 ViewModel，再刷标题栏与变量表。</summary>
        /// <param name="deviceId">设备 Id，可为 null（无选中）。</param>
        private void OnDeviceSelected (string deviceId) {
            // 同步 ViewModel 选中项，再刷标题栏和变量表
            _vm.SelectDevice(deviceId);
            deviceHeader.Show(deviceId);
            variableTable.Load(deviceId);
        }

        // ============================================================================
        // 单条编辑
        // ============================================================================

        /// <summary>打开新增变量面板。未选设备时由 ViewModel 弹提示并拦下。</summary>
        private void OpenAdd () {
            // 未选设备时 ViewModel 会弹提示，面板未生成则忽略
            if (!_vm.EnsureDeviceSelected() || editPanel == null)
                return;
            // 空白表单，保存走新增
            editPanel.PrepareNew();
            ShowPanel(editPanel);
        }

        /// <summary>打开编辑弹层并用已存变量回填；<paramref name="item"/> 为 null 表示新增。</summary>
        public void OpenEdit (VariableItem item) {
            // 无条目或面板未就绪则忽略
            if (item == null || editPanel == null)
                return;
            // 编辑其它设备的变量时先切选中项，保证保存挂对 DeviceId
            _vm.SelectDevice(item.DeviceId);
            editPanel.Load(item);
            ShowPanel(editPanel);
        }

        // 收起编辑弹层，遮罩在无其它面板时一并关掉
        private void CloseEdit () => HidePanel(editPanel);

        /// <summary>
        /// 保存编辑结果。
        /// </summary>
        /// <remarks>
        /// <c>Build()</c> 返回 null 表示面板内的校验没过（名称或地址为空），
        /// 此时<b>不关弹层</b>——直接关掉会让操作员刚填的内容全部丢失，
        /// 而且看不出是哪一项没填对。
        /// </remarks>
        private void SaveEdit () {
            // 面板尚未加载完时不能 Build
            if (editPanel == null)
                return;

            VariableItem built = editPanel.Build();
            // Build 校验失败（名称/地址为空）时不保存
            if (built == null)
                return;

            // CRUD 交给 ViewModel，成功后再关弹层
            _vm.SaveVariable(built, editPanel.IsNew);
            CloseEdit();
        }

        /// <summary>删除当前编辑的变量。新增模式下没有可删对象，直接返回。</summary>
        private void DeleteEdit () {
            // 新增模式没有可删 Id
            if (editPanel == null || editPanel.IsNew || string.IsNullOrEmpty(editPanel.EditingId))
                return;

            _vm.DeleteVariable(editPanel.EditingId);
            // 删除后关掉弹层，列表由 ViewModel 的 RequestRefresh 刷新
            CloseEdit();
        }

        // ============================================================================
        // 批量
        // ============================================================================

        /// <summary>打开批量添加弹层。未选设备时不打开——批量行必须有归属设备。</summary>
        private void OpenBatch () {
            // 未选设备时不打开，避免批量行没有 DeviceId
            if (!_vm.EnsureDeviceSelected() || batchPanel == null)
                return;
            // 副标题带设备名，默认 3 行空白
            batchPanel.Prepare(_vm.GetSelectedDeviceTitle());
            ShowPanel(batchPanel);
        }

        /// <summary>收起批量添加弹层。</summary>
        private void CloseBatch () => HidePanel(batchPanel);

        /// <summary>批量入库后关闭弹层；列表刷新由 ViewModel 的 RequestRefresh 触发。</summary>
        private void SaveBatch (IList<VariableItem> items) {
            // 批量入库后关弹层，刷新由 ViewModel 触发
            _vm.SaveBatch(items);
            CloseBatch();
        }

        // ============================================================================
        // 导入 / 导出
        // ============================================================================

        /// <summary>打开导出面板，并把当前设备信息交给它生成默认文件名。</summary>
        private void OpenExport () {
            // 面板未生成时忽略顶栏点击
            if (exportPanel == null)
                return;
            // 把当前设备、标题、条数交给弹层生成默认文件名
            exportPanel.Prepare(
                _vm.SelectedDeviceId,
                _vm.GetSelectedDeviceTitle(),
                _vm.CountCurrentVariables());
            ShowPanel(exportPanel);
        }

        /// <summary>收起导出弹层。</summary>
        private void CloseExport () => HidePanel(exportPanel);

        /// <summary>导出成功：先收起导出层，再弹成功框。</summary>
        /// <remarks>顺序固定——两个弹层同时可见会叠在一起，遮罩层级也会乱。</remarks>
        private void OnExportSucceeded (string path, int count) {
            // 先关导出层，再弹成功框（含「打开目录」）
            CloseExport();
            ShowExportSuccess(path, count);
        }

        /// <summary>
        /// 弹出导出成功框，附带「打开目录」次按钮。
        /// </summary>
        /// <remarks>
        /// 记住 <see cref="_lastExportPath"/> 是必须的：次按钮的回调拿不到参数，
        /// 只能从字段读路径。同时把 pending 清成 None——
        /// 这个框的主按钮只是确认，不该触发任何待决动作。
        /// </remarks>
        private void ShowExportSuccess (string path, int count) {
            if (msgDialog == null)
                return;
            // 记住路径，次按钮用来开资源管理器
            _lastExportPath = path;
            _msgPending = MsgPending.None;
            // 路径放详情框，次按钮用于打开目录
            msgDialog.Setup(
                AppMessageKind.Success,
                "导出完成",
                "已导出 " + count + " 条变量",
                path,
                primaryText: "确定",
                secondaryText: "打开目录",
                showSecondary: true,
                detailAsBox: true);
            ShowPanel(msgDialog);
        }

        /// <summary>打开导入弹层，以当前所选设备作为默认导入范围。</summary>
        private void OpenImport () {
            if (importPanel == null)
                return;
            // 当前设备作为默认导入范围
            importPanel.Prepare(_vm.SelectedDeviceId, _vm.GetSelectedDeviceTitle());
            ShowPanel(importPanel);
        }

        /// <summary>收起导入弹层。</summary>
        private void CloseImport () => HidePanel(importPanel);

        /// <summary>
        /// 导入前的覆盖确认。
        /// </summary>
        /// <remarks>
        /// 导入会覆盖当前范围内的变量，且不可撤销，因此必须二次确认。
        /// 置 <c>MsgPending.ImportClear</c> 让主按钮回调知道该执行导入——
        /// 消息框是复用的，不靠这个标记就分不清这次的确定是要干什么。
        /// </remarks>
        private void OnImportConfirmClear (string title, string detail) {
            if (msgDialog == null)
                return;
            // 记下 pending，主按钮确认后才真正 ExecuteImport
            _msgPending = MsgPending.ImportClear;
            msgDialog.Setup(
                AppMessageKind.Warning,
                title,
                "导入将覆盖当前范围内变量",
                detail,
                primaryText: "继续导入",
                secondaryText: "取消",
                showSecondary: true);
            ShowPanel(msgDialog);
        }

        /// <summary>导入成功：先刷表与左侧计数，再弹成功框。</summary>
        /// <remarks>先刷新再弹框——反过来会让操作员点掉提示后才看到数据变化。</remarks>
        private void OnImportSucceeded (int count) {
            // 先刷表和左侧计数，再弹成功框
            RefreshList();
            if (msgDialog == null)
                return;
            _msgPending = MsgPending.None;
            msgDialog.Setup(
                AppMessageKind.Success,
                "导入完成",
                "已导入 " + count + " 条变量",
                detail: null,
                primaryText: "确定",
                showSecondary: false);
            ShowPanel(msgDialog);
        }

        /// <summary>
        /// 消息框主按钮：取出待决动作并执行。
        /// </summary>
        /// <remarks>
        /// <b>先取出再清空，然后才执行。</b>执行中若再次弹框（例如导入失败提示），
        /// 残留的 pending 会让那个框的确定又跑一次导入。
        /// </remarks>
        private void OnMsgPrimary () {
            MsgPending pending = _msgPending;
            _msgPending = MsgPending.None;
            HidePanel(msgDialog);
            // 导入前清空确认：用户点确定后才真正写入
            if (pending == MsgPending.ImportClear)
                importPanel?.ExecuteImport();
        }

        /// <summary>消息框取消/关闭：清掉待决动作，避免下次误触发。</summary>
        private void OnMsgClose () {
            // 取消/关框：清掉 pending，避免下次误触发导入
            _msgPending = MsgPending.None;
            HidePanel(msgDialog);
        }

        /// <summary>次按钮，目前只用于「打开导出目录」。</summary>
        /// <remarks>目录可能在弹框期间被删掉，因此打开前要再确认一次存在性。</remarks>
        private void OnMessageSecondary () {
            // 次按钮只用于「打开导出目录」
            if (string.IsNullOrEmpty(_lastExportPath))
                return;

            string dir = Path.GetDirectoryName(_lastExportPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) {
                ShowInfo("提示", "目录不存在或已被删除");
                return;
            }

            try {
                // 用资源管理器打开所在目录，方便核对导出文件
                Process.Start(new ProcessStartInfo {
                    FileName = dir,
                    UseShellExecute = true
                });
            } catch (InvalidOperationException ex) {
                ShowInfo("打开目录失败", ex.Message);
            } catch (System.ComponentModel.Win32Exception ex) {
                ShowInfo("打开目录失败", ex.Message);
            }
        }

        // ============================================================================
        // 写入
        // ============================================================================

        /// <summary>变量表请求写入 PLC。</summary>
        /// <remarks>
        /// <c>async void</c> 在这里是必要的——它是事件处理器，签名不能改。
        /// 内部 <c>await</c> 的 <c>WriteVariableAsync</c> 自行捕获异常并走
        /// RequestShowInfo 提示，不会有异常逃逸成为未观察的任务异常。
        /// </remarks>
        /// <param name="variableId">变量 Id。</param>
        /// <param name="writeText">操作员输入的值文本，解析由下层负责。</param>
        private async void OnVariableWriteRequested (string variableId, string writeText) {
            // 协议写入走 ViewModel，失败时由 RequestShowInfo 弹主题框
            await _vm.WriteVariableAsync(variableId, writeText);
        }

        // ============================================================================
        // 消息
        // ============================================================================

        /// <summary>子面板请求提示：统一走本页的主题信息框。</summary>
        /// <param name="title">标题。</param>
        /// <param name="message">正文。</param>
        private void OnPanelInfo (string title, string message) =>
            ShowInfo(title, message);

        /// <summary>弹一个只有确定按钮的信息框。</summary>
        private void ShowInfo (string title, string message) {
            if (msgDialog == null)
                return;
            // 普通提示不带 pending，避免和导入确认串台
            _msgPending = MsgPending.None;
            msgDialog.Setup(
                AppMessageKind.Info,
                title ?? "提示",
                message ?? "",
                detail: null,
                primaryText: "确定",
                showSecondary: false);
            ShowPanel(msgDialog);
        }

        /// <summary>点击遮罩空白处关闭全部弹层。</summary>
        /// <remarks>只响应打在遮罩<b>自身</b>上的点击；打在弹层上的会冒泡上来，
        /// 不加判断会导致"点弹层内部反而把弹层关了"。</remarks>
        private void EditOverlay_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) {
            // 点遮罩关闭所有弹层（含未完成的导入确认）
            CloseAllPanels();
        }

        // ============================================================================
        // 弹层 Visibility
        // ============================================================================

        /// <summary>显示指定弹层，并保证同时只有一个可见。</summary>
        /// <param name="panel">要显示的弹层；为 null 时只显示遮罩。</param>
        private void ShowPanel (UIElement panel) {
            // 同时只显示一个弹层，先收起其它再打开目标
            HideAllPanels();
            if (panel != null)
                panel.Visibility = Visibility.Visible;
            if (editOverlay != null)
                editOverlay.Visibility = Visibility.Visible;
        }

        /// <summary>隐藏指定弹层，并在没有其它弹层时收起遮罩。</summary>
        private void HidePanel (UIElement panel) {
            if (panel != null)
                panel.Visibility = Visibility.Collapsed;
            // 没有其它弹层时再关遮罩，避免闪一下露出页面
            HideOverlayIfIdle();
        }

        /// <summary>收起全部弹层，但<b>不</b>动遮罩与 pending 状态。</summary>
        private void HideAllPanels () {
            SetCollapsed(editPanel);
            SetCollapsed(batchPanel);
            SetCollapsed(exportPanel);
            SetCollapsed(importPanel);
            SetCollapsed(msgDialog);
        }

        /// <summary>关闭全部弹层与遮罩，并清掉待决操作。</summary>
        /// <remarks>清 pending 是关键：不清的话下次弹消息框点确定，
        /// 会执行上一次遗留的动作（例如意外触发一次清空导入）。</remarks>
        private void CloseAllPanels () {
            // 点遮罩关闭时清掉导入确认 pending
            _msgPending = MsgPending.None;
            HideAllPanels();
            if (editOverlay != null)
                editOverlay.Visibility = Visibility.Collapsed;
        }

        /// <summary>仅当所有弹层都已收起时才隐藏遮罩。</summary>
        /// <remarks>逐层关闭时若无条件隐藏遮罩，会在两个弹层之间闪一下露出底下的页面。</remarks>
        private void HideOverlayIfIdle () {
            bool busy =
                IsShown(editPanel) || IsShown(batchPanel) ||
                IsShown(exportPanel) || IsShown(importPanel) ||
                IsShown(msgDialog);

            // 仍有弹层则保留遮罩
            if (!busy && editOverlay != null)
                editOverlay.Visibility = Visibility.Collapsed;
        }

        /// <summary>安全收起一个元素；XAML 未加载完时该元素可能为 null。</summary>
        private static void SetCollapsed (UIElement e) {
            if (e != null)
                e.Visibility = Visibility.Collapsed;
        }

        /// <summary>元素是否处于可见状态；null 视为不可见。</summary>
        private static bool IsShown (UIElement e) =>
            e != null && e.Visibility == Visibility.Visible;

        /// <summary>按当前所选设备重建变量表，并同步刷新左侧的变量计数。</summary>
        private void RefreshList () {
            // 变量表按当前设备重建，左侧同步刷新计数
            variableTable.Load(_vm.SelectedDeviceId);
            deviceList.Reload();
        }
    }
}