namespace CommunicationKernel.Plugin.Loader.Abstractions;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: IPluginManifest.cs
/// 层级: Plugin.Loader / Abstractions
/// 作用: 定义插件清单（Manifest）最小契约。
/// 说明:
/// 1) 每个可被运行时识别的插件程序集，都应提供至少一个该接口实现类型。
/// 2) 运行时通过反射实例化该类型并读取 <see cref="Descriptor"/>，完成插件元数据发现。
/// 3) 该接口只描述“身份与能力声明”，不承载具体通讯执行逻辑。
/// -----------------------------------------------------------------------------
/// </summary>
public interface IPluginManifest {
    /// <summary>
    /// 获取当前插件的描述信息。
    /// 该描述信息用于版本校验、插件分类、展示名称以及后续实例化路由。
    /// </summary>
    PluginDescriptor Descriptor { get; }
}
