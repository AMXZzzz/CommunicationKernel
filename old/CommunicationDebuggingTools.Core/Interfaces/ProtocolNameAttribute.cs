using System;

namespace CommunicationDebuggingTools.Core.Interfaces {

    /// <summary>
    /// 标注协议插件的显示名称，供 ProtocolResolver 在不实例化类的情况下完成注册。
    ///
    /// 用法：
    ///   [ProtocolName("Modbus TCP")]
    ///   public sealed class ModbusTcpProtocol : IProtocol { ... }
    ///
    /// 替代原来的 IProtocol.GetProtocolName()——
    /// 旧方案需要 Activator.CreateInstance 创建临时实例才能读名称，
    /// 新方案通过反射直接读 Attribute，零实例化、零资源泄漏。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ProtocolNameAttribute : Attribute {

        /// <summary>协议显示名称；须与 DeviceInfo.Protocol 存储值完全一致。</summary>
        public string Name { get; }

        public ProtocolNameAttribute (string name) {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("协议名称不能为空", nameof(name));
            Name = name.Trim();
        }
    }
}