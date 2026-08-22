// -----------------------------------------------------------------------------
// 文件: FrameProbe.cs
// 层级: Communication.Transport / Abstractions
// 作用: 定义"帧完整性判定"回调，把分帧职责从传输层交还给协议层。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Communication.Transport.Abstractions;

/// <summary>
/// 帧完整性判定委托：传输层每收到一批新字节就调用一次，由协议决定这一帧是否已完整。
/// </summary>
/// <remarks>
/// <para>
/// 传输层每收到一批新字节就调用一次本委托，由协议决定这一帧是否已完整。
/// 这样传输层无需理解任何协议，协议也无需接触 socket 或串口句柄。
/// </para>
/// <para>
/// <b>为什么不能用"静默判帧"代替。</b>
/// 旧实现以"N 毫秒内无新字节即认为帧结束"切分响应。这在两个方向都会错：
/// </para>
/// <list type="bullet">
///   <item>
///     <b>切断</b>——响应跨 TCP 分段到达且间隔超过阈值（链路拥塞、PLC 忙、跨网段），
///     只拿到半帧，上层报"响应过短"。
///   </item>
///   <item>
///     <b>粘连</b>——两个响应在阈值内先后到达被合并成一个缓冲区，
///     解析器只认第一帧，第二帧被静默丢弃；此后该连接上请求与响应
///     <b>永久错位一格</b>，每次读回的都是上一次请求的数据，且全程 Success = true。
///   </item>
/// </list>
/// <para>
/// 本项目支持的五种协议全部具备确定性帧长：Modbus TCP 有 MBAP 长度字段、
/// Modbus RTU 可由功能码与字节数推定、Modbus ASCII 以 CRLF 收尾、
/// MEWTOCOL 以 CR 收尾、S7 有 TPKT 长度字段。因此没有任何协议需要靠时序猜测。
/// </para>
/// <para>
/// 另有一个现场场景使静默判帧更不可靠：Modbus RTU 经 TCP 转串口透传装置
/// （如 Moxa NPort、USR-TCP232）传输时，串口原本的 3.5 字符帧间静默
/// 在 TCP 侧不被保证保留，只能按长度分帧。
/// </para>
/// </remarks>
/// <param name="received">目前已累积收到的全部响应字节。</param>
/// <param name="totalLength">
/// 判定成功时输出该帧的<b>总</b>字节数（含已收到的部分）。
/// 该值可能小于 <paramref name="received"/> 的长度——多出的部分属于下一帧。
/// </param>
/// <returns>
/// <c>true</c> 表示已能确定帧长，传输层据此续读或截断；
/// <c>false</c> 表示信息不足，需要继续读取。
/// </returns>
public delegate bool TryGetFrameLength(ReadOnlySpan<byte> received, out int totalLength);
