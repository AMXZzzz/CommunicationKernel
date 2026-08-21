using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CommunicationKernel.UI.Wpf.Views.Controls {
    public enum AppMessageKind {
        Info,
        Success,
        Warning,
        Danger
    }

    /// <summary>主题化消息/确认弹层（。</summary>
    public partial class AppMessageDialog : UserControl {
        public event Action CloseRequested;
        public event Action PrimaryRequested;
        public event Action SecondaryRequested;

        public bool ResultConfirmed { get; private set; }

        public AppMessageDialog () {
            InitializeComponent();
        }

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
            ResultConfirmed = false;
            txtTitle.Text = title ?? "提示";
            txtMessage.Text = message ?? "";
            btnPrimary.Content = string.IsNullOrEmpty(primaryText) ? "确定" : primaryText;
            btnSecondary.Content = string.IsNullOrEmpty(secondaryText) ? "取消" : secondaryText;
            btnSecondary.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;

            ApplyKind(kind);

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

        private void ApplyKind (AppMessageKind kind) {
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