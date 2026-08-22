#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/GrpcProtocolResolver.cs
// 层级: UI 层 — 服务实现
// 作用: IProtocolResolver 的 gRPC 实现。协议清单一律来自 Host.App，
//       UI 层不内置任何协议知识。
// 离线策略:
//       上一次成功获取的服务端清单被缓存到本地 JSON 文件，
//       宿主不可达时用它填充下拉框，并把状态标记为「离线缓存」。
//       从未成功获取过时返回空列表，由界面提示用户检查连接。
// 为什么不硬编码兜底列表:
//       硬编码等于把协议 ID、展示名、介质、站号需求全部复制到 UI 源码里，
//       新增一个协议插件就要改 UI 并重新发布客户端，插件架构的收益被抵消；
//       且两份清单必然漂移（历史上就发生过 UI 用展示名当 ProtocolId 回传，
//       导致服务端匹配不到工厂、每次添加设备都静默失败）。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.UI.Wpf.Core.Interfaces;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// <see cref="IProtocolResolver"/> 的 gRPC 实现。
    /// </summary>
    public sealed class GrpcProtocolResolver : IProtocolResolver
    {
        /// <summary>协议清单本地缓存路径，与 settings.json 同目录。</summary>
        private static readonly string CachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CommunicationKernel", "protocols.cache.json");

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private readonly HostClient _client;

        /// <summary>
        /// 当前协议清单。多线程读写通过 volatile + 整体替换（copy-on-write）保证安全。
        /// </summary>
        private volatile List<ProtocolDescriptorDto> _cached = new();

        /// <summary>清单来源，供界面区分「实时」「离线缓存」「不可用」。</summary>
        private volatile string _sourceState = ProtocolSourceState.Unavailable;

        /// <summary>防止并发刷新叠加。</summary>
        private int _refreshing;

        /// <inheritdoc />
        public event Action ProtocolsChanged;

        /// <param name="client">已初始化的 gRPC 客户端。</param>
        public GrpcProtocolResolver(HostClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));

            // 先加载本地缓存，保证界面立即有内容可显示
            LoadCache();

            // 后台拉取服务端实时清单
            _ = Task.Run(() => RefreshAsync(CancellationToken.None));
        }

        /// <inheritdoc />
        public IList<ProtocolDescriptorDto> GetProtocols() => new List<ProtocolDescriptorDto>(_cached);

        /// <inheritdoc />
        public string SourceState => _sourceState;

        /// <inheritdoc />
        public ProtocolDescriptorDto FindById(string protocolId)
        {
            if (string.IsNullOrWhiteSpace(protocolId))
                return null;

            foreach (ProtocolDescriptorDto d in _cached)
            {
                if (string.Equals(d.ProtocolId, protocolId, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        /// <inheritdoc />
        public async Task RefreshAsync(CancellationToken ct)
        {
            // 已有刷新在途则直接返回，避免重复请求
            if (Interlocked.Exchange(ref _refreshing, 1) == 1)
                return;

            try
            {
                IReadOnlyList<ProtocolDescriptorDto> serverList =
                    await _client.QueryProtocolsAsync(ct).ConfigureAwait(false);

                if (serverList is { Count: > 0 })
                {
                    _cached      = new List<ProtocolDescriptorDto>(serverList);
                    _sourceState = ProtocolSourceState.Live;
                    SaveCache(serverList);
                }
                else if (_cached.Count == 0)
                {
                    // 服务端可达但无插件，且本地无缓存：确实没有可用协议
                    _sourceState = ProtocolSourceState.Unavailable;
                }

                ProtocolsChanged?.Invoke();
            }
            catch (Exception)
            {
                // 拉取失败：保留现有清单（可能来自缓存），仅更新状态
                if (_cached.Count > 0 && _sourceState != ProtocolSourceState.Live)
                    _sourceState = ProtocolSourceState.Cached;

                ProtocolsChanged?.Invoke();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }

        // =====================================================================
        // 本地缓存
        // =====================================================================

        private void LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath))
                    return;

                string json = File.ReadAllText(CachePath, Encoding.UTF8);
                List<ProtocolDescriptorDto> loaded =
                    JsonSerializer.Deserialize<List<ProtocolDescriptorDto>>(json, JsonOpts);

                if (loaded is { Count: > 0 })
                {
                    _cached      = loaded;
                    _sourceState = ProtocolSourceState.Cached;
                }
            }
            catch (Exception)
            {
                // 缓存损坏或版本不兼容：忽略，等待服务端实时清单
            }
        }

        private static void SaveCache(IReadOnlyList<ProtocolDescriptorDto> protocols)
        {
            try
            {
                string dir = Path.GetDirectoryName(CachePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string tmp = CachePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(protocols, JsonOpts), Encoding.UTF8);
                File.Move(tmp, CachePath, overwrite: true);
            }
            catch (Exception)
            {
                // 缓存写入失败不影响本次会话可用性
            }
        }
    }

    /// <summary>协议清单的来源状态。</summary>
    public static class ProtocolSourceState
    {
        /// <summary>来自服务端实时查询。</summary>
        public const string Live = "Live";

        /// <summary>来自本地缓存，服务端当前不可达。</summary>
        public const string Cached = "Cached";

        /// <summary>无任何可用清单。</summary>
        public const string Unavailable = "Unavailable";
    }
}
