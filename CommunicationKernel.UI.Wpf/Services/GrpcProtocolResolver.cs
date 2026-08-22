#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/GrpcProtocolResolver.cs
// 层级: UI 层 — WPF 服务实现
// 作用: IProtocolResolver 的 gRPC 实现。协议清单一律来自 Host.App，UI 不内置协议知识。
// 离线策略:
//       上一次成功获取的服务端清单缓存到本地 JSON，宿主不可达时用它填下拉框。
//       从未成功获取过时返回空列表，由界面提示用户检查连接。
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
            // gRPC 客户端必填，协议清单一律向 Host.App 查询
            _client = client ?? throw new ArgumentNullException(nameof(client));

            // 先加载本地缓存，保证设备编辑面板立刻有下拉内容
            LoadCache();

            // 后台拉取服务端实时清单，成功后覆盖缓存
            _ = Task.Run(() => RefreshAsync(CancellationToken.None));
        }

        /// <inheritdoc />
        public IList<ProtocolDescriptorDto> GetProtocols() => new List<ProtocolDescriptorDto>(_cached);

        /// <inheritdoc />
        public string SourceState => _sourceState;

        /// <inheritdoc />
        public ProtocolDescriptorDto FindById(string protocolId)
        {
            // 空 ID 无法匹配，编辑面板应保持未选中
            if (string.IsNullOrWhiteSpace(protocolId))
                return null;

            foreach (ProtocolDescriptorDto d in _cached)
            {
                // 忽略大小写比较 ProtocolId，还原已保存设备的协议下拉项
                if (string.Equals(d.ProtocolId, protocolId, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            // 清单中没有该 ID（插件已卸载或缓存过期）
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
                // 向 Host.App 拉取当前已加载的协议插件清单
                IReadOnlyList<ProtocolDescriptorDto> serverList =
                    await _client.QueryProtocolsAsync(ct).ConfigureAwait(false);

                if (serverList is { Count: > 0 })
                {
                    // 服务端有协议：整体替换缓存并标记为实时
                    _cached      = new List<ProtocolDescriptorDto>(serverList);
                    _sourceState = ProtocolSourceState.Live;
                    SaveCache(serverList);
                }
                else if (_cached.Count == 0)
                {
                    // 服务端可达但无插件，且本地无缓存：确实没有可用协议
                    _sourceState = ProtocolSourceState.Unavailable;
                }

                // 通知设备编辑面板刷新下拉框
                ProtocolsChanged?.Invoke();
            }
            catch (Exception)
            {
                // 拉取失败：保留现有清单（可能来自缓存），仅更新状态
                if (_cached.Count > 0 && _sourceState != ProtocolSourceState.Live)
                    _sourceState = ProtocolSourceState.Cached;

                // 即使失败也通知界面，以便显示「离线缓存」提示
                ProtocolsChanged?.Invoke();
            }
            finally
            {
                // 释放刷新闸门，允许下次手动重试
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }

        // ============================================================================
        // 本地缓存
        // ============================================================================

        private void LoadCache()
        {
            try
            {
                // 从未成功拉取过则没有缓存文件
                if (!File.Exists(CachePath))
                    return;

                // 读取并反序列化上次成功的协议清单
                string json = File.ReadAllText(CachePath, Encoding.UTF8);
                List<ProtocolDescriptorDto> loaded =
                    JsonSerializer.Deserialize<List<ProtocolDescriptorDto>>(json, JsonOpts);

                if (loaded is { Count: > 0 })
                {
                    // 有有效缓存：先给界面用，状态标为离线缓存
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
                // 确保缓存目录存在
                string dir = Path.GetDirectoryName(CachePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 先写临时文件再覆盖，避免写入中断留下半截 JSON
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
