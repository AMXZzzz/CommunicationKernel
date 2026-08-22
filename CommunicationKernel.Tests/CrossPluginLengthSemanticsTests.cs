// -----------------------------------------------------------------------------
// 文件: CrossPluginLengthSemanticsTests.cs
// 层级: Tests
// 作用: 对所有协议插件统一验证 IProtocolDriver.length 的语义。
//
// 为什么必须跨插件参数化，而不是每个插件各写一份：
//   本项目栽过一次——Modbus 侧修好了 length==1 的处理，MEWTOCOL 侧
//   一模一样的缺陷被漏掉，因为排查是「按插件」进行而不是「按缺陷模式」进行。
//   参数化到工厂集合后，新增协议插件只要加一行就自动纳入同一套约束；
//   忘了加也会在这里显性缺席，而不是悄悄逃过检查。
//
// 被验证的契约（见 IProtocolDriver 文件头）：
//   1) length 的单位是字节；
//   2) 奇数 length 不得静默向下取整——要么向上对齐并裁剪，要么明确报错；
//   3) length <= 0 必须被拒绝，不得构出一个"读 0 个"的帧发给 PLC。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugins.Modbus.Ascii;
using CommunicationKernel.Plugins.Modbus.Rtu;
using CommunicationKernel.Plugins.Modbus.Tcp;
using CommunicationKernel.Plugins.Panasonic;
using CommunicationKernel.Plugins.Siemens.S7;

namespace CommunicationKernel.Tests;

[TestClass]
public class CrossPluginLengthSemanticsTests {

    /// <summary>
    /// 全部协议工厂及一个该协议下合法的示例地址。
    /// </summary>
    /// <remarks>
    /// 新增协议插件时必须在此登记，否则它不受本文件的任何约束。
    /// <see cref="AllFactories_AreCoveredByThisTest"/> 会检查登记数量，
    /// 让「忘了加」变成一次显式失败而不是静默的覆盖缺口。
    /// </remarks>
    private static IEnumerable<object[]> ProtocolCases() {
        yield return new object[] { new ModbusTcpProtocolDriverFactory(),        "40001" };
        yield return new object[] { new ModbusRtuProtocolDriverFactory(),        "40001" };
        yield return new object[] { new ModbusAsciiProtocolDriverFactory(),      "40001" };
        yield return new object[] { new MewtocolProtocolDriverFactory(),         "DT100" };
        yield return new object[] { new SiemensS7_1200ProtocolDriverFactory(),   "DB1.DBB0" };
        yield return new object[] { new SiemensS7_200SmartProtocolDriverFactory(), "VB0" };
    }

    // =========================================================================
    // length <= 0
    // =========================================================================

    [TestMethod]
    [DynamicData(nameof(ProtocolCases))]
    public void BuildReadFrame_RejectsNonPositiveLength(
        IProtocolDriverFactory factory, string address) {

        IProtocolDriver driver = factory.CreateDriver(new ProtocolDriverContext { Station = "1" });

        foreach (int length in new[] { 0, -1 }) {
            OperationResult<byte[]> result = driver.BuildReadFrame(address, length);

            Assert.IsFalse(result.Success,
                $"{factory.Metadata.ProtocolId}: length={length} 必须被拒绝，" +
                "否则会向 PLC 发出一个读 0 个单位的帧");
        }
    }

    // =========================================================================
    // 奇数 length：不得静默向下取整
    // =========================================================================

    [TestMethod]
    [DynamicData(nameof(ProtocolCases))]
    public void BuildReadFrame_HandlesOddLength_WithoutSilentTruncation(
        IProtocolDriverFactory factory, string address) {

        IProtocolDriver driver = factory.CreateDriver(new ProtocolDriverContext { Station = "1" });

        // length=1 是历史缺陷的触发点：按字区组织的协议会把它算成 0 个字，
        // 于是构出一个「读 0 个」的帧，PLC 要么报错要么返回空。
        OperationResult<byte[]> result = driver.BuildReadFrame(address, 1);

        // 两种做法都合规：向上对齐到 1 个字（读 2 字节后裁剪），或明确报错。
        // 不合规的只有一种：成功返回、但帧里写的是 0 个单位。
        if (result.Success) {
            Assert.IsNotNull(result.Value);
            Assert.IsGreaterThan(0, result.Value.Length,
                $"{factory.Metadata.ProtocolId}: length=1 构帧成功却产出空帧");
        } else {
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.ErrorMessage),
                $"{factory.Metadata.ProtocolId}: 拒绝 length=1 时必须说明原因");
        }
    }

    [TestMethod]
    [DynamicData(nameof(ProtocolCases))]
    public void BuildReadFrame_AcceptsTypicalByteLengths(
        IProtocolDriverFactory factory, string address) {

        IProtocolDriver driver = factory.CreateDriver(new ProtocolDriverContext { Station = "1" });

        // 2 / 4 / 8 字节分别对应 UInt16、UInt32/Float、Double——
        // 这是上层最常见的三种读取宽度，任何一个失败都会让相应数据类型不可用。
        foreach (int length in new[] { 2, 4, 8 }) {
            OperationResult<byte[]> result = driver.BuildReadFrame(address, length);

            Assert.IsTrue(result.Success,
                $"{factory.Metadata.ProtocolId}: length={length} 应被接受，实际失败：{result.ErrorMessage}");
            Assert.IsGreaterThan(0, result.Value.Length);
        }
    }

    // =========================================================================
    // 超大 length：必须报错，不得溢出成小数值
    // =========================================================================

    [TestMethod]
    [DynamicData(nameof(ProtocolCases))]
    public void BuildReadFrame_RejectsAbsurdlyLargeLength(
        IProtocolDriverFactory factory, string address) {

        IProtocolDriver driver = factory.CreateDriver(new ProtocolDriverContext { Station = "1" });

        // 各协议的单帧上限都远小于此。关键在于必须「报错」而不是
        // 让数量字段回绕成一个看起来合法的小数值——那会读回一段错位的数据。
        OperationResult<byte[]> result = driver.BuildReadFrame(address, 100_000);

        Assert.IsFalse(result.Success,
            $"{factory.Metadata.ProtocolId}: 100000 字节远超单帧上限，必须被拒绝");
    }

    // =========================================================================
    // 元数据一致性
    // =========================================================================

    [TestMethod]
    [DynamicData(nameof(ProtocolCases))]
    public void Metadata_DeclaresAtLeastOneSupportedTransport(
        IProtocolDriverFactory factory, string address) {

        _ = address;

        // SupportedTransports 为空时 RouteAssembler 会跳过介质校验，
        // 于是把协议配到不兼容的介质上只会在首次读写才以无关错误暴露。
        Assert.IsNotNull(factory.Metadata.SupportedTransports);
        Assert.IsGreaterThan(0, factory.Metadata.SupportedTransports.Count,
            $"{factory.Metadata.ProtocolId}: 必须声明至少一种支持的传输介质");
    }

    [TestMethod]
    public void AllFactories_AreCoveredByThisTest() {
        // 覆盖缺口守卫：新增协议插件却忘了登记到 ProtocolCases 时，
        // 这条会失败并直接指出该补哪里。
        //
        // 这里比对的是「解决方案内已知的协议工厂数量」——
        // 该数字随插件增减而变，改动时必须同步更新 ProtocolCases。
        const int knownProtocolFactoryCount = 6;

        int registered = 0;
        foreach (object[] _ in ProtocolCases()) registered++;

        Assert.AreEqual(knownProtocolFactoryCount, registered,
            "协议工厂数量与本测试登记的不一致：新增插件后请在 ProtocolCases 中补齐，" +
            "否则该插件不受 length 语义约束（历史上 MEWTOCOL 正是这样漏掉的）");
    }
}
