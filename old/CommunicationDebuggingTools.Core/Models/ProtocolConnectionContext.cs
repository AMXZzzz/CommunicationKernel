using CommunicationDebuggingTools.Core;
using CommunicationDebuggingTools.Core.Enums;

namespace CommunicationDebuggingTools.Core.Models {

    /// <summary>
    /// 建立协议会话时的「连接上下文」。
    /// <para>
    /// 设计约定（架构红线）：
    /// 1. UI / Business 只负责填充字段，不解释字段在某一协议下的含义；
    /// 2. 站号统一使用 <see cref="StationNo"/>，不再通过 JSON 键名（unitId / station）区分协议；
    /// 3. <see cref="ExtraSettingsJson"/> 为可选扩展袋，内容仅由对应插件解析（如 S7 的 rack/slot）；
    /// 4. 字节序 / 字序 / 字符串编码是设备级默认，读写报文时可再带到 <see cref="ProtocolDataMessage"/>。
    /// </para>
    /// <para>
    /// 生命周期：每次 ConnectAsync 由 Business 新建或填充一次，传入插件后由插件读取，
    /// 不要求插件长期持有本对象引用。
    /// </para>
    /// </summary>
    public class ProtocolConnectionContext {

        /// <summary>
        /// 设备 IP（IPv4 或主机名）。空字符串表示未配置，插件应拒绝连接。
        /// </summary>
        public string Ip { get; set; }

        /// <summary>
        /// 通信端口。0 表示未配置；插件可在 Port≤0 时使用本协议默认端口
        /// （例如 Modbus 502、松下 9094），是否使用默认由插件决定。
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// 站号 / 从站号（架构级共性字段）。
        /// <para>
        /// UI 界面标签统一为「站号」，绑定的就是本属性。
        /// 插件侧自行映射，例如：
        /// - Modbus TCP → Unit Id（0–255）
        /// - Panasonic MEWTOCOL → 站号（0–99）
        /// - 某协议若不需要站号 → 可忽略
        /// </para>
        /// 默认值为 1。Business 不得按协议名改写本字段的语义。
        /// </summary>
        public int StationNo { get; set; }

        /// <summary>
        /// 协议私有扩展参数（JSON 字符串，透传）。
        /// <para>
        /// 一期：UI 不编辑，固定为 "{}"。
        /// 二期：仅高级协议（如 S7 rack/slot）由专用表单写入；
        /// Core、Business、普通 UI 代码均不得解析其中的键值。
        /// </para>
        /// 禁止再把「站号」放进本 JSON（站号只走 <see cref="StationNo"/>）。
        /// </summary>
        public string ExtraSettingsJson { get; set; }

        /// <summary>设备默认字节序；读写时作为 ProtocolDataMessage 的默认来源。</summary>
        public ByteOrder ByteOrder { get; set; }

        /// <summary>设备默认字序（多寄存器浮点/整型的高低字顺序）。</summary>
        public WordOrder WordOrder { get; set; }

        /// <summary>设备默认字符串编码（ASCII / UTF-8 / 系统 ANSI 等）。</summary>
        public StringEncodingKind StringEncoding { get; set; }

        /// <summary>
        /// 连接与收发超时（毫秒）。≤0 时插件应使用自身默认（建议不少于 500ms）。
        /// </summary>
        public int TimeoutMs { get; set; }

        /// <summary>
        /// 构造时给出安全默认值，避免 Business 漏赋值导致 null 引用。
        /// StationNo 默认 1；ExtraSettingsJson 默认空对象 "{}".
        /// </summary>
        public ProtocolConnectionContext () {
            Ip = "";
            Port = 0;
            StationNo = 1;
            ExtraSettingsJson = "{}";
            ByteOrder = ByteOrder.BigEndian;
            WordOrder = WordOrder.HighWordFirst;
            StringEncoding = StringEncodingKind.Utf8;
            TimeoutMs = AppConfig.DefaultTimeoutMs;
        }
    }
}