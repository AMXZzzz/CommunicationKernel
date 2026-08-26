// -----------------------------------------------------------------------------
// 文件: PluginCatalogTests.cs
// 层级: 测试
// 作用: 覆盖 PluginCatalog.DiscoverAndLoad 对缺失/空/非法目录的容错。
// 说明:
//   只覆盖单次加载路径。旧的 DiscoverAndValidate + LoadValidPlugins
//   双次加载已移除：它把每个 DLL 加载两遍，且校验结论与运行实例来自两次不同加载。
// -----------------------------------------------------------------------------

using CommunicationKernel.Plugin.Context;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

// 目录扫描必须在「没有插件」时安静返回空，而不是抛异常拖垮宿主启动
[TestClass]
public sealed class PluginCatalogTests {

    // 目录根本不存在时返回空清单——现场漏配 plugins 路径不应让宿主起不来
    [TestMethod]
    public void DiscoverAndLoad_WhenDirectoryMissing_ReturnsEmpty() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var catalog = new PluginCatalog();

        // ============================================================================
        // Act
        // ============================================================================
        var results = catalog.DiscoverAndLoad("Z:\\path-not-exists\\plugins");

        // ============================================================================
        // Assert
        // ============================================================================
        // 缺失目录是配置问题，不是崩溃；空清单让上层提示「未发现插件」
        Assert.IsEmpty(results);
    }

    // 空目录同样返回空，不得把「扫到 0 个」当成错误
    [TestMethod]
    public void DiscoverAndLoad_WhenEmptyDirectory_ReturnsEmpty() {
        // ============================================================================
        // Arrange
        // ============================================================================
        string dir = Path.Combine(Path.GetTempPath(), "CommunicationKernelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var catalog = new PluginCatalog();

            // ============================================================================
            // Act
            // ============================================================================
            var results = catalog.DiscoverAndLoad(dir);

            // ============================================================================
            // Assert
            // ============================================================================
            Assert.IsEmpty(results);
        } finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    // null / 空白路径视为未配置，返回空而不是 ArgumentException
    [TestMethod]
    public void DiscoverAndLoad_WhenNullOrWhitespace_ReturnsEmpty() {
        // ============================================================================
        // Arrange
        // ============================================================================
        var catalog = new PluginCatalog();

        // ============================================================================
        // Act / Assert
        // ============================================================================
        // 未填写插件目录时宿主仍应能启动（纯 SDK 嵌入场景就是如此）
        Assert.IsEmpty(catalog.DiscoverAndLoad(null!));
        Assert.IsEmpty(catalog.DiscoverAndLoad("   "));
    }
}
