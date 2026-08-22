using System.Collections.Generic;

namespace CommunicationKernel.Communication.Transport.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: ISerialPortEnumerator.cs
/// 层级: Communication.Transport / Abstractions
/// 作用: 由传输插件可选实现，枚举本机可用串口。
/// -----------------------------------------------------------------------------
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须由宿主侧枚举，而不是上位机自己列本机串口。</b>
/// 串口长在跑通讯的那台机器上。宿主部署在树莓派、上位机在办公室 PC 时，
/// 上位机列出的 COM1/COM2 是它自己的，与 PLC 毫无关系——
/// 选中后注册必然失败，且错误信息指向"打不开 COM1"，完全误导。
/// </para>
/// <para>
/// 这是<b>可选</b>接口：由具体传输工厂按需实现，宿主用类型判断发现它。
/// 加到 <see cref="ITransportFactory"/> 上会强迫 TCP 插件实现一个
/// 与自己无关的方法，也违背「宿主不持有具体介质知识」——
/// 宿主只知道"某个工厂可能会枚举点什么"，不知道那是串口。
/// </para>
/// </remarks>
public interface ISerialPortEnumerator {
    /// <summary>
    /// 枚举当前可用的串口。
    /// </summary>
    /// <returns>
    /// 串口描述集合；无可用串口时返回空集合，不返回 null，也不抛异常——
    /// 没有串口是正常状态（纯以太网现场），不是错误。
    /// </returns>
    IReadOnlyList<SerialPortDescriptor> ListPorts();
}

/// <summary>
/// 一个可用串口的描述。
/// </summary>
/// <param name="PortName">
/// 传给 <see cref="TransportEndpoint.SerialPort"/> 的设备名，
/// Windows 上形如 <c>COM3</c>，Linux 上形如 <c>/dev/ttyUSB0</c>。
/// </param>
/// <param name="Description">
/// 面向操作员的补充说明，可为空字符串。
/// Linux 上通常填 by-id 稳定路径——多个 USB 串口同时插着时，
/// ttyUSB 的编号会随枚举顺序在重启后对调，而接错 PLC 的代价远大于读不到数。
/// </param>
public readonly record struct SerialPortDescriptor(string PortName, string Description);
