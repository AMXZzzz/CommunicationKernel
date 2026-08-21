using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core.Interfaces;

namespace CommunicationDebuggingTools.Business.Device {
    /// <summary>
    /// 默认 TCP 端口探测：尝试 ConnectAsync，成功则立即关闭。
    /// </summary>
    public sealed class TcpProbe : ITcpProbe {
        public async Task<bool> IsPortOpenAsync (
            string ip,
            int port,
            int timeoutMs,
            CancellationToken cancellationToken) {

            if (string.IsNullOrWhiteSpace(ip) || port <= 0)
                return false;

            if (timeoutMs < 200)
                timeoutMs = 200;

            TcpClient client = null;
            try {
                client = new TcpClient();
                Task connectTask = client.ConnectAsync(ip.Trim(), port);
                Task delayTask = Task.Delay(timeoutMs, cancellationToken);
                Task finished = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);

                if (finished != connectTask) {
                    try { client.Close(); } catch { }
                    return false;
                }

                await connectTask.ConfigureAwait(false);
                return client.Connected;
            } catch (OperationCanceledException) {
                throw;
            } catch {
                return false;
            } finally {
                try {
                    if (client != null)
                        client.Close();
                } catch { }
            }
        }
    }
}
