using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Plugin.Runtime.Loader;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

[TestClass]
public sealed class PluginCatalogTests {

    [TestMethod]
    public void DiscoverAndValidate_WhenDirectoryMissing_ShouldReturnPluginNotFound() {
        var catalog = new PluginCatalog();

        var results = catalog.DiscoverAndValidate("Z:\\path-not-exists\\plugins");

        Assert.HasCount(1, results);
        Assert.IsFalse(results[0].IsValid);
        Assert.AreEqual(KernelErrorCode.PluginNotFound, results[0].ErrorCode);
    }

    [TestMethod]
    public void DiscoverAndValidate_WhenEmptyDirectory_ShouldReturnEmptyList() {
        string dir = Path.Combine(Path.GetTempPath(), "CommunicationKernelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var catalog = new PluginCatalog();
            var results = catalog.DiscoverAndValidate(dir);
            Assert.IsEmpty(results);
        } finally {
            Directory.Delete(dir, recursive: true);
        }
    }
}
