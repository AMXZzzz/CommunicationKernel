#nullable disable

// -----------------------------------------------------------------------------
// 文件: HostResults.cs
// 层级: 客户端层 — Host.Sdk 对外结果契约
// 作用: 定义 Host.Sdk 全部方法的返回形态。
//
// 为什么单独成文件、且一律用具名 record：
//   Host.Sdk 是要发 NuGet 的对外契约。此前 RegisterRouteAsync / RemoveRouteAsync /
//   HealthAsync 返回匿名 ValueTuple，带来三个具体问题：
//     1) 调用方只能靠<b>位置</b>记忆字段含义，(bool, string, string, string)
//        里哪个是 ErrorCode、哪个是 RouteId 全凭记忆；
//     2) 元组无法承载 XML 文档注释，IntelliSense 里是光秃秃的 Item1/Item2；
//     3) 加一个字段就是破坏性变更——元组元数比较是结构性的，
//        而 record 加可选属性不会打断既有调用方。
//
// 统一的失败形态:
//   所有「可能失败的操作」共享 HostOperationResult 的三字段形状
//   (Success / ErrorCode / ErrorMessage)，带载荷的结果在其上追加字段。
//   这样跨层传递时不再需要把一种结果手工翻译成另一种——翻译处正是语义流失的地方。
// -----------------------------------------------------------------------------

using System;

namespace CommunicationKernel.Host.Sdk
{
    // ========================================================================
    // 统一失败形态
    // ========================================================================

    /// <summary>
    /// Host.Sdk 所有可失败操作的公共结果形状。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本类型及其派生类型一律以「返回结果」而非「抛异常」表达业务失败：
    /// 传输异常在 <see cref="HostClient"/> 内部就被转换成
    /// <c>RPC_ERROR</c> / <c>TIMEOUT</c> 等错误码，调用方无需为每次读写包 try/catch。
    /// </para>
    /// <para>
    /// <b>不要再在 UI 层定义同形状的类型。</b>UI.Wpf 曾有一个字段完全相同的
    /// OperationResult，导致 <c>LocalVariableStore</c> 里出现纯粹的重新包装
    /// （把 WriteResultDto 拆开再装进另一个对象）。那种翻译除了制造出错机会
    /// 没有任何收益，已删除。
    /// </para>
    /// </remarks>
    /// <param name="Success">操作是否成功。</param>
    /// <param name="ErrorCode">错误码，仅在失败时有意义；成功时为空字符串。</param>
    /// <param name="ErrorMessage">面向操作员的错误描述；成功时为空字符串。</param>
    public record HostOperationResult(bool Success, string ErrorCode, string ErrorMessage)
    {
        /// <summary>构造一个成功结果，错误字段置空。</summary>
        public static HostOperationResult Ok() =>
            new(true, string.Empty, string.Empty);

        /// <summary>构造一个失败结果。</summary>
        /// <param name="code">错误码，例如 "NOT_FOUND"、"PARSE_ERROR"。</param>
        /// <param name="message">面向操作员的描述。</param>
        public static HostOperationResult Fail(string code, string message) =>
            new(false, code ?? string.Empty, message ?? string.Empty);
    }

    // ========================================================================
    // 健康检查
    // ========================================================================

    /// <summary>
    /// 健康检查结果。
    /// </summary>
    /// <remarks>
    /// 这里不复用 <see cref="HostOperationResult"/>：健康检查探测的是「宿主是否可达」，
    /// 不可达本身就是一次正常的探测结论而非操作失败，因此没有 ErrorCode 语义。
    /// 网络异常时返回 <c>Ok = false</c> 而非抛出。
    /// </remarks>
    /// <param name="Ok">宿主是否响应。</param>
    /// <param name="HostVersion">宿主版本号；不可达时为空字符串。</param>
    /// <param name="RouteCount">宿主当前登记的路由条数；不可达时为 0。</param>
    public sealed record HealthResultDto(bool Ok, string HostVersion, int RouteCount)
    {
        /// <summary>宿主不可达时的标准结果。</summary>
        public static HealthResultDto Offline() =>
            new(false, string.Empty, 0);
    }

    // ========================================================================
    // 路由生命周期
    // ========================================================================

    /// <summary>
    /// 注册路由的结果。
    /// </summary>
    /// <param name="Success">注册是否成功。</param>
    /// <param name="ErrorCode">错误码，仅在失败时有意义。</param>
    /// <param name="ErrorMessage">面向操作员的错误描述。</param>
    /// <param name="RouteId">
    /// 宿主最终采用的路由 ID。
    /// 可能与请求中的 ID 不同——宿主有权规范化（去空白等），
    /// 后续读写必须以本字段为准，不要沿用请求里那个。
    /// </param>
    public sealed record RegisterRouteResultDto(
        bool Success, string ErrorCode, string ErrorMessage, string RouteId)
        : HostOperationResult(Success, ErrorCode, ErrorMessage);

    /// <summary>注销路由的结果。</summary>
    /// <param name="Success">注销是否成功。</param>
    /// <param name="ErrorCode">错误码，仅在失败时有意义。</param>
    /// <param name="ErrorMessage">面向操作员的错误描述。</param>
    public sealed record RemoveRouteResultDto(
        bool Success, string ErrorCode, string ErrorMessage)
        : HostOperationResult(Success, ErrorCode, ErrorMessage);

    // ========================================================================
    // 读写
    // ========================================================================

    /// <summary>读取结果。</summary>
    /// <param name="Success">读取是否成功。</param>
    /// <param name="ErrorCode">错误码，仅在失败时有意义。</param>
    /// <param name="ErrorMessage">面向操作员的错误描述。</param>
    /// <param name="Data">
    /// 原始字节，<b>大端序</b>（所有协议插件统一以大端序上抛）。
    /// 转换成具体数据类型请走 <see cref="ValueCodec"/>，不要自行 BitConverter——
    /// 本机是小端序，直接转会得到字节颠倒的值。
    /// </param>
    public sealed record ReadResultDto(
        bool Success, string ErrorCode, string ErrorMessage, byte[] Data)
        : HostOperationResult(Success, ErrorCode, ErrorMessage);

    /// <summary>写入结果。</summary>
    /// <param name="Success">写入是否成功。</param>
    /// <param name="ErrorCode">错误码，仅在失败时有意义。</param>
    /// <param name="ErrorMessage">面向操作员的错误描述。</param>
    public sealed record WriteResultDto(
        bool Success, string ErrorCode, string ErrorMessage)
        : HostOperationResult(Success, ErrorCode, ErrorMessage);
}
