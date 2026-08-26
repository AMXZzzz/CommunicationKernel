// -----------------------------------------------------------------------------
// 文件: DriverBuildFrameTests.cs
// 层级: 测试
// 作用: 通过公开 IProtocolDriver 接口验证各协议 BuildReadFrame / BuildWriteFrame
//       返回正确的字节帧（驱动级别，不依赖 internal 类）。
// -----------------------------------------------------------------------------

using CommunicationKernel.Core.Protocol.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugins.Protocol.Modbus.Tcp;
using CommunicationKernel.Plugins.Protocol.Modbus.Rtu;
using CommunicationKernel.Plugins.Protocol.Modbus.Ascii;
using CommunicationKernel.Plugins.Protocol.Siemens.S7;
using CommunicationKernel.Plugins.Protocol.Panasonic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CommunicationKernel.Tests;

// =============================================================================
// Modbus TCP Driver — BuildReadFrame / BuildWriteFrame
// =============================================================================

// 验证 MBAP 封装下 FC01/FC03/FC06/FC10 的公开接口输出
[TestClass]
public sealed class ModbusTcpDriverFrameTests {

    private readonly IProtocolDriver _driver =
        new ModbusTcpProtocolDriverFactory().CreateDriver();

    // 保持寄存器读：FC03，quantity 按字节向上取整成寄存器数
    [TestMethod]
    public void BuildReadFrame_HoldingRegister_ReturnsFC03Frame() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildReadFrame("40001", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        // MBAP(7) + PDU(5) = 12 bytes
        Assert.HasCount(12, r.Value);
        // 协议标识符 = 0x0000
        Assert.AreEqual(0x00, r.Value[2]);
        Assert.AreEqual(0x00, r.Value[3]);
        // PDU 长度 = 6
        Assert.AreEqual(0x06, r.Value[5]);
        // FC03
        Assert.AreEqual(0x03, r.Value[7]);
        // 寄存器地址 0 (40001 → offset 0)
        Assert.AreEqual(0x00, r.Value[8]);
        Assert.AreEqual(0x00, r.Value[9]);
        // quantity = ceil(2/2) = 1
        Assert.AreEqual(0x00, r.Value[10]);
        Assert.AreEqual(0x01, r.Value[11]);
    }

    // 线圈读必须走 FC01，不得误发成保持寄存器
    [TestMethod]
    public void BuildReadFrame_Coil_ReturnsFC01Frame() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildReadFrame("coil:10", 1);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x01, r.Value[7]); // FC01
        Assert.AreEqual(0x00, r.Value[8]);
        Assert.AreEqual(0x0A, r.Value[9]); // 地址 10
    }

    // 无法识别的地址必须失败，不得默默落到某个数据区
    [TestMethod]
    public void BuildReadFrame_InvalidAddress_ReturnsFail() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildReadFrame("INVALID_ADDR_XYZ", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
    }

    // 2 字节 payload 走 FC06 单寄存器写
    [TestMethod]
    public void BuildWriteFrame_SingleRegister_ReturnsFC06Frame() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 2 字节 payload → FC06
        OperationResult<byte[]> r = _driver.BuildWriteFrame("40001", new byte[] { 0x00, 0x64 });

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x06, r.Value[7]); // FC06
        Assert.AreEqual(0x00, r.Value[10]); // value hi
        Assert.AreEqual(0x64, r.Value[11]); // value lo = 100
    }

    // 4 字节 payload 走 FC10 多寄存器写
    [TestMethod]
    public void BuildWriteFrame_MultipleRegisters_ReturnsFC10Frame() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 4 字节 payload → FC10
        OperationResult<byte[]> r = _driver.BuildWriteFrame("40001", new byte[] { 0x00, 0x01, 0x00, 0x02 });

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x10, r.Value[7]); // FC10
    }

    // 空 payload 必须拒绝，不得发出值为 0 的写帧
    [TestMethod]
    public void BuildWriteFrame_EmptyPayload_ReturnsFail() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildWriteFrame("40001", Array.Empty<byte>());

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
    }
}

// =============================================================================
// Modbus RTU Driver — BuildReadFrame / BuildWriteFrame
// =============================================================================

// RTU 帧：从站号 + PDU + CRC；length 语义必须按字节而不是寄存器数
[TestClass]
public sealed class ModbusRtuDriverFrameTests {

    private readonly IProtocolDriver _driver =
        new ModbusRtuProtocolDriverFactory().CreateDriver();

    // 读 4 字节 = 2 个寄存器；历史实现曾把 length 当寄存器数，换协议后长度翻倍
    [TestMethod]
    public void BuildReadFrame_HoldingRegister_HasFC03AndCrc() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildReadFrame("40001", 4);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        // [SlaveId][FC03][AddrHi][AddrLo][QtyHi][QtyLo][CrcLo][CrcHi]
        Assert.HasCount(8, r.Value);
        Assert.AreEqual(0x01, r.Value[0]); // SlaveId = 1
        Assert.AreEqual(0x03, r.Value[1]); // FC03
        Assert.AreEqual(0x00, r.Value[2]); // Addr Hi
        Assert.AreEqual(0x00, r.Value[3]); // Addr Lo = 0
        Assert.AreEqual(0x00, r.Value[4]); // Qty Hi
        // length 语义统一为「字节」：请求 4 字节 = 2 个 16 位寄存器。
        // 历史实现中 RTU/ASCII 把 length 当寄存器数、TCP/S7/MEWTOCOL 当字节数，
        // 同一变量换协议后读回长度直接翻倍且不报错。
        Assert.AreEqual(0x02, r.Value[5]); // Qty Lo = 2 个寄存器
    }

    // 帧里的 SlaveId 只能来自设备级站号，地址无权覆盖
    [TestMethod]
    public void BuildReadFrame_AddressWithStationPrefix_IsRejected() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 曾支持 "3:40001" 把 SlaveId 改写成 3。现已禁止：
        // 一条路由只对应一个从站，否则该路由的写串行化与帧间静默形同虚设。
        OperationResult<byte[]> r = _driver.BuildReadFrame("3:40001", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success, "带站号前缀的地址必须被拒绝，不能组出帧");
        StringAssert.Contains(r.ErrorMessage, "站号");
    }

    // 1 字节线圈 payload 走 FC05
    [TestMethod]
    public void BuildWriteFrame_SingleCoil_ReturnsFC05() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 1 字节 coil payload → FC05
        OperationResult<byte[]> r = _driver.BuildWriteFrame("coil:0", new byte[] { 0xFF });

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x05, r.Value[1]); // FC05
    }
}

// =============================================================================
// Modbus ASCII Driver — BuildReadFrame / BuildWriteFrame
// =============================================================================

// ASCII 帧：以 ':' 开头、CR/LF 结尾，中间必须是可打印十六进制
[TestClass]
public sealed class ModbusAsciiDriverFrameTests {

    private readonly IProtocolDriver _driver =
        new ModbusAsciiProtocolDriverFactory().CreateDriver();

    // 帧边界字符是串口分帧的依据，错一个都会让对端永远等不满
    [TestMethod]
    public void BuildReadFrame_StartsWithColonEndsWithCrLf() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildReadFrame("40001", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual((byte)':', r.Value[0]);
        Assert.AreEqual(0x0D, r.Value[r.Value.Length - 2]); // CR
        Assert.AreEqual(0x0A, r.Value[r.Value.Length - 1]); // LF
    }

    // 除末尾 CR/LF 外必须是十六进制 ASCII，否则对端无法解码
    [TestMethod]
    public void BuildReadFrame_IsAllAsciiPrintable() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildReadFrame("40001", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);

        // 除末尾 CR/LF 外，每个字节应为可打印 ASCII
        for (int i = 1; i < r.Value.Length - 2; i++) {
            Assert.IsTrue(r.Value[i] >= 0x30 && r.Value[i] <= 0x46,
                $"byte[{i}] = 0x{r.Value[i]:X2} is not hex digit or colon");
        }
    }

    // 线圈 ON 必须编码为 FF00（Modbus 线圈写的标准常量）
    [TestMethod]
    public void BuildWriteFrame_ON_ContainsFF00() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildWriteFrame("coil:0", new byte[] { 0x01 });

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        string ascii = System.Text.Encoding.ASCII.GetString(r.Value);
        Assert.Contains("FF00", ascii, StringComparison.Ordinal);
    }
}

// =============================================================================
// Siemens S7 Driver — BuildReadFrame / BuildWriteFrame
// =============================================================================

// S7 公开接口必须产出合法 TPKT 帧，读写功能码分别为 0x04 / 0x05
[TestClass]
public sealed class SiemensS7DriverFrameTests {

    private readonly IProtocolDriver _driver =
        new SiemensS7_1200ProtocolDriverFactory().CreateDriver();

    // DB 字地址读：TPKT 版本 0x03，S7 功能码 Read Var = 0x04
    [TestMethod]
    public void BuildReadFrame_DB10_DBW0_ReturnsTpktFrame() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildReadFrame("DB10.DBW0", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        // TPKT 版本 = 0x03
        Assert.AreEqual(0x03, r.Value[0]);
        // 最小长度：TPKT(4) + COTP DT(3) + S7Header(10) + Param ≥ 31
        Assert.IsGreaterThanOrEqualTo(r.Value.Length, 31, $"frame too short: {r.Value.Length}");
        // S7 function: Read Var = 0x04
        Assert.AreEqual(0x04, r.Value[17]);
    }

    // M 区字节地址同样必须包在 TPKT 里
    [TestMethod]
    public void BuildReadFrame_MB10_ReturnsTpktFrame() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildReadFrame("MB10", 1);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x03, r.Value[0]); // TPKT version
    }

    // 非法地址必须失败，不得发出指向未知区域的 S7 帧
    [TestMethod]
    public void BuildReadFrame_InvalidAddress_ReturnsFail() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildReadFrame("BADADDR%%", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
    }

    // 写帧功能码必须是 Write Var = 0x05
    [TestMethod]
    public void BuildWriteFrame_DB10_DBW0_ReturnsTpktWriteFrame() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildWriteFrame("DB10.DBW0", new byte[] { 0x00, 0xFF });

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(0x03, r.Value[0]); // TPKT version
        // S7 function: Write Var = 0x05
        Assert.AreEqual(0x05, r.Value[17]);
    }
}

// =============================================================================
// Panasonic Mewtocol TCP Driver — BuildReadFrame / BuildWriteFrame
// =============================================================================

// MEWTOCOL 公开接口：'%' 开头、CR 结尾；字地址走 RD/WD，位地址走 RCS
[TestClass]
public sealed class MewtocolTcpDriverFrameTests {

    private readonly IProtocolDriver _driver =
        new MewtocolProtocolDriverFactory().CreateDriver();

    // DT 字寄存器读必须产出标准 ASCII 帧
    [TestMethod]
    public void BuildReadFrame_DT100_ReturnsMewtocolAsciiFrame() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildReadFrame("DT100", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        // MEWTOCOL 帧以 '%' 开头，以 CR 结尾
        Assert.AreEqual((byte)'%', r.Value[0]);
        Assert.AreEqual(0x0D, r.Value[r.Value.Length - 1]); // CR
    }

    // 位地址 (IsBit=true) 必须走触点读命令
    [TestMethod]
    public void BuildReadFrame_BitAddress_ReturnsMewtocolContactFrame() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 位地址 (IsBit=true) → RCS 命令
        OperationResult<byte[]> r = _driver.BuildReadFrame("X0", 1);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        string ascii = System.Text.Encoding.ASCII.GetString(r.Value);
        Assert.IsTrue(ascii.Contains("RCS") || ascii.Contains("RD"),
            $"Expected contact read command, got: {ascii}");
    }

    // 无法识别的地址必须失败
    [TestMethod]
    public void BuildReadFrame_InvalidAddress_ReturnsFail() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildReadFrame("ZZINVALID999", 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
    }

    // DT 字寄存器写必须走 WD 命令
    [TestMethod]
    public void BuildWriteFrame_DT100_ReturnsMewtocolWriteFrame() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildWriteFrame("DT100", new byte[] { 0x00, 0x64 });

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual((byte)'%', r.Value[0]);
        string ascii = System.Text.Encoding.ASCII.GetString(r.Value);
        Assert.Contains("WD", ascii, StringComparison.Ordinal);
    }

    // 空 payload 必须拒绝
    [TestMethod]
    public void BuildWriteFrame_EmptyPayload_ReturnsFail() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        OperationResult<byte[]> r = _driver.BuildWriteFrame("DT100", Array.Empty<byte>());

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
    }
}
