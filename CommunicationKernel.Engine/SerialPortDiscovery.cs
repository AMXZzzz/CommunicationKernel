// -----------------------------------------------------------------------------
// 文件: SerialPortDiscovery.cs
// 层级: Engine
// -----------------------------------------------------------------------------

using CommunicationKernel.Communication.Transport.Abstractions;

namespace CommunicationKernel.Engine;

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
    internal static IReadOnlyList<SerialPortDescriptor> Enumerate(
        IReadOnlyList<ITransportFactory> transportFactories) {

        List<SerialPortDescriptor>? result = null;
        HashSet<string>? seen = null;

        foreach (ITransportFactory factory in transportFactories) {
            if (factory is not ISerialPortEnumerator enumerator) continue;

            // 单个插件枚举失败不应让整份清单变空——纯以太网现场照样要能配设备。
            IReadOnlyList<SerialPortDescriptor> ports;
            try {
                ports = enumerator.ListPorts();
            } catch (Exception) {
                continue;
            }

            if (ports is null || ports.Count == 0) continue;

            result ??= new List<SerialPortDescriptor>();
            seen   ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 去重：同一物理串口可能被多个工厂各报一次
            foreach (SerialPortDescriptor port in ports) {
                if (string.IsNullOrWhiteSpace(port.PortName)) continue;
                if (seen.Add(port.PortName)) result.Add(port);
            }
        }

        return (IReadOnlyList<SerialPortDescriptor>?)result ?? Array.Empty<SerialPortDescriptor>();
    }
}
