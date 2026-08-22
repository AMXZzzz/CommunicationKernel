#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Models/OperationResult.cs
// 层级: UI 层 — WPF 核心模型
// 作用: 统一操作结果封装，避免服务接口用异常表示业务失败。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.Wpf.Core.Models
{
    /// <summary>
    /// 通用操作结果，封装成功/失败标志及错误信息。
    /// 不可变对象，通过静态工厂方法 <see cref="Ok"/> / <see cref="Fail"/> 创建。
    /// </summary>
    public sealed class OperationResult
    {
        /// <summary>操作是否成功。true = 成功，false = 失败。</summary>
        public bool Success { get; }

        /// <summary>错误码，仅在 Success 为 false 时有意义。成功时为空字符串。</summary>
        public string ErrorCode { get; }

        /// <summary>错误描述，仅在 Success 为 false 时有意义。成功时为空字符串。</summary>
        public string ErrorMessage { get; }

        /// <summary>
        /// 创建表示成功的操作结果。
        /// ErrorCode 和 ErrorMessage 均为空字符串。
        /// </summary>
        /// <returns>成功的 OperationResult 实例。</returns>
        public static OperationResult Ok()
        {
            // 返回成功结果，错误字段置空
            return new OperationResult(true, string.Empty, string.Empty);
        }

        /// <summary>
        /// 创建表示失败的操作结果。
        /// </summary>
        /// <param name="code">错误码，例如 "RPC_ERROR"、"PARSE_ERROR"。</param>
        /// <param name="msg">错误描述，提供给 UI 显示的友好提示。</param>
        /// <returns>失败的 OperationResult 实例。</returns>
        public static OperationResult Fail(string code, string msg)
        {
            // 返回失败结果，携带错误码和描述供变量写入页弹出
            return new OperationResult(false, code, msg);
        }

        /// <summary>
        /// 私有构造函数，强制通过工厂方法创建，保证对象语义正确。
        /// </summary>
        private OperationResult(bool ok, string code, string msg)
        {
            // 初始化所有不可变字段
            Success      = ok;
            ErrorCode    = code;
            ErrorMessage = msg;
        }
    }
}
