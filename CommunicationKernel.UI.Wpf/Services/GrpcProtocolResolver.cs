// -----------------------------------------------------------------------------
// 文件: Services/GrpcProtocolResolver.cs
// 层级: UI 层 — 服务实现
// 作用: IProtocolResolver 的 gRPC 实现。
//       启动时向 EngineHost 发起 QueryProtocols 请求，
//       将服务端实际加载的协议插件描述符缓存到本地；
//       服务端不可达（Unimplemented / 网络错误）时自动回退到本地兜底列表，
//       保证设备编辑面板的协议下拉框在离线状态下仍可正常显示。
// 关键约束:
//       兜底列表中的 ProtocolId 必须与插件 DLL 中 ProtocolMetadata.ProtocolId
//       完全一致（modbus-tcp 而非 "Modbus TCP"），否则离线添加的设备
//       在服务端恢复后依然注册失败。
// 调用链:
//   App.xaml.cs → DI → GrpcProtocolResolver(EngineHostGrpcClient)
//     → 构造时 Task.Run 后台拉取 QueryProtocols → _cached 热替换
//   DeviceEditPanel → IProtocolResolver.GetProtocols() → 渲染下拉框与表单
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunicationKernel.UI.Wpf.Core.Interfaces;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// <see cref="IProtocolResolver"/> 的 gRPC 实现。
    /// 优先从 EngineHost.QueryProtocols 获取已加载协议清单；
    /// 服务端未实现或离线时回退到本地兜底列表。
    /// </summary>
    public sealed class GrpcProtocolResolver : IProtocolResolver
    {
        // =========================================================================
        // 兜底列表 — 服务端不可达时使用
        // =========================================================================

        /// <summary>
        /// 本地兜底协议列表。
        /// ProtocolId 与各插件 DLL 中的 ProtocolMetadata.ProtocolId 严格对应，
        /// 修改插件 ProtocolId 时必须同步更新此处。
        /// 服务端 QueryProtocols 成功后此列表会被服务端结果整体覆盖。
        /// </summary>
        private static readonly List<ProtocolDescriptorDto> FallbackProtocols = new()
        {
            new ProtocolDescriptorDto("modbus-tcp",
                "Modbus TCP (MBAP)",              "Tcp",    true,  "从站地址 1-247"),
            new ProtocolDescriptorDto("modbus-rtu",
                "Modbus RTU (CRC16, serial framing)", "Serial", true,  "从站地址 1-247"),
            new ProtocolDescriptorDto("modbus-ascii",
                "Modbus ASCII (LRC, ':' framing, CRLF)", "Serial", true,  "从站地址 1-247"),
            new ProtocolDescriptorDto("panasonic-mewtocol-tcp",
                "Panasonic MEWTOCOL-COM (TCP/ASCII)", "Tcp",    true,  "站号 1-99"),
            new ProtocolDescriptorDto("siemens-s7-1200",
                "Siemens S7-1200 (ISO on TCP)",   "Tcp",    false, string.Empty),
            new ProtocolDescriptorDto("siemens-s7-200smart",
                "Siemens S7-200Smart (ISO on TCP)", "Tcp",  false, string.Empty),
        };

        // =========================================================================
        // 私有字段
        // =========================================================================

        /// <summary>gRPC 客户端，用于调用 QueryProtocols。</summary>
        private readonly EngineHostGrpcClient _client;

        /// <summary>
        /// 缓存的协议描述符列表。
        /// 初始值为兜底列表；QueryProtocols 成功后替换为服务端结果。
        /// 多线程读写通过 volatile + 整体替换（copy-on-write）保证安全，
        /// 无需加锁（列表内容不可变，只替换引用）。
        /// </summary>
        private volatile List<ProtocolDescriptorDto> _cached;

        // =========================================================================
        // 构造函数
        // =========================================================================

        /// <summary>
        /// 初始化并立即在后台发起 QueryProtocols 请求。
        /// 构造完成后 <see cref="GetProtocols"/> 即可安全调用（先返回兜底列表）。
        /// </summary>
        /// <param name="client">已初始化的 gRPC 客户端。</param>
        public GrpcProtocolResolver(EngineHostGrpcClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));

            // 先用兜底列表初始化，保证 GetProtocols 立即可用
            _cached = new List<ProtocolDescriptorDto>(FallbackProtocols);

            // 后台异步拉取服务端真实列表，拉取完成后热替换缓存
            _ = Task.Run(FetchFromServerAsync);
        }

        // =========================================================================
        // IProtocolResolver 实现
        // =========================================================================

        /// <inheritdoc />
        public IList<ProtocolDescriptorDto> GetProtocols()
        {
            // 返回当前缓存的快照副本，防止调用方修改内部状态
            return new List<ProtocolDescriptorDto>(_cached);
        }

        /// <inheritdoc />
        public ProtocolDescriptorDto FindById(string protocolId)
        {
            if (string.IsNullOrWhiteSpace(protocolId))
                return null;

            // 在当前缓存快照中按 Id 不区分大小写查找
            foreach (ProtocolDescriptorDto d in _cached)
            {
                if (string.Equals(d.ProtocolId, protocolId, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            return null;
        }

        // =========================================================================
        // 私有方法
        // =========================================================================

        /// <summary>
        /// 后台异步拉取服务端协议列表。
        /// 成功后热替换 <see cref="_cached"/>；失败时保留兜底列表。
        /// </summary>
        private async Task FetchFromServerAsync()
        {
            try
            {
                IReadOnlyList<ProtocolDescriptorDto> serverList =
                    await _client.QueryProtocolsAsync().ConfigureAwait(false);

                // 仅当服务端返回非空列表时才替换缓存。
                // 空列表意味着服务端未实现该接口或未加载任何插件，此时兜底列表更有用。
                if (serverList != null && serverList.Count > 0)
                {
                    // copy-on-write：替换整个引用，调用方已持有的快照不受影响
                    _cached = new List<ProtocolDescriptorDto>(serverList);
                }
            }
            catch (Exception)
            {
                // 任何异常（网络、超时；Unimplemented 已在客户端内部捕获）
                // 均保留兜底列表，不影响 UI 正常使用
            }
        }
    }
}
