namespace CommunicationDebuggingTools.Core.Enums {
    /// <summary>
    /// 变量数据类型（协议无关）。
    /// 插件通过 Capabilities 声明支持子集；未声明的类型业务层不要下发。
    /// </summary>
    public enum VariableDataType {
        Bool = 0,
        Int16 = 1,
        UInt16 = 2,
        Int32 = 3,
        UInt32 = 4,
        Int64 = 5,
        UInt64 = 6,
        Float = 7,
        Double = 8,
        String = 9
    }
}