#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Controls/AppMessageDialog.xaml.cs
// 层级: UI 层 — WPF 通用控件
// 作用: 主题化消息/确认弹层，按 Info/Success/Warning/Danger 切换配色与按钮。
// -----------------------------------------------------------------------------

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CommunicationKernel.UI.Wpf.Views.Controls {

    // ============================================================================
    // 消息种类
    // ============================================================================

    /// <summary>消息种类，决定弹层的强调色、图标与主按钮样式。</summary>
    public enum AppMessageKind {

        /// <summary>普通信息，强调色为主题主色。</summary>
        Info,

        /// <summary>操作成功。</summary>
        Success,

        /// <summary>警告，操作可继续但需留意。</summary>
        Warning,

        /// <summary>危险操作（如删除），主按钮为红色。</summary>
        Danger
    }

    /// <summary>主题化消息/确认弹层。</summary>
    public partial class AppMessageDialog : UserControl {

        /// <summary>请求收起弹层。三个按钮最终都会触发它。</summary>
        public event Action CloseRequested;

        /// <summary>主按钮被点击（确定 / 删除）。在 <see cref="CloseRequested"/> 之前触发。</summary>
        public event Action PrimaryRequested;

        /// <summary>次按钮被点击（取消）。</summary>
        public event Action SecondaryRequested;

        /// <summary>
        /// 上一次交互是否点了主按钮。
        /// </summary>
        /// <remarks>
        /// 供不订阅事件、只在关闭后回看结果的调用方使用。
        /// 关闭按钮与次按钮都会把它置 false——只有明确点了主按钮才算确认。
        /// </remarks>
        public bool ResultConfirmed { get; private set; }

        /// <summary>构造：解析 XAML，构建视觉树。</summary>
        public AppMessageDialog () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }

        // ============================================================================
        // 内容配置
        // ============================================================================

        /// <summary>配置并显示内容。</summary>
        public void Setup (
            AppMessageKind kind,
            string title,
            string message,
            string detail = null,
            string primaryText = "确定",
            string secondaryText = "取消",
            bool showSecondary = true,
            bool detailAsBox = false) {
            // 每次打开都视为未确认，避免沿用上次结果
            ResultConfirmed = false;
            txtTitle.Text = title ?? "提示";
            txtMessage.Text = message ?? "";
            btnPrimary.Content = string.IsNullOrEmpty(primaryText) ? "确定" : primaryText;
            btnSecondary.Content = string.IsNullOrEmpty(secondaryText) ? "取消" : secondaryText;
            // 纯提示只留主按钮
            btnSecondary.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;

            // 按种类刷色条、图标、主按钮样式
            ApplyKind(kind);

            // 无详情：两处都藏；detailAsBox 走等宽框，否则走普通副文案
            if (string.IsNullOrWhiteSpace(detail)) {
                txtDetail.Visibility = Visibility.Collapsed;
                detailBox.Visibility = Visibility.Collapsed;
            } else if (detailAsBox) {
                txtDetail.Visibility = Visibility.Collapsed;
                detailBox.Visibility = Visibility.Visible;
                txtDetailBox.Text = detail;
            } else {
                detailBox.Visibility = Visibility.Collapsed;
                txtDetail.Visibility = Visibility.Visible;
                txtDetail.Text = detail;
            }
        }

        // ============================================================================
        // 外观
        // ============================================================================

        /// <summary>按消息种类刷新强调条、图标与主按钮样式。</summary>
        /// <param name="kind">消息种类。未列出的一律按 <see cref="AppMessageKind.Info"/> 处理。</param>
        private void ApplyKind (AppMessageKind kind) {
            // 从主题资源取语义色；找不到时 BrushOf 回退灰色
            Brush accent = BrushOf("SF.Brush.Accent.Default");
            Brush success = BrushOf("SF.Brush.Status.Success");
            Brush warning = BrushOf("SF.Brush.Status.Warning");
            Brush error = BrushOf("SF.Brush.Status.Error");
            Style primary = TryFindResource("SF.Style.PrimaryButton") as Style;
            Style danger = TryFindResource("SF.Style.DangerButton") as Style;

            switch (kind) {
                case AppMessageKind.Success:
                    barAccent.Background = success;
                    iconCircle.Background = success;
                    txtIcon.Text = "✓";
                    btnPrimary.Style = primary;
                    break;
                case AppMessageKind.Warning:
                    barAccent.Background = warning;
                    iconCircle.Background = warning;
                    txtIcon.Text = "!";
                    btnPrimary.Style = primary;
                    break;
                case AppMessageKind.Danger:
                    barAccent.Background = error;
                    iconCircle.Background = error;
                    txtIcon.Text = "!";
                    // 危险操作用红色主按钮，资源缺失时退回 Primary
                    btnPrimary.Style = danger ?? primary;
                    break;
                default:
                    barAccent.Background = accent;
                    iconCircle.Background = accent;
                    txtIcon.Text = "i";
                    btnPrimary.Style = primary;
                    break;
            }
        }

        /// <summary>按键取主题画刷。</summary>
        /// <param name="key">资源键。</param>
        /// <returns>画刷；资源缺失时回退灰色，绝不返回 null——弹层的图标底色不能是空的。</returns>
        private Brush BrushOf (string key) =>
            TryFindResource(key) as Brush ?? Brushes.Gray;

        // ============================================================================
        // 按钮
        // ============================================================================

        /// <summary>右上角关闭：视为未确认。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnClose_Click (object sender, RoutedEventArgs e) {
            ResultConfirmed = false;
            CloseRequested?.Invoke();
        }

        /// <summary>次按钮（取消）：视为未确认，先发自身事件再请求关闭。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnSecondary_Click (object sender, RoutedEventArgs e) {
            ResultConfirmed = false;
            SecondaryRequested?.Invoke();
            CloseRequested?.Invoke();
        }

        /// <summary>
        /// 主按钮（确定 / 删除）：标记已确认。
        /// </summary>
        /// <remarks>
        /// <see cref="ResultConfirmed"/> 必须在事件之前赋值——
        /// 订阅方常在回调里直接读它，赋值晚一步读到的就是上一次的结果。
        /// </remarks>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnPrimary_Click (object sender, RoutedEventArgs e) {
            ResultConfirmed = true;
            PrimaryRequested?.Invoke();
            CloseRequested?.Invoke();
        }
    }
}
