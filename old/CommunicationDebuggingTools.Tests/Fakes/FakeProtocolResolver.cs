using System;
using System.Collections.Generic;
using System.Reflection;
using CommunicationDebuggingTools.Core.Interfaces;

namespace CommunicationDebuggingTools.Tests.Fakes {
    public class FakeProtocolResolver : IProtocolResolver {
        public IProtocol ProtocolToReturn { get; set; }

        public void LoadFromFolder (string folder) { }

        public IProtocol Resolve (string protocolName) {
            if (ProtocolToReturn == null || string.IsNullOrEmpty(protocolName))
                return null;
            // 读 ProtocolNameAttribute（与生产 ProtocolResolver 一致）
            var attr = (ProtocolNameAttribute)Attribute.GetCustomAttribute(
                ProtocolToReturn.GetType(), typeof(ProtocolNameAttribute));
            if (attr != null && attr.Name == protocolName)
                return ProtocolToReturn;
            return null;
        }

        public IList<string> GetProtocolNames () {
            if (ProtocolToReturn == null) return new List<string>();
            var attr = (ProtocolNameAttribute)Attribute.GetCustomAttribute(
                ProtocolToReturn.GetType(), typeof(ProtocolNameAttribute));
            string name = attr?.Name ?? string.Empty;
            return string.IsNullOrEmpty(name)
                ? new List<string>()
                : new List<string> { name };
        }
    }
}