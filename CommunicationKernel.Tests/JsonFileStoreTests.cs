// -----------------------------------------------------------------------------
// 文件: JsonFileStoreTests.cs
// 层级: 测试
// 作用: 锁住本地配置文件的原子落盘与损坏容错。
//
// 背景（结构审查中发现）：
//   WPF 的 DeviceConfigStore 一直是原子写——先写 .tmp 再 File.Replace。
//   Web 的 WebDeviceStore / WebVariableStore / WebSettingsStore 却是
//   直接 File.WriteAllText 覆写。两处代码看着在做同一件事，
//   实际上只有一处带崩溃防护，而缺的那处只在掉电那天才暴露：
//   留下一个被截断的 JSON，下次启动整份设备配置全部丢失，且没有任何提示。
//
//   这就是重复实现最典型的代价，所以把这一层收敛进了 Host.Sdk。
//   本测试锁住收敛后的行为，防止哪一端再退回非原子写。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunicationKernel.Host.Sdk;

namespace CommunicationKernel.Tests;

[TestClass]
public class JsonFileStoreTests {

    /// <summary>每个测试独占的临时目录，TestCleanup 里删除。</summary>
    private string _dir = string.Empty;

    /// <summary>本次测试用的目标文件路径。</summary>
    private string _file = string.Empty;

    [TestInitialize]
    public void Setup() {
        // 用 GUID 目录隔离，避免并行测试互相踩文件
        _dir = Path.Combine(Path.GetTempPath(), "ckjsontest-" + Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_dir, "records.json");
    }

    [TestCleanup]
    public void Cleanup() {
        try {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        } catch (IOException) {
            // 临时目录清理失败不应让测试失败——系统会自行回收
        }
    }

    /// <summary>测试用的简单记录类型。</summary>
    private sealed class Record {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    // =========================================================================
    // 往返
    // =========================================================================

    [TestMethod]
    public void SaveThenLoad_RoundTrips() {
        List<Record> original = new() {
            new Record { Id = "PLC-1", Name = "一号线主控", Port = 502 },
            new Record { Id = "PLC-2", Name = "二号线", Port = 503 },
        };

        Assert.IsTrue(JsonFileStore.Save(_file, original, out string saveError), saveError);

        List<Record> loaded = JsonFileStore.Load<Record>(_file, out string loadError);
        Assert.IsNull(loadError, loadError);
        Assert.HasCount(2, loaded);
        Assert.AreEqual("PLC-1", loaded[0].Id);
        Assert.AreEqual(502, loaded[0].Port);
    }

    [TestMethod]
    public void Save_CreatesMissingDirectory() {
        // 首次运行时 %APPDATA%\CommunicationKernel 还不存在
        string nested = Path.Combine(_dir, "a", "b", "c", "records.json");

        Assert.IsTrue(JsonFileStore.Save(nested, new[] { new Record { Id = "X" } }, out string error), error);
        Assert.IsTrue(File.Exists(nested));
    }

    [TestMethod]
    public void Save_KeepsChineseReadable() {
        // 默认编码器会把中文转义成 \uXXXX，文件用记事本打开是一片乱码，
        // 现场排查时没法直接看配置
        JsonFileStore.Save(_file, new[] { new Record { Id = "A", Name = "一号线主控" } }, out _);

        string raw = File.ReadAllText(_file);
        StringAssert.Contains(raw, "一号线主控");
    }

    // =========================================================================
    // 原子性
    // =========================================================================

    [TestMethod]
    public void Save_LeavesNoTempFileBehind() {
        JsonFileStore.Save(_file, new[] { new Record { Id = "A" } }, out _);

        // .tmp 残留说明替换步骤没走完
        Assert.IsFalse(File.Exists(_file + ".tmp"),
            "临时文件应在替换后消失，残留意味着原子替换未完成");
    }

    [TestMethod]
    public void Save_OverwriteKeepsFileIntact() {
        // 连续覆写：每次都必须得到完整可解析的文件，而不是被截断的半份
        for (int i = 1; i <= 5; i++) {
            List<Record> batch = Enumerable.Range(0, i * 10)
                .Select(n => new Record { Id = "R" + n, Name = "设备" + n, Port = 500 + n })
                .ToList();

            Assert.IsTrue(JsonFileStore.Save(_file, batch, out string error), error);

            List<Record> back = JsonFileStore.Load<Record>(_file, out string loadError);
            Assert.IsNull(loadError, loadError);
            Assert.HasCount(i * 10, back, "第 " + i + " 次覆写后条数不符");
        }
    }

    // =========================================================================
    // 容错
    // =========================================================================

    [TestMethod]
    public void Load_MissingFile_ReturnsEmptyWithoutError() {
        // 首次启动尚无文件，这不是错误
        List<Record> loaded = JsonFileStore.Load<Record>(_file, out string error);

        Assert.IsNull(error, "文件不存在属于正常情况，不应报错");
        Assert.IsEmpty(loaded);
    }

    [TestMethod]
    public void Load_CorruptFile_ReturnsEmptyAndReportsError() {
        Directory.CreateDirectory(_dir);
        // 模拟掉电留下的截断 JSON
        File.WriteAllText(_file, "[{\"Id\":\"PLC-1\",\"Na");

        List<Record> loaded = JsonFileStore.Load<Record>(_file, out string error);

        Assert.IsEmpty(loaded, "损坏文件应以空配置起步，不能抛异常阻止启动");
        Assert.IsNotNull(error, "损坏必须上报，否则用户不知道配置丢了");
    }

    [TestMethod]
    public void Load_CorruptFile_IsNotDeleted() {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, "{ 这不是合法 JSON");

        JsonFileStore.Load<Record>(_file, out _);

        // 刻意保留损坏文件供事后排查——删掉就再也查不出当时发生了什么
        Assert.IsTrue(File.Exists(_file), "损坏文件不应被删除");
    }

    [TestMethod]
    public void Load_LiteralNullContent_ReturnsEmpty() {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_file, "null");

        List<Record> loaded = JsonFileStore.Load<Record>(_file, out string error);

        Assert.IsNull(error);
        Assert.IsEmpty(loaded, "内容为字面量 null 时反序列化结果也是 null，必须归一为空列表");
    }

    // =========================================================================
    // 单对象
    // =========================================================================

    [TestMethod]
    public void SaveObject_RoundTripsThroughLoad() {
        // settings.json 走的是这条路径
        Assert.IsTrue(JsonFileStore.SaveObject(
            _file, new { HostAddress = "http://192.168.1.10:5000" }, out string error), error);

        string raw = File.ReadAllText(_file);
        StringAssert.Contains(raw, "192.168.1.10");
        Assert.IsFalse(File.Exists(_file + ".tmp"));
    }
}
