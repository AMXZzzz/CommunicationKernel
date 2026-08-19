// -----------------------------------------------------------------------------
// 文件: KernelRuntime.cs
// 层级: Core.Runtime
// 作用: 内核运行时公共工具库（占位，待后续扩展）。
// 规划:
//   本项目将承载所有层共用的非抽象运行时工具，例如：
//   - TimeProvider 封装（测试可注入假时间）
//   - 统一日志门面（避免各层直接依赖具体日志框架）
//   - 诊断埋点基础设施（ActivitySource、Metrics）
//   - 配置绑定公共基类
//   目前仅定义占位类，不含任何运行逻辑。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Core.Runtime;

/// <summary>
/// 内核运行时版本与诊断工具占位入口。
/// </summary>
public static class KernelRuntime
{
    /// <summary>内核运行时包版本。</summary>
    public const string Version = "1.0.0";
}
