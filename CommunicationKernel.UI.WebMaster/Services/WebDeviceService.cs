// -----------------------------------------------------------------------------
// 文件: Services/WebDeviceService.cs
// 层级: UI 层 — Web 服务
// 作用: 设备的连接/断开/注销与宿主侧清单查询，是 Blazor 页面唯一的设备操作入口。
// 调用链:
//   DevicesPage.razor → IWebDeviceService → HostClient → gRPC → Host.App
//
// 为什么要有这个类:
//   Web 端此前没有任何设备服务抽象，DevicesPage.razor 直接持有 Session.Client
//   发 gRPC——752 行的页面里 507 行是 @code，其中夹着注册、注销、失败分类与日志。
//   带来的问题：
//     1) 设备操作无法脱离 Blazor 渲染上下文测试；
//     2) 同一套失败分类逻辑在页面里散落多处，改一处漏一处；
//     3) 视图与传输细节耦合，换传输实现要动页面。
//   WPF 端的对应实现是 UI.Wpf/Services/GrpcDeviceService.cs，两端职责现已一致。
//
// 职责边界:
//   本服务负责"做操作 + 判定结果性质 + 记日志"，不产出任何面向界面的文案或样式。
//   横幅措辞与 CSS 类由页面根据 <see cref="DeviceOperationResult.FailureKind"/> 决定——
//   同一个失败在设备页和向导里可能要用不同说法，那是视图的事。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Host.Sdk;

namespace CommunicationKernel.UI.WebMaster.Services;

// ============================================================================
// 结果类型
// ============================================================================

/// <summary>
/// 一次设备操作的结果。
/// </summary>
/// <remarks>
/// 派生自 <see cref="HostOperationResult"/>，与 SDK 的失败形态保持同一形状，
/// 额外携带 <see cref="FailureKind"/> 供界面决定措辞——
/// "目标不可达"要提示检查现场，"配置错误"要提示去改参数，两者动作不同。
/// </remarks>
/// <param name="Success">操作是否成功。</param>
/// <param name="ErrorCode">错误码，仅在失败时有意义。</param>
/// <param name="ErrorMessage">面向操作员的错误描述。</param>
/// <param name="FailureKind">失败性质；成功时为 <see cref="RegisterFailureKind.None"/>。</param>
public sealed record DeviceOperationResult(
    bool Success,
    string ErrorCode,
    string ErrorMessage,
    RegisterFailureKind FailureKind)
    : HostOperationResult(Success, ErrorCode, ErrorMessage)
{
    /// <summary>构造成功结果。刻意不叫 Ok，避免遮蔽基类的 HostOperationResult.Ok()。</summary>
    public static DeviceOperationResult Succeeded() =>
        new(true, string.Empty, string.Empty, RegisterFailureKind.None);

    /// <summary>
    /// 由错误码构造失败结果，失败性质自动分类。
    /// </summary>
    public static DeviceOperationResult Failed(string code, string message) =>
        new(false,
            code ?? string.Empty,
            message ?? string.Empty,
            RegisterFailure.Classify(code));
}

// ============================================================================
// 契约
// ============================================================================

/// <summary>
/// Web 端设备操作契约。页面只依赖本接口，不直接接触 <see cref="HostClient"/>。
/// </summary>
public interface IWebDeviceService
{
    /// <summary>
    /// 按本地配置连接一台设备（向宿主注册路由并真正建链）。
    /// </summary>
    /// <param name="routeId">目标路由 ID，必须已存在于本地配置库。</param>
    /// <param name="ct">取消令牌。</param>
    Task<DeviceOperationResult> ConnectAsync(string routeId, CancellationToken ct = default);

    /// <summary>
    /// 断开一台设备（从宿主注销路由），本地配置保留。
    /// </summary>
    Task<DeviceOperationResult> DisconnectAsync(string routeId, CancellationToken ct = default);

    /// <summary>
    /// 为删除设备而注销路由。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="DisconnectAsync"/> 的差别只在容错：
    /// 宿主本就没有这条路由（RouteNotFound）时视为成功——目的是"确保它不在宿主上"，
    /// 而不是"必须亲手注销一次"。
    /// </remarks>
    Task<DeviceOperationResult> UnregisterForDeleteAsync(string routeId, CancellationToken ct = default);

    /// <summary>查询宿主当前加载的协议清单。</summary>
    Task<IReadOnlyList<ProtocolDescriptorDto>> GetProtocolsAsync(CancellationToken ct = default);

    /// <summary>查询宿主所在机器上可用的串口。</summary>
    Task<IReadOnlyList<SerialPortDto>> GetSerialPortsAsync(CancellationToken ct = default);
}

// ============================================================================
// 实现
// ============================================================================

/// <summary>
/// <see cref="IWebDeviceService"/> 的 gRPC 实现，作用域随 <see cref="HostSession"/>。
/// </summary>
public sealed class WebDeviceService : IWebDeviceService
{
    /// <summary>会话，提供当前的 <see cref="HostClient"/> 与在线状态。</summary>
    private readonly HostSession _session;

    /// <summary>本地设备配置库，连接时从中取参数。</summary>
    private readonly WebDeviceStore _store;

    /// <summary>应用日志。</summary>
    private readonly AppLogStore _log;

    /// <param name="session">会话服务，必填。</param>
    /// <param name="store">本地配置库，必填。</param>
    /// <param name="log">应用日志，必填。</param>
    public WebDeviceService(HostSession session, WebDeviceStore store, AppLogStore log)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _store   = store   ?? throw new ArgumentNullException(nameof(store));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    // ========================================================================
    // 连接 / 断开
    // ========================================================================

    /// <inheritdoc />
    public async Task<DeviceOperationResult> ConnectAsync(
        string routeId, CancellationToken ct = default)
    {
        // 连接参数一律取自本地配置库：页面上显示的行可能来自宿主快照，
        // 不含串口、波特率等仅存于本地的字段
        WebDeviceRecord? rec = _store.GetAll().FirstOrDefault(
            d => string.Equals(d.RouteId, routeId, StringComparison.OrdinalIgnoreCase));

        if (rec is null)
        {
            // 本地无配置说明列表与配置库不同步，属于配置问题而非现场问题
            _log.Warn("Devices", "连接失败：本地找不到 " + routeId + " 的配置");
            return DeviceOperationResult.Failed(
                "ConfigNotFound", "本地找不到 " + routeId + " 的配置");
        }

        RegisterRouteResultDto result = await _session.Client.RegisterRouteAsync(
            rec.RouteId, rec.ProtocolId, rec.TransportKind,
            rec.Address, rec.Port, rec.Station,
            rec.SerialPort, rec.BaudRate, rec.MinIoIntervalMs, ct).ConfigureAwait(false);

        if (result.Success)
        {
            _log.Info("Devices", "已连接 " + rec.RouteId);
            return DeviceOperationResult.Succeeded();
        }

        // 连接失败不动本地配置——设备还在列表里，稍后可重试
        _log.Warn("Devices",
            "连接 " + rec.RouteId + " 失败: [" + result.ErrorCode + "] " + result.ErrorMessage);
        return DeviceOperationResult.Failed(result.ErrorCode, result.ErrorMessage);
    }

    /// <inheritdoc />
    public async Task<DeviceOperationResult> DisconnectAsync(
        string routeId, CancellationToken ct = default)
    {
        RemoveRouteResultDto result =
            await _session.Client.RemoveRouteAsync(routeId, ct).ConfigureAwait(false);

        if (result.Success)
        {
            _log.Info("Devices", "已断开 " + routeId);
            return DeviceOperationResult.Succeeded();
        }

        _log.Warn("Devices",
            "断开 " + routeId + " 失败: [" + result.ErrorCode + "] " + result.ErrorMessage);
        return DeviceOperationResult.Failed(result.ErrorCode, result.ErrorMessage);
    }

    /// <inheritdoc />
    public async Task<DeviceOperationResult> UnregisterForDeleteAsync(
        string routeId, CancellationToken ct = default)
    {
        // 宿主离线时无需注销：本来就没有活动路由，直接放行让调用方删本地配置
        if (!_session.Online)
            return DeviceOperationResult.Succeeded();

        RemoveRouteResultDto result =
            await _session.Client.RemoveRouteAsync(routeId, ct).ConfigureAwait(false);

        // 宿主本就没有这条路由，等价于"已经不在宿主上"，是想要的终态
        bool alreadyGone = string.Equals(
            result.ErrorCode, "RouteNotFound", StringComparison.OrdinalIgnoreCase);

        if (result.Success || alreadyGone)
            return DeviceOperationResult.Succeeded();

        _log.Warn("Devices",
            "注销 " + routeId + " 失败: [" + result.ErrorCode + "] " + result.ErrorMessage);
        return DeviceOperationResult.Failed(result.ErrorCode, result.ErrorMessage);
    }

    // ========================================================================
    // 宿主侧清单
    // ========================================================================

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProtocolDescriptorDto>> GetProtocolsAsync(
        CancellationToken ct = default) =>
        await _session.Client.QueryProtocolsAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SerialPortDto>> GetSerialPortsAsync(
        CancellationToken ct = default) =>
        await _session.Client.QuerySerialPortsAsync(ct).ConfigureAwait(false);
}
