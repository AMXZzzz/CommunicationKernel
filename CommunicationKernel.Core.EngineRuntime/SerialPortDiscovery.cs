// -----------------------------------------------------------------------------
// 文件: SerialPortDiscovery.cs
// 层级: Core.EngineRuntime
// 作用: 在传输工厂中寻找能枚举串口的插件，汇总宿主本机可用串口清单。
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Transport.Abstractions;

namespace CommunicationKernel.Core.EngineRuntime;

/// <summary>
/// 在传输工厂集合中寻找能枚举串口的工厂，并汇总其结果。
/// </summary>
/// <remarks>
/// 引擎不持有任何串口知识：它只知道"某些工厂可能实现了
/// <see cref="ISerialPortEnumerator"/>"，至于枚举出来的是 COM3 还是
/// /dev/ttyUSB0、怎么枚举的，全在插件内部。
/// 这与「协议解析只能在插件内」是同一条约束。
/// </remarks>
internal static class SerialPortDiscovery {

    /// <summary>
    /// 汇总所有实现了 <see cref="ISerialPortEnumerator"/> 的工厂枚举出的串口。
    /// </summary>
    /// <returns>
    /// 去重后的串口清单；没有工厂实现该接口（如纯以太网部署未装串口插件）
    /// 时返回空集合。
    /// </returns>
    internal static IReadOnlyList<SerialPortInfo> Enumerate(
        IReadOnlyList<ITransportFactory> transportFactories) {

        List<SerialPortInfo>? result = null;
        HashSet<string>? seen = null;

        foreach (ITransportFactory factory in transportFactories) {
            // 只有实现了枚举接口的工厂（通常是串口插件）才参与；TCP 工厂直接跳过
            if (factory is not ISerialPortEnumerator enumerator) continue;

            // 单个插件枚举失败不应让整份清单变空——纯以太网现场照样要能配设备。
            IReadOnlyList<SerialPortInfo> ports;
            try {
                ports = enumerator.ListPorts();
            } catch (Exception) {
                continue;
            }

            // 该工厂当前没有可用串口（未插 USB 转串口等），不分配去重集合
            if (ports is null || ports.Count == 0) continue;

            // 惰性分配：现场一个串口都没有时避免无意义的 List/HashSet
            result ??= new List<SerialPortInfo>();
            seen   ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 去重：同一物理串口可能被多个工厂各报一次
            foreach (SerialPortInfo port in ports) {
                // 空端口名无法回填到注册命令，丢弃
                if (string.IsNullOrWhiteSpace(port.PortName)) continue;
                if (seen.Add(port.PortName)) result.Add(port);
            }
        }

        // 没有任何工厂实现枚举接口时返回空数组，UI 串口下拉框显示为空
        return (IReadOnlyList<SerialPortInfo>?)result ?? Array.Empty<SerialPortInfo>();
    }
}
