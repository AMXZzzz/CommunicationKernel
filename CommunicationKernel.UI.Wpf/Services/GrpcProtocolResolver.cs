// -----------------------------------------------------------------------------
// 文件: Services/GrpcProtocolResolver.cs
// 层级: UI 层 — 服务实现
// 作用: IProtocolResolver 的 gRPC 实现。
//       启动时向 EngineHost 发起 QueryProtocols 请求，
//       将服务端实际加载的协议插件 ID 列表缓存到本地；
//       服务端不可达（Unimplemented / 网络错误）时自动回退到硬编码兜底列表，
//       保证设备添加对话框的协议下拉框在离线状态下仍可正常显示。
// 调用链:
//   App.xaml.cs → DI → GrpcProtocolResolver(EngineHostGrpcClient)
//     → 构造时 Task.Run 后台拉取 QueryProtocols → _cachedProtocols 更新
//   DevicePage → IProtocolResolver.GetProtocolNames() → 返回缓存列表
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunicationKernel.UI.Wpf.Core.Interfaces;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// <see cref="IProtocolResolver"/> 的 gRPC 实现。
    /// 优先从 EngineHost.QueryProtocols 获取已加载协议列表；
    /// 服务端未实现或离线时回退到硬编码兜底列表。
    /// </summary>
    public sealed class GrpcProtocolResolver : IProtocolResolver
    {
        // =========================================================================
        // 兜底列表 — 服务端不可达时使用，与现有插件 DLL 对应
        // =========================================================================

        /// <summary>
        /// 硬编码兜底协议列表，按常用频率排序。
        /// 服务端 QueryProtocols 成功后此列表会被服务端结果覆盖。
        /// </summary>
        private static readonly List<string> FallbackProtocols = new List<string>
        {
            "Modbus TCP",
            "Modbus RTU",
            "Siemens S7-1200",
            "Panasonic Mewtocol TCP",
        };

        // =========================================================================
        // 私有字段
        // =========================================================================

        /// <summary>gRPC 客户端，用于调用 QueryProtocols。</summary>
        private readonly EngineHostGrpcClient _client;

        /// <summary>
        /// 缓存的协议列表。
        /// 初始值为兜底列表；QueryProtocols 成功后替换为服务端结果。
        /// 多线程读写通过 volatile + 整体替换（copy-on-write）保证安全，
        /// 无需加锁（List 本身不可变，只替换引用）。
        /// </summary>
        private volatile List<string> _cachedProtocols;

        // =========================================================================
        // 构造函数
        // =========================================================================

        /// <summary>
        /// 初始化并立即在后台发起 QueryProtocols 请求。
        /// 构造完成后 <see cref="GetProtocolNames"/> 即可安全调用（返回兜底列表）。
        /// </summary>
        /// <param name="client">已初始化的 gRPC 客户端。</param>
        public GrpcProtocolResolver(EngineHostGrpcClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));

            // 先用兜底列表初始化，保证 GetProtocolNames 立即可用
            _cachedProtocols = new List<string>(FallbackProtocols);

            // 后台异步拉取服务端真实列表，拉取完成后热替换缓存
            _ = Task.Run(FetchFromServerAsync);
        }

        // =========================================================================
        // IProtocolResolver 实现
        // =========================================================================

        /// <summary>
        /// 获取协议名称列表。
        /// 返回后台拉取的服务端列表；服务端未响应时返回兜底列表。
        /// 列表为快照，调用方不应修改。
        /// </summary>
        public IList<string> GetProtocolNames()
        {
            // 返回当前缓存的快照副本，防止调用方修改内部状态
            return new List<string>(_cachedProtocols);
        }

        // =========================================================================
        // 私有方法
        // =========================================================================

        /// <summary>
        /// 后台异步拉取服务端协议列表。
        /// 成功后热替换 <see cref="_cachedProtocols"/>；失败时保留兜底列表。
        /// </summary>
        private async Task FetchFromServerAsync()
        {
            try
            {
                // 向服务端发起 QueryProtocols 请求
                IReadOnlyList<string> serverList =
                    await _client.QueryProtocolsAsync().ConfigureAwait(false);

                // 仅当服务端返回非空列表时才替换缓存
                // 空列表意味着服务端暂无加载插件，此时兜底列表更有用
                if (serverList != null && serverList.Count > 0)
                {
                    // copy-on-write：替换整个引用，避免调用方持有的副本受影响
                    _cachedProtocols = new List<string>(serverList);
                }
                // 服务端返回空列表：保留兜底列表，不替换
            }
            catch (Exception)
            {
                // 任何异常（网络、超时、Unimplemented 已在客户端捕获）
                // 均保留兜底列表，不影响 UI 正常使用
            }
        }
    }
}
