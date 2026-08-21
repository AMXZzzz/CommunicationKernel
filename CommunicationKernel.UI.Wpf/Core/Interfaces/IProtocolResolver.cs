// 文件: Core/Interfaces/IProtocolResolver.cs
// 层级: UI层 — 核心接口
// 作用: 定义协议名称列表提供者的抽象接口，供设备添加对话框的协议下拉框绑定使用。
//       具体实现（GrpcProtocolResolver）可提供硬编码列表或从服务端动态获取。

using System.Collections.Generic;

namespace CommunicationKernel.UI.Wpf.Core.Interfaces
{
    /// <summary>
    /// 协议名称解析器接口。
    /// 提供当前系统支持的协议名称列表，用于设备配置界面的协议选择下拉框。
    /// </summary>
    public interface IProtocolResolver
    {
        /// <summary>
        /// 获取系统支持的协议名称列表。
        /// 列表顺序与 UI 下拉框顺序一致，调用方不应修改返回的列表。
        /// </summary>
        /// <returns>协议名称列表，例如 ["Modbus TCP", "Siemens S7-1200"]。</returns>
        IList<string> GetProtocolNames();
    }
}
