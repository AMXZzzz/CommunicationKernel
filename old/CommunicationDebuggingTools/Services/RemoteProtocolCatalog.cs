using CommunicationDebuggingTools.Client;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CommunicationDebuggingTools.Services {

    /// <summary>
    /// 远程协议目录：通过 EngineHost 获取可用协议名称列表。
    /// WPF 不再本地加载协议插件；Resolve 恒为 null（协议实例只存在于 EngineHost 进程内）。
    /// </summary>
    public sealed class RemoteProtocolCatalog : IProtocolResolver {

        private readonly EngineClient _client;
        private readonly IAppLogger _log;
        private volatile IList<string> _names = new List<string>();

        public RemoteProtocolCatalog (EngineClient client, IAppLogger log = null) {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _log = log;
        }

        /// <summary>纯远程架构下无本地插件目录，忽略。</summary>
        public void LoadFromFolder (string folder) { }

        /// <summary>协议实例仅存在于 EngineHost 内，客户端不可解析。</summary>
        public IProtocol Resolve (string protocolName) => null;

        public IList<string> GetProtocolNames () => _names;

        /// <summary>从 EngineHost 拉取最新协议列表并缓存（连通后调用）。</summary>
        public async Task RefreshAsync (CancellationToken ct = default) {
            try {
                IReadOnlyList<string> names = await _client.ListProtocolsAsync(ct).ConfigureAwait(false);
                _names = new List<string>(names);
                _log?.Info("Protocol", "已从 EngineHost 获取协议 " + _names.Count + " 个");
            } catch (Exception ex) {
                _log?.Warn("Protocol", "获取远程协议列表失败：" + ex.Message);
            }
        }
    }
}
