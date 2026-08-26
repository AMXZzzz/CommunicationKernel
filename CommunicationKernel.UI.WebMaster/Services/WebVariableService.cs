// -----------------------------------------------------------------------------
// 文件: Services/WebVariableService.cs
// 层级: UI 层 — Web 服务
// 作用: 变量的读取与写入，负责按设备配置的字节序编解码。
// 调用链:
//   VariablesPage.razor / VariablePoller → IWebVariableService → HostClient → gRPC
//
// 为什么要有这个类:
//   读写此前散落在两处且<b>行为不一致</b>——
//   VariablesPage 解码时传了设备配置的字节序，VariablePoller 没传（取默认 ABCD）。
//   于是同一个配置成 CDAB 的设备，手动读一个值、后台轮询列显示另一个值，
//   而两边都报成功，没有任何报错提示这件事。
//   把"取字节序 → 调 gRPC → 编解码"收进一个方法后，这类分叉在结构上就不可能再发生。
//
// 职责边界:
//   本服务返回已解码的显示值与结构化的失败信息，不产出横幅文案或 CSS 类。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Host.Sdk;

namespace CommunicationKernel.UI.WebMaster.Services;

// ============================================================================
// 结果类型
// ============================================================================

/// <summary>
/// 一次变量读取的结果。
/// </summary>
/// <param name="Success">读取是否成功。</param>
/// <param name="ErrorCode">错误码，仅在失败时有意义。</param>
/// <param name="ErrorMessage">面向操作员的错误描述。</param>
/// <param name="DisplayValue">
/// 已按设备字节序解码的显示值；失败时为空字符串。
/// </param>
public sealed record VariableReadOutcome(
    bool Success, string ErrorCode, string ErrorMessage, string DisplayValue)
    : HostOperationResult(Success, ErrorCode, ErrorMessage);

// ============================================================================
// 契约
// ============================================================================

/// <summary>
/// Web 端变量读写契约。页面与后台轮询器都只依赖本接口。
/// </summary>
public interface IWebVariableService
{
    /// <summary>
    /// 读取一个变量并按其所属设备的字节序解码。
    /// </summary>
    Task<VariableReadOutcome> ReadAsync(WebVariable row, CancellationToken ct = default);

    /// <summary>
    /// 把 <see cref="WebVariable.WriteText"/> 按设备字节序编码后写入。
    /// </summary>
    /// <remarks>
    /// 编码失败（例如把 "abc" 当 Int16）不会发起任何 I/O，
    /// 直接返回 <c>PARSE_ERROR</c>。
    /// </remarks>
    Task<HostOperationResult> WriteAsync(WebVariable row, CancellationToken ct = default);

    /// <summary>
    /// 查询某条路由所配置的字节序，用于界面提示。
    /// </summary>
    /// <remarks>
    /// 路由不存在或未配置时返回 <see cref="ByteOrder.ABCD"/>——
    /// 那是所有协议插件上抛数据的原始排列。
    /// </remarks>
    ByteOrder OrderOf(string routeId);
}

// ============================================================================
// 实现
// ============================================================================

/// <summary>
/// <see cref="IWebVariableService"/> 的 gRPC 实现。
/// </summary>
public sealed class WebVariableService : IWebVariableService
{
    /// <summary>会话，提供 <see cref="HostClient"/>。</summary>
    private readonly HostSession _session;

    /// <summary>设备配置库，字节序的来源。</summary>
    private readonly WebDeviceStore _devices;

    /// <param name="session">会话服务，必填。</param>
    /// <param name="devices">设备配置库，必填。</param>
    public WebVariableService(HostSession session, WebDeviceStore devices)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    /// <inheritdoc />
    public ByteOrder OrderOf(string routeId) =>
        ValueCodec.ParseOrder(_devices.Get(routeId)?.ByteOrder);

    /// <inheritdoc />
    public async Task<VariableReadOutcome> ReadAsync(
        WebVariable row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        // 长度至少 1：0 会被部分协议判为非法请求而不是"读 0 个字节"
        ReadResultDto result = await _session.Client
            .ReadAsync(row.RouteId, row.Address, Math.Max(1, row.Length), ct)
            .ConfigureAwait(false);

        if (!result.Success)
            return new VariableReadOutcome(
                false, result.ErrorCode, result.ErrorMessage, string.Empty);

        // 字节序必须在这里取：调用方（页面或轮询器）不该各自记得要传这个参数——
        // 之前正是因为轮询器忘了传，才出现两处显示值不一致
        string display = ValueCodec.Decode(
            result.Data ?? Array.Empty<byte>(), row.DataType, OrderOf(row.RouteId));

        return new VariableReadOutcome(true, string.Empty, string.Empty, display);
    }

    /// <inheritdoc />
    public async Task<HostOperationResult> WriteAsync(
        WebVariable row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        // 先编码再发起 I/O：格式错误就没必要打扰 PLC
        if (!ValueCodec.TryEncode(
                row.WriteText, row.DataType, row.Length,
                out byte[] data, out string err, OrderOf(row.RouteId)))
        {
            return HostOperationResult.Fail("PARSE_ERROR", err);
        }

        return await _session.Client
            .WriteAsync(row.RouteId, row.Address, data, ct)
            .ConfigureAwait(false);
    }
}
