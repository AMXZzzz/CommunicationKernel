// -----------------------------------------------------------------------------
// 文件: RegisterFailureClassifyTests.cs
// 层级: 测试
// 作用: 钉住「注册失败之后，本地配置什么时候可以删」。
//
// 为什么这件事值得单独用测试锁住：
//   分类错了不会有任何报错，只会在某个现场把操作员刚录的设备<b>悄悄删掉</b>。
//   而删除发生在 GrpcDeviceService 的失败分支里，只有真的踩中那个错误码
//   才会执行到——自测环境几乎不可能覆盖。
//
// 已经踩过的那次：
//   TransportUnavailable（宿主侧 plugins/ 里没有对应传输插件 DLL）
//   此前落进 default 分支被判为 BadConfiguration，于是 _config.Delete(routeId)。
//   操作员填的参数一个字都没错，是部署不全；装好插件重启后，设备已经没了。
//
//   而引擎自己在三个地方都把这个码当作<b>暂态</b>：
//     EngineRuntime.ShouldAttemptReconnect  —— 要重连
//     ProtocolProbe                          —— 归为链路故障
//     RouteAssembler                         —— 产生它的地方本就是"插件没装"
//   两层对同一个码给出相反判断，结果是引擎正准备重试、UI 已经把配置删了。
// -----------------------------------------------------------------------------

using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Hosting.Sdk;

namespace CommunicationKernel.Tests;

[TestClass]
public class RegisterFailureClassifyTests {

    // =========================================================================
    // 必须保留配置的
    // =========================================================================

    [TestMethod]
    [DataRow("TransportIoError", DisplayName = "建连失败：PLC 未上电 / 网关未接 / 串口被占")]
    [DataRow("Timeout",          DisplayName = "握手超时")]
    [DataRow("RPC_ERROR",        DisplayName = "宿主没起来（客户端自填码）")]
    [DataRow("TIMEOUT",          DisplayName = "RPC 超时（客户端自填码）")]
    public void TransientFailures_KeepConfiguration (string errorCode) {
        Assert.AreEqual(RegisterFailureKind.Unreachable, RegisterFailure.Classify(errorCode));
        Assert.IsTrue(RegisterFailure.ShouldKeepConfiguration(errorCode),
            errorCode + " 是暂态失败，删掉配置等于让操作员为现场状态买单");
    }

    [TestMethod]
    public void TransportUnavailable_KeepsConfiguration () {
        // 宿主 plugins/ 里缺传输插件 DLL 时 RouteAssembler 报的就是这个码。
        // 配置本身完全正确，删它是纯粹的数据丢失。
        Assert.IsTrue(
            RegisterFailure.ShouldKeepConfiguration(
                KernelErrorCode.TransportUnavailable.ToString()),
            "插件没装是部署问题，不是配置问题：装好插件重启就该能用，" +
            "配置被删则要凭记忆重录");
    }

    [TestMethod]
    public void TransportUnavailable_ClassificationMatchesEngine () {
        // 与 EngineRuntime.ShouldAttemptReconnect / ProtocolProbe 保持同一判断。
        // 这条测试的意义在于：将来若有人把它改回 BadConfiguration，
        // 会立刻看到"引擎要重连、UI 要删配置"这个矛盾，而不是在现场发现。
        Assert.AreEqual(
            RegisterFailureKind.Unreachable,
            RegisterFailure.Classify(KernelErrorCode.TransportUnavailable.ToString()));
    }

    // =========================================================================
    // 必须清掉配置的
    // =========================================================================

    [TestMethod]
    [DataRow("ProtocolNotFound", DisplayName = "协议名拼错或插件不存在")]
    [DataRow("InvalidArgument",  DisplayName = "参数非法")]
    [DataRow("RouteConflict",    DisplayName = "路由键冲突")]
    public void ConfigurationFailures_DropConfiguration (string errorCode) {
        Assert.AreEqual(RegisterFailureKind.BadConfiguration, RegisterFailure.Classify(errorCode));
        Assert.IsFalse(RegisterFailure.ShouldKeepConfiguration(errorCode),
            errorCode + " 存下来只会得到一个永远起不来的条目，还会让对账循环一直重试");
    }

    // =========================================================================
    // 边界
    // =========================================================================

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    [DataRow("SomethingNobodyHasSeenBefore")]
    public void UnknownCode_FallsBackToBadConfiguration (string? errorCode) {
        // 保守方向刻意选"不保留"：宁可让操作员看到失败并重填，
        // 也不要把一条来路不明的配置塞进对账循环里无限重试。
        Assert.AreEqual(RegisterFailureKind.BadConfiguration, RegisterFailure.Classify(errorCode!));
    }

    [TestMethod]
    public void CodeIsTrimmed_LeadingAndTrailingWhitespaceTolerated () {
        // 错误码经 gRPC 传来，两端空白不该改变判断
        Assert.AreEqual(RegisterFailureKind.Unreachable, RegisterFailure.Classify("  Timeout  "));
    }
}
