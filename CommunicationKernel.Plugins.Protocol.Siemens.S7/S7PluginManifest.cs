// -----------------------------------------------------------------------------
// 文件: S7PluginManifest.cs
// 层级: Plugins / Siemens.S7
// 作用: 西门子 S7 合并插件 Manifest，声明插件元数据与 API 版本。
// 说明:
// 1) 同一 DLL 提供两个协议工厂：S7-1200 与 S7-200Smart。
// 2) 宿主按 ProtocolId（siemens-s7-1200 / siemens-s7-200smart）选择对应工厂。
// 3) 两者共享 TPKT/COTP/S7Comm 基础帧工具，差异仅在 TSAP 连接参数。
// -----------------------------------------------------------------------------

using CommunicationKernel.Plugin.Loader.Abstractions;

namespace CommunicationKernel.Plugins.Protocol.Siemens.S7;

// ============================================================================
// Manifest
// ============================================================================

/// <summary>
/// 西门子 S7 合并插件 Manifest（包含 S7-1200 与 S7-200Smart）。
/// </summary>
public sealed class SiemensS7PluginManifest : IPluginManifest {
    public PluginDescriptor Descriptor { get; } = new() {
        PluginId    = "siemens-s7",
        DisplayName = "Siemens S7 Protocol Plugin (S7-1200 / S7-200Smart)",
        Kind        = PluginKind.Protocol,
        ApiVersion  = 1,
        Version     = "1.0.0",
        EntryType   = typeof(SiemensS7PluginManifest).FullName
    };
}
