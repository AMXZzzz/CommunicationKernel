using System.Threading;
using System.Threading.Tasks;

namespace CommunicationDebuggingTools.Core.Interfaces {
    /// <summary>
    /// TCP 端口可达性探测（连接前预检）。
    /// 实现可替换，便于单测注入，避免依赖真实网络。
    /// </summary>
    public interface ITcpProbe {
        /// <summary>探测 ip:port 是否可在超时内建立 TCP。</summary>
        Task<bool> IsPortOpenAsync (string ip, int port, int timeoutMs, CancellationToken cancellationToken);
    }
}
