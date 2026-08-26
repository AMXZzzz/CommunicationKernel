#nullable disable

// -----------------------------------------------------------------------------
// 文件: RegisterFailureKind.cs
// 层级: 客户端层 — 注册失败的分类
// 作用: 区分「配置本身有问题」与「配置没问题、只是现在连不上」。
//
// 为什么需要这个区分：
//   注册路由会真的去建连接。现场调试时 PLC 常常还没上电、
//   Modbus RTU 用的 TCP 转串口网关还没接、树莓派还没进产线——
//   这些情况下连接必然失败，但操作员填的配置是完全正确的。
//   若一律当成失败丢弃，就变成「PLC 不通电就没法录设备」，
//   而这恰恰是调试期最常见的状态。
//
//   反过来，协议名拼错、介质不支持这类错误，配置本身就是坏的，
//   存下来只会得到一个永远起不来的条目，还会让对账循环一直重试。
// -----------------------------------------------------------------------------

using System;

namespace CommunicationKernel.EngineHost.Sdk
{
    /// <summary>注册路由失败的性质。</summary>
    public enum RegisterFailureKind
    {
        /// <summary>没有失败。</summary>
        None = 0,

        /// <summary>
        /// 配置有效，但目标当前不可达（PLC 未上电、网关未接、网线未插）。
        /// 配置应当保留，等目标可达后由对账自动补注册。
        /// </summary>
        Unreachable = 1,

        /// <summary>
        /// 配置本身有问题（协议不存在、介质不支持、参数非法）。
        /// 不应保留——存下来只会得到一个永远起不来的条目。
        /// </summary>
        BadConfiguration = 2,
    }

    /// <summary>把服务端返回的错误码归类，供 UI 决定是否保留配置。</summary>
    public static class RegisterFailure
    {
        /// <summary>
        /// 依据 RegisterRoute 返回的 error_code 判断失败性质。
        /// </summary>
        /// <param name="errorCode">
        /// 服务端填的错误码字面量，取自 <c>KernelErrorCode.ToString()</c>。
        /// </param>
        /// <remarks>
        /// 无法识别的错误码一律归为 <see cref="RegisterFailureKind.BadConfiguration"/>：
        /// 宁可让操作员看到失败并重填，也不要把一条来路不明的配置塞进对账循环里反复重试。
        /// </remarks>
        public static RegisterFailureKind Classify(string errorCode)
        {
            if (string.IsNullOrWhiteSpace(errorCode))
                return RegisterFailureKind.BadConfiguration;

            switch (errorCode.Trim())
            {
                // 建连接失败 / 握手超时：配置没问题，是目标此刻不可达
                case "TransportIoError":
                case "Timeout":
                    return RegisterFailureKind.Unreachable;

                // RPC 层面的失败（宿主没起来、网络不通）同样属于「现在连不上」。
                // 这两个是客户端自己填的码，不是服务端的 KernelErrorCode。
                case "RPC_ERROR":
                case "TIMEOUT":
                    return RegisterFailureKind.Unreachable;

                default:
                    return RegisterFailureKind.BadConfiguration;
            }
        }

        /// <summary>该失败是否应当保留本地配置。</summary>
        public static bool ShouldKeepConfiguration(string errorCode)
            => Classify(errorCode) == RegisterFailureKind.Unreachable;
    }
}
