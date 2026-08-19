using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core.Interfaces;

namespace CommunicationDebuggingTools.Tests.Fakes {
    /// <summary>可注入的端口探测假实现。</summary>
    public sealed class FakeTcpProbe : ITcpProbe {
        public bool Result { get; set; } = true;
        public int CallCount { get; private set; }

        public Task<bool> IsPortOpenAsync (
            string ip, int port, int timeoutMs, CancellationToken cancellationToken) {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }
}
