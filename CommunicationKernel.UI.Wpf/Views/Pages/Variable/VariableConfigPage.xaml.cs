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
        private enum MsgPending {
            None,
            ImportClear
        }

        private readonly VariablePageViewModel _vm;
        private string _lastExportPath;
        private MsgPending _msgPending;

        /// <summary>是否已订阅单例 ViewModel 的事件，保证订阅/退订幂等。</summary>
        private bool _vmWired;

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

        // ViewModel 请求弹 Info 时走本页主题对话框
        private void OnRequestShowInfo (string title, string msg) => ShowInfo(title, msg);

        // 重新进入可视树时恢复订阅（首次即初次订阅）
        private void VariableConfigPage_Loaded (object sender, RoutedEventArgs e) => WireViewModel();

        // 离开可视树立即退订，防止切页后重复弹框
        private void VariableConfigPage_Unloaded (object sender, RoutedEventArgs e) => UnwireViewModel();

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

        private void OnDeviceSelected (string deviceId) {
            // 同步 ViewModel 选中项，再刷标题栏和变量表
            _vm.SelectDevice(deviceId);
            deviceHeader.Show(deviceId);
            variableTable.Load(deviceId);
        }

        // ============================================================================
        // 单条编辑
        // ============================================================================

        private void OpenAdd () {
            // 未选设备时 ViewModel 会弹提示，面板未生成则忽略
            if (!_vm.EnsureDeviceSelected() || editPanel == null)
                return;
            // 空白表单，保存走新增
            editPanel.PrepareNew();
            ShowPanel(editPanel);
        }

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

        private void OpenBatch () {
            // 未选设备时不打开，避免批量行没有 DeviceId
            if (!_vm.EnsureDeviceSelected() || batchPanel == null)
                return;
            // 副标题带设备名，默认 3 行空白
            batchPanel.Prepare(_vm.GetSelectedDeviceTitle());
            ShowPanel(batchPanel);
        }

        private void CloseBatch () => HidePanel(batchPanel);

        private void SaveBatch (IList<VariableItem> items) {
            // 批量入库后关弹层，刷新由 ViewModel 触发
            _vm.SaveBatch(items);
            CloseBatch();
        }

        // ============================================================================
        // 导入 / 导出
        // ============================================================================

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

        private void CloseExport () => HidePanel(exportPanel);

        private void OnExportSucceeded (string path, int count) {
            // 先关导出层，再弹成功框（含「打开目录」）
            CloseExport();
            ShowExportSuccess(path, count);
        }

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

        private void OpenImport () {
            if (importPanel == null)
                return;
            // 当前设备作为默认导入范围
            importPanel.Prepare(_vm.SelectedDeviceId, _vm.GetSelectedDeviceTitle());
            ShowPanel(importPanel);
        }

        private void CloseImport () => HidePanel(importPanel);

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

        private void OnMsgPrimary () {
            MsgPending pending = _msgPending;
            _msgPending = MsgPending.None;
            HidePanel(msgDialog);
            // 导入前清空确认：用户点确定后才真正写入
            if (pending == MsgPending.ImportClear)
                importPanel?.ExecuteImport();
        }

        private void OnMsgClose () {
            // 取消/关框：清掉 pending，避免下次误触发导入
            _msgPending = MsgPending.None;
            HidePanel(msgDialog);
        }

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

        private async void OnVariableWriteRequested (string variableId, string writeText) {
            // 协议写入走 ViewModel，失败时由 RequestShowInfo 弹主题框
            await _vm.WriteVariableAsync(variableId, writeText);
        }

        // ============================================================================
        // 消息
        // ============================================================================

        private void OnPanelInfo (string title, string message) =>
            ShowInfo(title, message);

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

        private void EditOverlay_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) {
            // 点遮罩关闭所有弹层（含未完成的导入确认）
            CloseAllPanels();
        }

        // ============================================================================
        // 弹层 Visibility
        // ============================================================================

        private void ShowPanel (UIElement panel) {
            // 同时只显示一个弹层，先收起其它再打开目标
            HideAllPanels();
            if (panel != null)
                panel.Visibility = Visibility.Visible;
            if (editOverlay != null)
                editOverlay.Visibility = Visibility.Visible;
        }

        private void HidePanel (UIElement panel) {
            if (panel != null)
                panel.Visibility = Visibility.Collapsed;
            // 没有其它弹层时再关遮罩，避免闪一下露出页面
            HideOverlayIfIdle();
        }

        private void HideAllPanels () {
            SetCollapsed(editPanel);
            SetCollapsed(batchPanel);
            SetCollapsed(exportPanel);
            SetCollapsed(importPanel);
            SetCollapsed(msgDialog);
        }

        private void CloseAllPanels () {
            // 点遮罩关闭时清掉导入确认 pending
            _msgPending = MsgPending.None;
            HideAllPanels();
            if (editOverlay != null)
                editOverlay.Visibility = Visibility.Collapsed;
        }

        private void HideOverlayIfIdle () {
            bool busy =
                IsShown(editPanel) || IsShown(batchPanel) ||
                IsShown(exportPanel) || IsShown(importPanel) ||
                IsShown(msgDialog);

            // 仍有弹层则保留遮罩
            if (!busy && editOverlay != null)
                editOverlay.Visibility = Visibility.Collapsed;
        }

        private static void SetCollapsed (UIElement e) {
            if (e != null)
                e.Visibility = Visibility.Collapsed;
        }

        private static bool IsShown (UIElement e) =>
            e != null && e.Visibility == Visibility.Visible;

        private void RefreshList () {
            // 变量表按当前设备重建，左侧同步刷新计数
            variableTable.Load(_vm.SelectedDeviceId);
            deviceList.Reload();
        }
    }
}