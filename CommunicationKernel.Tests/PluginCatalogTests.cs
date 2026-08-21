using CommunicationKernel.Plugin.Runtime.Loader;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

/// <summary>
/// 插件目录扫描测试。
/// </summary>
/// <remarks>
/// 只覆盖 <see cref="PluginCatalog.DiscoverAndLoad"/> —— 单次加载即完成校验与实例化。
/// 旧的 DiscoverAndValidate + LoadValidPlugins 双次加载路径已移除：
/// 它把每个 DLL 加载两遍，且校验结论与运行实例来自两次不同加载。
/// </remarks>
[TestClass]
public sealed class PluginCatalogTests {

    [TestMethod]
    public void DiscoverAndLoad_WhenDirectoryMissing_ReturnsEmpty() {
        var catalog = new PluginCatalog();

        var results = catalog.DiscoverAndLoad("Z:\\path-not-exists\\plugins");

        Assert.IsEmpty(results);
    }

    [TestMethod]
    public void DiscoverAndLoad_WhenEmptyDirectory_ReturnsEmpty() {
        string dir = Path.Combine(Path.GetTempPath(), "CommunicationKernelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var catalog = new PluginCatalog();
            var results = catalog.DiscoverAndLoad(dir);
            Assert.IsEmpty(results);
        } finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void DiscoverAndLoad_WhenNullOrWhitespace_ReturnsEmpty() {
        var catalog = new PluginCatalog();

        Assert.IsEmpty(catalog.DiscoverAndLoad(null!));
        Assert.IsEmpty(catalog.DiscoverAndLoad("   "));
    }
}
