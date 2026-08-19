using CommunicationDebuggingTools.Core.Enums;



namespace CommunicationDebuggingTools.Core.Models {
    /// <summary>
    /// 读写报文。
    /// Business 填充请求字段，插件回填 Value / Success / Quality / ErrorMessage。
    /// Address 为不透明字符串，仅插件内解析。
    /// </summary>
    public class ProtocolDataMessage {
        /// <summary>可选。一般使用当前会话；工具场景可单独指定。</summary>
        public string Ip { get; set; }

        /// <summary>可选。与 Ip 成对使用。</summary>
        public int? Port { get; set; }

        /// <summary>点地址原文（如 40001、DB1.DBD4、R1A）。</summary>
        public string Address { get; set; }

        /// <summary>数据类型。</summary>
        public VariableDataType DataType { get; set; }

        /// <summary>长度：字符串最大长度等；数值型可为 0。</summary>
        public int Length { get; set; }

        /// <summary>字节序（一期来自设备默认）。</summary>
        public ByteOrder ByteOrder { get; set; }

        /// <summary>字序（一期来自设备默认）。</summary>
        public WordOrder WordOrder { get; set; }

        /// <summary>字符串编码（一期来自设备默认）。</summary>
        public StringEncodingKind StringEncoding { get; set; }

        /// <summary>写：待写入值；读成功：回填值。</summary>
        public object Value { get; set; }

        /// <summary>操作是否成功。</summary>
        public bool Success { get; set; }

        /// <summary>读结果质量。</summary>
        public DataQuality Quality { get; set; }

        /// <summary>失败时的错误说明。</summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 构造函数
        /// </summary>
        public ProtocolDataMessage () {
            Address = "";
            DataType = VariableDataType.Int16;
            Length = 0;
            ByteOrder = ByteOrder.BigEndian;
            WordOrder = WordOrder.HighWordFirst;
            StringEncoding = StringEncodingKind.Utf8;
            Success = false;
            Quality = DataQuality.Bad;
            ErrorMessage = "";
        }
    }
}