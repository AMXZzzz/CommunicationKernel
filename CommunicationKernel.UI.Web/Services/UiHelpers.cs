// -----------------------------------------------------------------------------
// 文件: Services/UiHelpers.cs
// 层级: UI 层 — Blazor Server
// 作用: 协议色条等纯展示辅助，不涉及通讯语义。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.Web.Services;

internal static class UiHelpers
{
    public static string ProtocolBarClass(string? protocolId)
    {
        string p = protocolId ?? string.Empty;
        if (p.Contains("siemens", StringComparison.OrdinalIgnoreCase) || p.Contains("s7", StringComparison.OrdinalIgnoreCase))
            return "siemens";
        if (p.Contains("panasonic", StringComparison.OrdinalIgnoreCase) || p.Contains("mewtocol", StringComparison.OrdinalIgnoreCase))
            return "panasonic";
        if (p.Contains("modbus", StringComparison.OrdinalIgnoreCase))
            return "modbus";
        return "other";
    }
}
