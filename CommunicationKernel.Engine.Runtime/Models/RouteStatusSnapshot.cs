// -----------------------------------------------------------------------------
// 文件: RouteStatusSnapshot.cs
// 层级: Engine / Models
// -----------------------------------------------------------------------------

using CommunicationKernel.Core.Abstractions.Errors;

namespace CommunicationKernel.Engine.Runtime.Models;

/// <summary>某条路由在某一时刻的连接状态。</summary>
/// <remarks>
/// 时间戳语义是「状态变为当前值的时刻」，而非「最近一次 I/O 时刻」——
/// 状态仅在实际变化时才发布，未变化时保留原快照。
/// </remarks>
public sealed class RouteStatusSnapshot {
    /// <summary>路由标识。</summary>
    public required string RouteId { get; init; }

    /// <summary>是否在线。</summary>
    public bool Online { get; init; }

    /// <summary>离线时的错误码；在线时为 <see cref="KernelErrorCode.None"/>。</summary>
    public KernelErrorCode ErrorCode { get; init; }

    /// <summary>离线时的错误描述；在线时为空字符串。</summary>
    public string ErrorMessage { get; init; } = string.Empty;

    /// <summary>状态变为当前值的时刻（UTC）。</summary>
    public DateTimeOffset TimestampUtc { get; init; }
}
