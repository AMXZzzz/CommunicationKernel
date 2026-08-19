using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Core.Interfaces {

    /// <summary>
    /// 通信协议插件唯一对外契约，继承 IDisposable 保证资源确定性释放。
    ///
    /// 协议显示名称现由 <see cref="Attributes.ProtocolNameAttribute"/> 标注在实现类上，
    /// 不再通过 GetProtocolName() 方法暴露——ProtocolResolver 反射 Attribute 完成注册，
    /// 无需创建临时实例。
    ///
    /// 生命周期：ConnectAsync → [ReadAsync/WriteAsync/PingAsync ...] → Disconnect → Dispose
    /// Disconnect：关闭当前会话，对象可再次 ConnectAsync。
    /// Dispose   ：最终销毁，内部调 Disconnect 后释放所有托管/非托管资源。
    /// </summary>
    public interface IProtocol : IDisposable {

        bool IsConnected { get; }

        Task<bool> ConnectAsync (
            ProtocolConnectionContext context,
            CancellationToken cancellationToken);

        void Disconnect ();

        Task<bool> PingAsync (CancellationToken cancellationToken);

        Task<ProtocolDataMessage> ReadAsync (
            ProtocolDataMessage request,
            CancellationToken cancellationToken);

        Task<ProtocolDataMessage> WriteAsync (
            ProtocolDataMessage request,
            CancellationToken cancellationToken);
    }
}