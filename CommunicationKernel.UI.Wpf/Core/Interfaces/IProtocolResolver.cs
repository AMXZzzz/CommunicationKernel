#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Interfaces/IProtocolResolver.cs
// 层级: UI 层 — WPF 核心接口
// 作用: 协议描述符提供者，供设备编辑面板渲染协议下拉框与连接参数表单。
// 说明:
//   返回完整描述符而非裸名称。下拉框展示 DisplayName，注册路由必须回传 ProtocolId——
//   两者不可混用，否则 Host.App 匹配不到协议工厂。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.UI.Wpf.Services;

namespace CommunicationKernel.UI.Wpf.Core.Interfaces
{
    /// <summary>
    /// 协议描述符解析器接口。
    /// 提供当前 Host.App 已加载的协议清单，用于设备配置界面。
    /// </summary>
    public interface IProtocolResolver
    {
        /// <summary>
        /// 获取可用协议描述符列表。
        /// 列表顺序与 UI 下拉框顺序一致，调用方不应修改返回的列表。
        /// 服务端不可达时返回本地兜底列表，保证离线状态下界面仍可操作。
        /// </summary>
        IList<ProtocolDescriptorDto> GetProtocols();

        /// <summary>
        /// 按 ProtocolId 查找描述符；未找到返回 null。
        /// 用于编辑既有设备时根据已保存的 ProtocolId 还原表单状态。
        /// </summary>
        /// <param name="protocolId">协议唯一标识。</param>
        ProtocolDescriptorDto FindById(string protocolId);

        /// <summary>
        /// 协议清单来源状态，取值见 <see cref="ProtocolSourceState"/>。
        /// 界面据此提示用户当前清单是实时的、来自离线缓存、还是完全不可用。
        /// </summary>
        string SourceState { get; }

        /// <summary>
        /// 重新向服务端拉取协议清单。
        /// 供界面在宿主恢复后手动重试——构造时的那一次拉取失败后不会自动重试。
        /// </summary>
        Task RefreshAsync(CancellationToken ct);

        /// <summary>清单或其来源状态发生变化时触发。可能在任意线程。</summary>
        event Action ProtocolsChanged;
    }
}
