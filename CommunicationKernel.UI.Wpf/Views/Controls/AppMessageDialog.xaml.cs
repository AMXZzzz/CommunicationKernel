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

    public enum AppMessageKind {
        Info,
        Success,
        Warning,
        Danger
    }

    /// <summary>主题化消息/确认弹层。</summary>
    public partial class AppMessageDialog : UserControl {
        public event Action CloseRequested;
        public event Action PrimaryRequested;
        public event Action SecondaryRequested;

        public bool ResultConfirmed { get; private set; }

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

        private Brush BrushOf (string key) =>
            TryFindResource(key) as Brush ?? Brushes.Gray;

        // ============================================================================
        // 按钮
        // ============================================================================

        private void BtnClose_Click (object sender, RoutedEventArgs e) {
            ResultConfirmed = false;
            CloseRequested?.Invoke();
        }

        private void BtnSecondary_Click (object sender, RoutedEventArgs e) {
            ResultConfirmed = false;
            SecondaryRequested?.Invoke();
            CloseRequested?.Invoke();
        }

        private void BtnPrimary_Click (object sender, RoutedEventArgs e) {
            ResultConfirmed = true;
            PrimaryRequested?.Invoke();
            CloseRequested?.Invoke();
        }
    }
}
