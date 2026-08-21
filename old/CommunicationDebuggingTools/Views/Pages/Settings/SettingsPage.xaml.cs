using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using CommunicationDebuggingTools.Client;
using CommunicationDebuggingTools.Services;

namespace CommunicationDebuggingTools.Views.Pages.Settings {

    public partial class SettingsPage : Page {

        private readonly AppSettings _settings;

        public SettingsPage () {
            InitializeComponent();
            _settings = AppSettings.Load();
            Loaded   += OnLoaded;
        }

        private void OnLoaded (object sender, RoutedEventArgs e) {
            if (_settings.RemoteMode) {
                if (rdoRemote != null) rdoRemote.IsChecked = true;
            } else {
                if (rdoLocal  != null) rdoLocal.IsChecked  = true;
            }
            if (txtAddress != null) txtAddress.Text = _settings.HostAddress;
        }

        private void RdoLocal_Checked  (object sender, RoutedEventArgs e) =>
            SetRemotePanel(false);

        private void RdoRemote_Checked (object sender, RoutedEventArgs e) =>
            SetRemotePanel(true);

        private void SetRemotePanel (bool show) {
            if (pnlRemote != null)
                pnlRemote.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BtnTest_Click (object sender, RoutedEventArgs e) {
            if (btnTest      != null) btnTest.IsEnabled    = false;
            if (lblTestResult != null) lblTestResult.Text  = "连接中...";

            string addr = txtAddress?.Text?.Trim()
                          ?? AppSettings.DefaultHostAddress;
            try {
                using var client = EngineClient.Connect(addr);
                bool ok = await client.PingAsync(CancellationToken.None).ConfigureAwait(true);
                if (lblTestResult != null)
                    lblTestResult.Text = ok
                        ? "✔ 连接成功，EngineHost 可达"
                        : "✘ EngineHost 无响应";
            } catch (Exception ex) {
                if (lblTestResult != null)
                    lblTestResult.Text = "✘ " + ex.Message;
            } finally {
                if (btnTest != null) btnTest.IsEnabled = true;
            }
        }

        private void BtnSave_Click (object sender, RoutedEventArgs e) {
            _settings.RemoteMode  = rdoRemote?.IsChecked == true;
            _settings.HostAddress = txtAddress?.Text?.Trim()
                                    ?? AppSettings.DefaultHostAddress;
            _settings.Save();
            if (lblRestart != null) {
                lblRestart.Text       = "✔ 已保存，重启应用后生效";
                lblRestart.Visibility = Visibility.Visible;
            }
        }
    }
}
