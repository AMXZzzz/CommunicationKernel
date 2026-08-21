using System.Collections.Generic;

namespace CommunicationDebuggingTools.Core.Interfaces {
    /// <summary>
    /// 协议插件解析器契约：负责从指定目录动态加载实现了 <see cref="IProtocol"/> 的插件程序集，
    /// 并根据协议名称创建对应的协议实例。
    /// </summary>
    public interface IProtocolResolver {
        /// <summary>扫描并加载指定目录下的所有 Plugin.*.dll 协议插件。</summary>
        /// <param name="folder">插件所在目录。</param>
        void LoadFromFolder (string folder);

        /// <summary>根据协议显示名称创建一个新的协议实例；若未找到对应插件则返回 null。</summary>
        /// <param name="protocolName">协议显示名称，如 "Modbus TCP"。</param>
        IProtocol Resolve (string protocolName);

        /// <summary>获取当前已成功加载的所有协议名称列表。</summary>
        IList<string> GetProtocolNames ();
    }
}