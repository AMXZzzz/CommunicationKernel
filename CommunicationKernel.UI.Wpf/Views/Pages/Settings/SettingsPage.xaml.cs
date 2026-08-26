#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Settings/SettingsPage.xaml.cs
// 层级: UI 层 — 系统设置页 code-behind
// 作用: 仅保留无法在 XAML 中完成的两个 RadioButton Checked 事件处理；
//       所有业务逻辑（读/写 settings.json、测试连接）均委托给 SettingsViewModel。
// 调用链:
//   App.xaml.cs → DI → SettingsPage(SettingsViewModel)
//     → DataContext = _vm
//       → TestConnectionCommand / SaveCommand / HostAddress 等通过 XAML 绑定驱动
//     → RdoLocal_Checked / RdoRemote_Checked → _vm.IsRemoteMode = false/true
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Controls;
using CommunicationKernel.UI.Wpf.ViewModels;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Settings {

    /// <summary>
    /// 系统设置页 code-behind。
    /// 除两个 RadioButton Checked 事件外，所有逻辑均在
    /// <see cref="SettingsViewModel"/> 中实现，通过 DataContext 绑定驱动。
    /// </summary>
    public partial class SettingsPage : Page {

        // -------------------------------------------------------------------------
        // 私有字段
        // -------------------------------------------------------------------------

        /// <summary>设置页 ViewModel，持有地址配置与命令。</summary>
        private readonly SettingsViewModel _vm;

        // -------------------------------------------------------------------------
        // 构造函数
        // -------------------------------------------------------------------------

        /// <param name="viewModel">由 DI 注入的设置 ViewModel。</param>
        public SettingsPage (SettingsViewModel viewModel) {
            // 保存 ViewModel 引用
            _vm = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));

            // 初始化 XAML 视觉树
            InitializeComponent();

            // 将 ViewModel 挂载到 DataContext，驱动所有 XAML 绑定
            DataContext = _vm;
        }

        // -------------------------------------------------------------------------
        // RadioButton Checked 事件（无法在 XAML 中完成，保留在 code-behind）
        // -------------------------------------------------------------------------

        /// <summary>
        /// 本地模式被选中：将 IsRemoteMode 置为 false，
        /// XAML 绑定会自动折叠 pnlRemote（通过 BooleanToVisibilityConverter）。
        /// </summary>
        private void RdoLocal_Checked (object sender, RoutedEventArgs e) {
            // 本地模式不需要 gRPC 远端地址配置
            _vm.IsRemoteMode = false;
        }

        /// <summary>
        /// 远端模式被选中：将 IsRemoteMode 置为 true，
        /// XAML 绑定会自动展开 pnlRemote 以显示地址配置区。
        /// </summary>
        private void RdoRemote_Checked (object sender, RoutedEventArgs e) {
            // 远端模式需要配置 EngineHost.App gRPC 地址
            _vm.IsRemoteMode = true;
        }
    }
}
