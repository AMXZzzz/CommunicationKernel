#nullable disable

using System;
using System.Windows;
using System.Windows.Controls;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Device {
    /// <summary>
    /// 设备管理页顶部工具栏（风格对齐 MES 顶栏：左操作、右统计）。
    /// <para>
    /// 普通模式：一键连接 / 一键断开 / 刷新 / 删除（删除仅用于进入多选）。
    /// 多选模式：确认删除 / 取消（由 <see cref="SetSelectMode"/> 切换 Visibility）。
    /// 业务由 <c>DevicePage</c> 通过事件处理，本控件不直接操作设备服务。
    /// </para>
    /// </summary>
    public partial class DeviceToolBar : UserControl {
        /// <summary>一键连接所有未连接设备。</summary>
        public event Action ConnectAllClicked;

        /// <summary>一键断开全部设备。</summary>
        public event Action DisconnectAllClicked;

        /// <summary>从持久化刷新设备列表。</summary>
        public event Action RefreshClicked;

        /// <summary>进入多选删除模式（不执行删除）。</summary>
        public event Action DeleteClicked;

        /// <summary>确认删除当前已勾选设备。</summary>
        public event Action ConfirmDeleteClicked;

        /// <summary>取消多选，恢复普通工具栏。</summary>
        public event Action CancelSelectClicked;

        public DeviceToolBar () {
            InitializeComponent();
        }

        /// <summary>
        /// 更新右侧「设备总数」角标。
        /// </summary>
        /// <param name="count">设备数量（不含添加占位卡）。</param>
        public void SetCount (int count) {
            if (txtCount != null)
                txtCount.Text = count.ToString();
        }

        /// <summary>
        /// 切换普通 / 多选模式的按钮可见性。
        /// </summary>
        /// <param name="selectMode">true 显示确认删除与取消；false 显示常规四键。</param>
        public void SetSelectMode (bool selectMode) {
            Visibility normal = selectMode ? Visibility.Collapsed : Visibility.Visible;
            Visibility select = selectMode ? Visibility.Visible : Visibility.Collapsed;

            if (btnConnectAll != null)
                btnConnectAll.Visibility = normal;
            if (btnDisconnectAll != null)
                btnDisconnectAll.Visibility = normal;
            if (btnRefresh != null)
                btnRefresh.Visibility = normal;
            if (btnDelete != null)
                btnDelete.Visibility = normal;

            if (btnConfirmDelete != null)
                btnConfirmDelete.Visibility = select;
            if (btnCancelSelect != null)
                btnCancelSelect.Visibility = select;
        }

        /// <summary>一键连接。</summary>
        private void BtnConnectAll_Click (object sender, RoutedEventArgs e) {
            if (ConnectAllClicked != null)
                ConnectAllClicked();
        }

        /// <summary>一键断开。</summary>
        private void BtnDisconnectAll_Click (object sender, RoutedEventArgs e) {
            if (DisconnectAllClicked != null)
                DisconnectAllClicked();
        }

        /// <summary>刷新。</summary>
        private void BtnRefresh_Click (object sender, RoutedEventArgs e) {
            if (RefreshClicked != null)
                RefreshClicked();
        }

        /// <summary>删除 → 通知页面进入多选。</summary>
        private void BtnDelete_Click (object sender, RoutedEventArgs e) {
            if (DeleteClicked != null)
                DeleteClicked();
        }

        /// <summary>确认删除已勾选项。</summary>
        private void BtnConfirmDelete_Click (object sender, RoutedEventArgs e) {
            if (ConfirmDeleteClicked != null)
                ConfirmDeleteClicked();
        }

        /// <summary>取消多选。</summary>
        private void BtnCancelSelect_Click (object sender, RoutedEventArgs e) {
            if (CancelSelectClicked != null)
                CancelSelectClicked();
        }
    }
}