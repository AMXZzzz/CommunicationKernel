#nullable disable

// -----------------------------------------------------------------------------
// 文件: ViewModels/SettingsViewModel.cs
// 层级: UI 层 — WPF 系统设置页 ViewModel
// 作用: 封装 Host.App 地址配置、连接模式、连接测试及 settings.json 持久化。
// 调用链:
//   SettingsPage → TestConnectionCommand → HostClient.HealthAsync
//                 → SaveCommand → settings.json
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Input;
using CommunicationKernel.UI.Wpf.Services;

namespace CommunicationKernel.UI.Wpf.ViewModels;

/// <summary>
/// 系统设置页 ViewModel。
/// 提供 <see cref="HostAddress"/>、<see cref="IsRemoteMode"/>、
/// <see cref="TestResultText"/>、<see cref="SaveConfirmText"/> 等可绑定属性，
/// 以及 <see cref="TestConnectionCommand"/> 和 <see cref="SaveCommand"/> 命令。
/// </summary>
public sealed class SettingsViewModel : ViewModelBase {

    // ============================================================================
    // 常量
    // ============================================================================

    /// <summary>持久化 JSON 文件路径（与 App.xaml.cs 中 LoadSavedHostAddress 保持一致）。</summary>
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CommunicationKernel", "settings.json");

    /// <summary>测试连接超时（秒）。</summary>
    private const int TestTimeoutSeconds = 5;

    // ============================================================================
    // 私有字段
    // ============================================================================

    /// <summary>gRPC 客户端，用于测试连接（HealthAsync）。</summary>
    private readonly HostClient _client;

    // 后备字段
    private string _hostAddress    = "http://localhost:5000";
    private bool   _isRemoteMode   = false;
    private string _testResultText = string.Empty;
    private string _saveConfirmText = string.Empty;
    private bool   _isTesting      = false;

    // ============================================================================
    // 公开属性（XAML 双向绑定）
    // ============================================================================

    /// <summary>
    /// Host.App gRPC 地址。绑定到 SettingsPage.txtAddress。
    /// 保存命令将此值写入 settings.json。
    /// </summary>
    public string HostAddress {
        get => _hostAddress;
        set => SetField(ref _hostAddress, value);
    }

    /// <summary>
    /// 是否处于远端模式。true → 显示地址配置区域。
    /// 由 SettingsPage 的 RadioButton Checked 事件写入（无需转换器）。
    /// </summary>
    public bool IsRemoteMode {
        get => _isRemoteMode;
        set => SetField(ref _isRemoteMode, value);
    }

    /// <summary>
    /// 连接测试结果文字。绑定到 SettingsPage.lblTestResult。
    /// 测试成功：绿色文字（由 Page 根据此属性决定颜色）；失败：错误描述。
    /// </summary>
    public string TestResultText {
        get => _testResultText;
        set => SetField(ref _testResultText, value);
    }

    /// <summary>
    /// 保存操作确认文字。绑定到 SettingsPage.lblRestart。
    /// 保存成功后置为提示文字，重启后清空。
    /// </summary>
    public string SaveConfirmText {
        get => _saveConfirmText;
        set => SetField(ref _saveConfirmText, value);
    }

    /// <summary>
    /// 是否正在测试连接（测试期间 true → 测试按钮禁用）。
    /// 绑定到 btnTest.IsEnabled（取反：IsEnabled="{Binding IsNotTesting}"）。
    /// </summary>
    public bool IsTesting {
        get => _isTesting;
        set {
            // 更新测试状态标志
            SetField(ref _isTesting, value);
            // 同步通知 IsNotTesting（计算属性，供按钮 IsEnabled 绑定）
            OnPropertyChanged(nameof(IsNotTesting));
        }
    }

    /// <summary>
    /// IsEnabled 绑定辅助：true 表示当前不在测试中，按钮可点击。
    /// </summary>
    public bool IsNotTesting => !_isTesting;

    // ============================================================================
    // 命令
    // ============================================================================

    /// <summary>测试连接命令：向当前 HostAddress 发起 HealthAsync 并更新 TestResultText。</summary>
    public ICommand TestConnectionCommand { get; }

    /// <summary>保存命令：将 HostAddress 写入 settings.json。</summary>
    public ICommand SaveCommand { get; }

    // ============================================================================
    // 构造函数
    // ============================================================================

    /// <param name="client">gRPC 客户端，用于测试连接，必须非 null。</param>
    public SettingsViewModel(HostClient client) {
        // 保存 gRPC 客户端引用
        _client = client ?? throw new ArgumentNullException(nameof(client));

        // 从 settings.json 加载已保存的地址，填充到绑定属性
        _hostAddress = LoadSavedAddress();

        // 绑定命令：测试连接（异步，使用 fire-and-forget 包装）
        TestConnectionCommand = new RelayCommand(ExecuteTestAsync);

        // 绑定命令：保存设置
        SaveCommand = new RelayCommand(ExecuteSave);
    }

    // ============================================================================
    // 命令实现
    // ============================================================================

    /// <summary>
    /// 测试连接：向 HostAddress 创建临时客户端并调用 HealthAsync。
    /// 在 UI 线程上以 async void 包装，避免 RelayCommand 阻塞。
    /// </summary>
    private async void ExecuteTestAsync() {
        // 防止重复点击
        if (_isTesting)
            return;

        // 标记测试中，禁用测试按钮
        IsTesting      = true;
        TestResultText = "连接中…";
        SaveConfirmText = string.Empty;

        // 读取当前输入地址，空值回落到开发默认
        string addr = string.IsNullOrWhiteSpace(_hostAddress)
            ? "http://localhost:5000"
            : _hostAddress.Trim();

        try {
            // 建立临时 gRPC 客户端（不与注入的单例共享，以测试新地址）
            HostClient tempClient = new HostClient(addr);

            using CancellationTokenSource cts =
                new CancellationTokenSource(TimeSpan.FromSeconds(TestTimeoutSeconds));

            // 发起 Health 检查
            (bool ok, string ver, int routes) =
                await tempClient.HealthAsync(cts.Token).ConfigureAwait(true);

            // 更新测试结果文字
            TestResultText = ok
                ? string.Format("✔ 连接成功 — Host.App v{0}，路由数: {1}", ver, routes)
                : "✘ Host.App 无响应";
        } catch (Exception ex) {
            // 网络异常或超时
            TestResultText = "✘ " + ex.Message;
        } finally {
            // 恢复按钮可用
            IsTesting = false;
        }
    }

    /// <summary>
    /// 保存设置：将当前 HostAddress 序列化写入 settings.json。
    /// </summary>
    private void ExecuteSave() {
        // 规范化地址（去空白），空值回落到开发默认
        string addr = string.IsNullOrWhiteSpace(_hostAddress)
            ? "http://localhost:5000"
            : _hostAddress.Trim();

        HostAddress = addr;

        try {
            // 确保目录存在
            string dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // 序列化为 JSON 并写入文件
            string json = JsonSerializer.Serialize(
                new AppSettings { HostAddress = addr },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);

            // 提示用户重启后生效（App 启动时才读取该文件）
            SaveConfirmText = "✔ 已保存，重启应用后生效";
            TestResultText  = string.Empty;
        } catch (Exception ex) {
            // 写入失败时通过 SaveConfirmText 显示错误
            SaveConfirmText = "✘ 保存失败: " + ex.Message;
        }
    }

    // ============================================================================
    // 持久化辅助
    // ============================================================================

    /// <summary>
    /// 从 settings.json 读取已保存的 HostAddress。
    /// 文件不存在或解析失败时返回默认地址 "http://localhost:5000"。
    /// </summary>
    private static string LoadSavedAddress() {
        const string defaultAddr = "http://localhost:5000";
        try {
            // 文件不存在视为首次运行
            if (!File.Exists(SettingsPath))
                return defaultAddr;

            // 读取并解析 JSON
            string json = File.ReadAllText(SettingsPath);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("HostAddress", out JsonElement el)) {
                string addr = el.GetString();
                // 非空时返回持久化地址
                if (!string.IsNullOrWhiteSpace(addr))
                    return addr;
            }
        } catch {
            // 解析失败时静默回退默认值
        }
        return defaultAddr;
    }

    // ============================================================================
    // 内部设置数据模型
    // ============================================================================

    /// <summary>settings.json 序列化模型，仅存储 HostAddress。</summary>
    private sealed class AppSettings {
        /// <summary>Host.App gRPC 服务地址。</summary>
        public string HostAddress { get; set; } = "http://localhost:5000";
    }
}
