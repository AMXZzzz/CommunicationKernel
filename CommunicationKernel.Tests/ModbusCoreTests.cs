// -----------------------------------------------------------------------------
// 文件: ModbusCoreTests.cs
// 层级: 测试
// 作用: Modbus 协议语义内核测试。三种变体（TCP / RTU / ASCII）共用同一套语义，
//       因此这些断言一次性覆盖全部三个插件。
// 防回归重点（均对应审查报告中的 P0）：
//   · 数据区只由地址决定，不受读取长度影响
//   · 奇数字节写入必须拒绝，绝不静默补零
//   · 协议上限必须校验，不得构造出畸形帧
//   · 异常帧检测先于正常帧长度检查
//   · 响应必须与请求配对（事务 ID / 从站号 / 功能码）
//   · 3xxxx / 1xxxx 数据区正确识别
//   · length 语义统一为字节
// -----------------------------------------------------------------------------

using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Plugins.Protocol.Modbus.Core;

namespace CommunicationKernel.Tests;

// =============================================================================
// 地址解析：四个数据区
// =============================================================================

// 地址字符串必须映射到正确数据区；映射错了会读到完全不相干的寄存器且不报错
[TestClass]
public class ModbusAddressAreaTests {

    // 4xxxx → 保持寄存器，偏移从 0 起算
    [TestMethod]
    public void Parse_4xxxx_MapsToHoldingRegister() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusAddress.Parse("40001");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusDataArea.HoldingRegister, r.Value.Area);
        Assert.AreEqual((ushort)0, r.Value.RegisterAddress);
    }

    // 历史缺陷：3xxxx 落到「裸数字」分支被当成保持寄存器 30001
    [TestMethod]
    public void Parse_3xxxx_MapsToInputRegister_NotHoldingRegister() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 历史缺陷：3xxxx 落到"裸数字"分支被当成保持寄存器 30001，读错数据区且不报错
        var r = ModbusAddress.Parse("30001");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusDataArea.InputRegister, r.Value.Area);
        Assert.AreEqual((ushort)0, r.Value.RegisterAddress);
    }

    // 1xxxx → 离散输入，不得当成线圈
    [TestMethod]
    public void Parse_1xxxx_MapsToDiscreteInput() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusAddress.Parse("10001");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusDataArea.DiscreteInput, r.Value.Area);
        Assert.AreEqual((ushort)0, r.Value.RegisterAddress);
    }

    // 0xxxx → 线圈
    [TestMethod]
    public void Parse_0xxxx_MapsToCoil() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusAddress.Parse("00001");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusDataArea.Coil, r.Value.Area);
        Assert.AreEqual((ushort)0, r.Value.RegisterAddress);
    }

    // 4x/3x/1x/0x 书写形式必须与 4xxxx 系列等价
    [TestMethod]
    public void Parse_XFormAddresses_AreSupported() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(ModbusDataArea.HoldingRegister, ModbusAddress.Parse("4x0001").Value.Area);
        Assert.AreEqual(ModbusDataArea.InputRegister,   ModbusAddress.Parse("3x0001").Value.Area);
        Assert.AreEqual(ModbusDataArea.DiscreteInput,   ModbusAddress.Parse("1x0001").Value.Area);
        Assert.AreEqual(ModbusDataArea.Coil,            ModbusAddress.Parse("0x0001").Value.Area);
    }

    // 命名前缀 coil:/holding:/input:/discrete: 必须能解析
    [TestMethod]
    public void Parse_NamedPrefixes_AreSupported() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual(ModbusDataArea.Coil,            ModbusAddress.Parse("coil:5").Value.Area);
        Assert.AreEqual(ModbusDataArea.HoldingRegister, ModbusAddress.Parse("holding:5").Value.Area);
        Assert.AreEqual(ModbusDataArea.InputRegister,   ModbusAddress.Parse("input:5").Value.Area);
        Assert.AreEqual(ModbusDataArea.DiscreteInput,   ModbusAddress.Parse("discrete:5").Value.Area);
        Assert.AreEqual((ushort)5, ModbusAddress.Parse("coil:5").Value.RegisterAddress);
    }

    // 裸数字按保持寄存器偏移处理（兼容历史书写）
    [TestMethod]
    public void Parse_BareNumber_IsHoldingRegisterOffset() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusAddress.Parse("100");

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusDataArea.HoldingRegister, r.Value.Area);
        Assert.AreEqual((ushort)100, r.Value.RegisterAddress);
    }

    // 站号前缀已废弃，必须明确拒绝并保留设备级站号语义
    [TestMethod]
    public void Parse_UnitPrefix_IsRejected() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusAddress.Parse("3:40001", defaultUnitId: 9);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success, "站号前缀必须被拒绝");
        StringAssert.Contains(r.ErrorMessage, "站号");
    }

    // 区号前缀含冒号，绝不能被站号拦截规则误伤
    [TestMethod]
    public void Parse_NamedPrefix_StillWorksAfterStationPrefixRemoval() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 拦截规则只认"冒号前是纯数字"，coil:5 的冒号前是字母，必须照常解析
        var coil = ModbusAddress.Parse("coil:5", defaultUnitId: 9);
        var holding = ModbusAddress.Parse("holding:5", defaultUnitId: 9);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(coil.Success, "coil:5 不是站号前缀，不应被拦截");
        Assert.AreEqual(ModbusDataArea.Coil, coil.Value.Area);
        Assert.AreEqual((byte)9, coil.Value.UnitId, "站号仍应取设备级配置");

        Assert.IsTrue(holding.Success, "holding:5 不是站号前缀，不应被拦截");
        Assert.AreEqual(ModbusDataArea.HoldingRegister, holding.Value.Area);
    }

    // 无前缀时吃设备级 UnitId
    [TestMethod]
    public void Parse_NoUnitPrefix_UsesDeviceStation() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusAddress.Parse("40001", defaultUnitId: 9);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual((byte)9, r.Value.UnitId);
    }

    // 空、null、非数字、负数必须失败
    [TestMethod]
    public void Parse_EmptyOrInvalid_Fails() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(ModbusAddress.Parse("").Success);
        Assert.IsFalse(ModbusAddress.Parse(null).Success);
        Assert.IsFalse(ModbusAddress.Parse("abc").Success);
        Assert.IsFalse(ModbusAddress.Parse("-5").Success);
    }

    // 从站前缀越界（>247）必须失败，不得截断成合法值
    [TestMethod]
    public void Parse_OutOfRangeUnitPrefix_Fails() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(ModbusAddress.Parse("250:40001").Success);
    }

    // 设备级默认 UnitId：空/越界回落到 1，合法值保留
    [TestMethod]
    public void ResolveDefaultUnitId_ClampsToValidRange() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual((byte)1,   ModbusAddress.ResolveDefaultUnitId(null));
        Assert.AreEqual((byte)1,   ModbusAddress.ResolveDefaultUnitId("0"));
        Assert.AreEqual((byte)1,   ModbusAddress.ResolveDefaultUnitId("248"));
        Assert.AreEqual((byte)247, ModbusAddress.ResolveDefaultUnitId("247"));
        Assert.AreEqual((byte)17,  ModbusAddress.ResolveDefaultUnitId(" 17 "));
    }
}

// =============================================================================
// PDU：读请求
// =============================================================================

// 读请求功能码只由数据区决定；length 只影响数量，不得反过来改数据区
[TestClass]
public class ModbusPduReadTests {

    // 保持寄存器读走 FC03；4 字节 = 2 个寄存器
    [TestMethod]
    public void BuildReadRequest_HoldingRegister_UsesFc03() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusPdu.BuildReadRequest(ModbusDataArea.HoldingRegister, 0, byteCount: 4);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusFunctionCode.ReadHoldingRegisters, r.Value.Pdu[0]);
        Assert.AreEqual((ushort)2, r.Value.Quantity);   // 4 字节 = 2 个寄存器
    }

    // 输入寄存器读走 FC04
    [TestMethod]
    public void BuildReadRequest_InputRegister_UsesFc04() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusPdu.BuildReadRequest(ModbusDataArea.InputRegister, 0, byteCount: 2);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusFunctionCode.ReadInputRegisters, r.Value.Pdu[0]);
    }

    // 离散输入读走 FC02
    [TestMethod]
    public void BuildReadRequest_DiscreteInput_UsesFc02() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusPdu.BuildReadRequest(ModbusDataArea.DiscreteInput, 0, byteCount: 1);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusFunctionCode.ReadDiscreteInputs, r.Value.Pdu[0]);
    }

    // 历史缺陷：length==1 被无条件当成线圈，保持寄存器却发出 FC01
    [TestMethod]
    public void BuildReadRequest_SingleByteFromHoldingRegister_StaysFc03() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 历史缺陷：length == 1 被无条件当成线圈，
        // "40001" 明明是保持寄存器却发出 FC01，读到完全不相干的数据区且返回 Success
        var r = ModbusPdu.BuildReadRequest(ModbusDataArea.HoldingRegister, 0, byteCount: 1);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusFunctionCode.ReadHoldingRegisters, r.Value.Pdu[0]);
    }

    // FC03 上限 125 个寄存器 = 250 字节，超限必须失败
    [TestMethod]
    public void BuildReadRequest_ExceedingRegisterLimit_Fails() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // FC03 上限 125 个寄存器 = 250 字节
        var r = ModbusPdu.BuildReadRequest(ModbusDataArea.HoldingRegister, 0, byteCount: 252);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
        Assert.AreEqual(KernelErrorCode.InvalidArgument, r.ErrorCode);
    }

    // FC01 上限 2000 位 = 250 字节，超限必须失败
    [TestMethod]
    public void BuildReadRequest_ExceedingBitLimit_Fails() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // FC01 上限 2000 位 = 250 字节
        var r = ModbusPdu.BuildReadRequest(ModbusDataArea.Coil, 0, byteCount: 251);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
    }

    // length<=0 必须拒绝
    [TestMethod]
    public void BuildReadRequest_NonPositiveLength_Fails() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(ModbusPdu.BuildReadRequest(ModbusDataArea.HoldingRegister, 0, 0).Success);
        Assert.IsFalse(ModbusPdu.BuildReadRequest(ModbusDataArea.HoldingRegister, 0, -1).Success);
    }
}

// =============================================================================
// PDU：写请求与 payload 校验
// =============================================================================

// 写路径比读更危险：静默补零会把 0x05 写成 0x0500
[TestClass]
public class ModbusPduWriteTests {

    // 奇数长度必须拒绝，绝不能在末尾补 0x00
    [TestMethod]
    public void ValidateRegisterPayload_OddLength_IsRejected() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        // 历史缺陷：奇数长度在末尾补 0x00，把低字节挪成高字节，
        // 写入 0x05 实际变成写入 0x0500（放大 256 倍）。写错值比读错值危险得多。
        OperationResult r = ModbusPdu.ValidateRegisterPayload(new byte[] { 0x05 });

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
        Assert.AreEqual(KernelErrorCode.InvalidArgument, r.ErrorCode);
    }

    // 空或 null payload 必须拒绝
    [TestMethod]
    public void ValidateRegisterPayload_EmptyOrNull_IsRejected() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(ModbusPdu.ValidateRegisterPayload(null).Success);
        Assert.IsFalse(ModbusPdu.ValidateRegisterPayload(System.Array.Empty<byte>()).Success);
    }

    // FC10 上限 123 个寄存器 = 246 字节；超限时字节数字段会被 (byte) 截断成畸形帧
    [TestMethod]
    public void ValidateRegisterPayload_ExceedingLimit_IsRejected() {
        // ============================================================================
        // Assert
        // ============================================================================
        // FC10 上限 123 个寄存器 = 246 字节；超限时字节数字段会被 (byte) 截断成畸形帧
        Assert.IsFalse(ModbusPdu.ValidateRegisterPayload(new byte[248]).Success);
        Assert.IsTrue(ModbusPdu.ValidateRegisterPayload(new byte[246]).Success);
    }

    // 2 字节走 FC06 单寄存器写
    [TestMethod]
    public void BuildWriteRequest_TwoBytes_UsesFc06() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusPdu.BuildWriteRequest(ModbusDataArea.HoldingRegister, 0, new byte[] { 0x12, 0x34 });

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusFunctionCode.WriteSingleRegister, r.Value[0]);
    }

    // 4 字节走 FC10，寄存器数与字节数必须同时正确
    [TestMethod]
    public void BuildWriteRequest_FourBytes_UsesFc10_WithCorrectByteCount() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusPdu.BuildWriteRequest(ModbusDataArea.HoldingRegister, 0, new byte[] { 1, 2, 3, 4 });

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusFunctionCode.WriteMultipleRegisters, r.Value[0]);
        Assert.AreEqual((ushort)2, (ushort)((r.Value[3] << 8) | r.Value[4]));  // 寄存器数
        Assert.AreEqual((byte)4, r.Value[5]);                                   // 字节数
    }

    // 只读区（输入寄存器 / 离散输入）写必须拒绝
    [TestMethod]
    public void BuildWriteRequest_ReadOnlyArea_IsRejected() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(ModbusPdu.BuildWriteRequest(ModbusDataArea.InputRegister, 0, new byte[] { 1, 2 }).Success);
        Assert.IsFalse(ModbusPdu.BuildWriteRequest(ModbusDataArea.DiscreteInput, 0, new byte[] { 1 }).Success);
    }

    // 线圈写走 FC05，ON 编码为 0xFF00
    [TestMethod]
    public void BuildWriteRequest_Coil_UsesFc05() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        var r = ModbusPdu.BuildWriteRequest(ModbusDataArea.Coil, 0, new byte[] { 1 });

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.AreEqual(ModbusFunctionCode.WriteSingleCoil, r.Value[0]);
        Assert.AreEqual((byte)0xFF, r.Value[3]);
    }
}

// =============================================================================
// PDU：响应校验
// =============================================================================

// 响应必须先认异常、再核功能码、再裁剪到请求字节数
[TestClass]
public class ModbusPduResponseTests {

    private static readonly ModbusRequestContext ReadCtx =
        new(UnitId: 1, FunctionCode: ModbusFunctionCode.ReadHoldingRegisters, ExpectedByteCount: 4);

    // 异常 PDU 只有 2 字节，必须先于正常最小长度检查被识别
    [TestMethod]
    public void ExceptionResponse_IsDetectedBeforeLengthCheck() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 历史缺陷：异常 PDU 只有 2 字节，却先按正常响应最小长度校验，
        // 导致所有异常都报成 "response too short"，
        // 真正的 "Illegal Data Address" 永远看不到——调试工具最需要的诊断信息被抹掉
        byte[] exceptionPdu = { 0x83, 0x02 };   // FC03 | 0x80, 非法数据地址

        // ============================================================================
        // Act
        // ============================================================================
        OperationResult r = ModbusPdu.ValidateResponsePdu(exceptionPdu, ReadCtx);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
        Assert.AreEqual(KernelErrorCode.ProtocolError, r.ErrorCode);
        StringAssert.Contains(r.ErrorMessage, "非法数据地址");
    }

    // 功能码不匹配必须拒绝，不得把线圈响应当保持寄存器数据
    [TestMethod]
    public void MismatchedFunctionCode_IsRejected() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] pdu = { ModbusFunctionCode.ReadCoils, 0x02, 0x00, 0x00 };
        OperationResult r = ModbusPdu.ValidateResponsePdu(pdu, ReadCtx);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.ErrorMessage, "功能码不匹配");
    }

    // 请求 3 字节 → 向上取整读 2 个寄存器 → 必须裁剪回 3 字节
    [TestMethod]
    public void ParseReadResponse_TrimsToRequestedByteCount() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 请求 3 字节 → 向上取整读 2 个寄存器（4 字节）→ 必须裁剪回 3 字节，
        // 保证「请求 N 字节 → 返回 N 字节」的跨插件契约
        var ctx = new ModbusRequestContext(1, ModbusFunctionCode.ReadHoldingRegisters, 3);
        byte[] pdu = { ModbusFunctionCode.ReadHoldingRegisters, 4, 0xAA, 0xBB, 0xCC, 0xDD };

        // ============================================================================
        // Act
        // ============================================================================
        var r = ModbusPdu.ParseReadResponse(pdu, ctx);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        Assert.HasCount(3, r.Value);
        CollectionAssert.AreEqual(new byte[] { 0xAA, 0xBB, 0xCC }, r.Value);
    }

    // 声明了 8 字节却只带 2 字节数据：必须报截断，不得用垃圾填充
    [TestMethod]
    public void ParseReadResponse_TruncatedData_IsRejected() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] pdu = { ModbusFunctionCode.ReadHoldingRegisters, 8, 0xAA, 0xBB };
        var r = ModbusPdu.ParseReadResponse(pdu, ReadCtx);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.ErrorMessage, "截断");
    }
}

// =============================================================================
// 外层封装：RTU
// =============================================================================

// RTU：CRC 小端、异常帧 5 字节、从站号必须配对
[TestClass]
public class ModbusRtuFramingTests {

    // 标准向量：01 03 00 00 00 01 → CRC 0x0A84（低字节先）
    [TestMethod]
    public void Crc16_KnownVector_IsCorrect() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 标准向量：01 03 00 00 00 01 → CRC 0x0A84（低字节先）
        byte[] frame = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 };

        // ============================================================================
        // Act
        // ============================================================================
        ushort crc = ModbusRtuFraming.ComputeCrc16(frame, 0, frame.Length);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual((ushort)0x0A84, crc);
    }

    // Wrap 必须把 CRC 以小端追加在帧尾
    [TestMethod]
    public void Wrap_AppendsCrcLittleEndian() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] pdu = { 0x03, 0x00, 0x00, 0x00, 0x01 };
        byte[] frame = ModbusRtuFraming.Wrap(0x01, pdu);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.HasCount(8, frame);
        Assert.AreEqual((byte)0x01, frame[0]);
        ushort crc = ModbusRtuFraming.ComputeCrc16(frame, 0, 6);
        Assert.AreEqual((byte)(crc & 0xFF), frame[6]);
        Assert.AreEqual((byte)(crc >> 8),   frame[7]);
    }

    // 读响应长度由 ByteCount 字段决定：3 + N + 2
    [TestMethod]
    public void TryGetFrameLength_ReadResponse_ComputesFromByteCount() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // [Unit][FC03][ByteCount=4] → 总长 3 + 4 + 2 = 9
        byte[] partial = { 0x01, 0x03, 0x04 };

        // ============================================================================
        // Act
        // ============================================================================
        bool ok = ModbusRtuFraming.TryGetFrameLength(partial, out int len);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(ok);
        Assert.AreEqual(9, len);
    }

    // 异常帧固定 5 字节。若按正常读响应等待更长，异常会一直等不满而超时
    [TestMethod]
    public void TryGetFrameLength_ExceptionResponse_IsFiveBytes() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 异常帧固定 5 字节。若按正常读响应等待更长长度，异常会一直等不满而超时，
        // 用户看到"读取超时"而不是真正的错误码
        byte[] partial = { 0x01, 0x83 };

        // ============================================================================
        // Act
        // ============================================================================
        bool ok = ModbusRtuFraming.TryGetFrameLength(partial, out int len);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(ok);
        Assert.AreEqual(ModbusRtuFraming.ExceptionFrameLength, len);
    }

    // 写响应固定 8 字节
    [TestMethod]
    public void TryGetFrameLength_WriteResponse_IsEightBytes() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] partial = { 0x01, ModbusFunctionCode.WriteSingleRegister };
        bool ok = ModbusRtuFraming.TryGetFrameLength(partial, out int len);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(ok);
        Assert.AreEqual(ModbusRtuFraming.WriteResponseLength, len);
    }

    // 字节不够时必须返回 false，让传输层继续读
    [TestMethod]
    public void TryGetFrameLength_InsufficientBytes_RequestsMore() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(ModbusRtuFraming.TryGetFrameLength(new byte[] { 0x01 }, out _));
        Assert.IsFalse(ModbusRtuFraming.TryGetFrameLength(
            new byte[] { 0x01, ModbusFunctionCode.ReadHoldingRegisters }, out _));
    }

    // CRC 损坏必须拒绝，不得把噪声当有效响应
    [TestMethod]
    public void Unwrap_BadCrc_IsRejected() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] frame = { 0x01, 0x03, 0x02, 0x00, 0x01, 0xFF, 0xFF };
        var r = ModbusRtuFraming.Unwrap(frame, 0x01);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.ErrorMessage, "CRC");
    }

    // RS-485 一主多从：不比对从站号会把从站 A 的数据当作从站 B 的值
    [TestMethod]
    public void Unwrap_WrongUnitId_IsRejected() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // RS-485 一主多从：上次超时请求的迟到响应会污染本次读取，
        // 不比对从站号就会把从站 A 的数据当作从站 B 的值显示
        byte[] pdu = { 0x03, 0x02, 0x00, 0x01 };
        byte[] frame = ModbusRtuFraming.Wrap(0x05, pdu);

        // ============================================================================
        // Act
        // ============================================================================
        var r = ModbusRtuFraming.Unwrap(frame, expectedUnitId: 0x01);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.ErrorMessage, "从站号不匹配");
    }

    // Wrap/Unwrap 往返必须还原原始 PDU
    [TestMethod]
    public void WrapUnwrap_RoundTrips() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] pdu = { 0x03, 0x02, 0xAB, 0xCD };
        byte[] frame = ModbusRtuFraming.Wrap(0x07, pdu);
        var r = ModbusRtuFraming.Unwrap(frame, 0x07);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        CollectionAssert.AreEqual(pdu, r.Value);
    }
}

// =============================================================================
// 外层封装：TCP (MBAP)
// =============================================================================

// MBAP：事务 ID 必须配对，否则粘连/迟到响应会被当成本次结果
[TestClass]
public class ModbusTcpFramingTests {

    // Wrap 必须写出合法 MBAP：事务 ID、协议 ID=0、长度=UnitId+PDU
    [TestMethod]
    public void Wrap_ProducesValidMbapHeader() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] pdu = { 0x03, 0x00, 0x00, 0x00, 0x01 };
        byte[] frame = ModbusTcpFraming.Wrap(0x1234, 0x01, pdu);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.HasCount(12, frame);
        Assert.AreEqual((byte)0x12, frame[0]);
        Assert.AreEqual((byte)0x34, frame[1]);
        Assert.AreEqual((byte)0x00, frame[2]);      // Protocol ID
        Assert.AreEqual((byte)0x00, frame[3]);
        Assert.AreEqual((byte)0x06, frame[5]);      // Length = UnitId + PDU(5)
        Assert.AreEqual((byte)0x01, frame[6]);
    }

    // MBAP 前 6 字节即可确定总长，无需任何时序猜测
    [TestMethod]
    public void TryGetFrameLength_UsesDeclaredLengthField() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // MBAP 前 6 字节即可确定总长，无需任何时序猜测
        byte[] partial = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06 };

        // ============================================================================
        // Act
        // ============================================================================
        bool ok = ModbusTcpFraming.TryGetFrameLength(partial, out int len);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(ok);
        Assert.AreEqual(12, len);   // 6 + 6
    }

    // 头都不够 6 字节时必须继续读
    [TestMethod]
    public void TryGetFrameLength_InsufficientHeader_RequestsMore() {
        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(ModbusTcpFraming.TryGetFrameLength(new byte[] { 0x00, 0x01, 0x00 }, out _));
    }

    // 事务 ID 的唯一用途就是配对。历史实现认真自增却从不在解析时读取
    [TestMethod]
    public void Unwrap_MismatchedTransactionId_IsRejected() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // 事务 ID 的唯一用途就是配对。历史实现认真自增并写入请求，
        // 却从不在解析时读取，导致粘连或迟到响应被当作本次结果且返回 Success
        byte[] pdu = { 0x03, 0x02, 0x00, 0x01 };
        byte[] frame = ModbusTcpFraming.Wrap(0x0001, 0x01, pdu);

        // ============================================================================
        // Act
        // ============================================================================
        var r = ModbusTcpFraming.Unwrap(frame, expectedTransactionId: 0x0002, expectedUnitId: 0x01);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.ErrorMessage, "事务 ID 不匹配");
    }

    // Unit ID 不匹配必须拒绝
    [TestMethod]
    public void Unwrap_MismatchedUnitId_IsRejected() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] pdu = { 0x03, 0x02, 0x00, 0x01 };
        byte[] frame = ModbusTcpFraming.Wrap(0x0001, 0x05, pdu);

        var r = ModbusTcpFraming.Unwrap(frame, 0x0001, expectedUnitId: 0x01);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.ErrorMessage, "从站号不匹配");
    }

    // Wrap/Unwrap 往返必须还原原始 PDU
    [TestMethod]
    public void WrapUnwrap_RoundTrips() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] pdu = { 0x03, 0x02, 0xAB, 0xCD };
        byte[] frame = ModbusTcpFraming.Wrap(0x00AA, 0x01, pdu);
        var r = ModbusTcpFraming.Unwrap(frame, 0x00AA, 0x01);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        CollectionAssert.AreEqual(pdu, r.Value);
    }
}

// =============================================================================
// 外层封装：ASCII
// =============================================================================

// ASCII：冒号开头、CRLF 结尾、LRC 校验
[TestClass]
public class ModbusAsciiFramingTests {

    // 帧边界字符是串口分帧依据
    [TestMethod]
    public void Wrap_StartsWithColon_EndsWithCrLf() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] pdu = { 0x03, 0x00, 0x00, 0x00, 0x01 };
        byte[] frame = ModbusAsciiFraming.Wrap(0x01, pdu);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual((byte)':', frame[0]);
        Assert.AreEqual((byte)'\r', frame[^2]);
        Assert.AreEqual((byte)'\n', frame[^1]);
    }

    // LRC = 求和取补码，对照已知向量
    [TestMethod]
    public void Lrc_KnownVector_IsCorrect() {
        // ============================================================================
        // Arrange
        // ============================================================================
        // LRC = 求和取补码
        byte[] data = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 };

        // ============================================================================
        // Act
        // ============================================================================
        byte lrc = ModbusAsciiFraming.ComputeLrc(data, 0, data.Length);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.AreEqual((byte)0xFB, lrc);
    }

    // 看到 CRLF 才能确定帧长
    [TestMethod]
    public void TryGetFrameLength_DetectsCrLf() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] frame = ModbusAsciiFraming.Wrap(0x01, new byte[] { 0x03, 0x02, 0x00, 0x01 });
        bool ok = ModbusAsciiFraming.TryGetFrameLength(frame, out int len);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(ok);
        Assert.AreEqual(frame.Length, len);
    }

    // 尚未出现 CRLF 时必须继续读
    [TestMethod]
    public void TryGetFrameLength_WithoutCrLf_RequestsMore() {
        // ============================================================================
        // Arrange / Act / Assert
        // ============================================================================
        byte[] partial = System.Text.Encoding.ASCII.GetBytes(":010302000");
        Assert.IsFalse(ModbusAsciiFraming.TryGetFrameLength(partial, out _));
    }

    // 缺少起始冒号是协议错误（len=-1），不是「再等一会儿」
    [TestMethod]
    public void TryGetFrameLength_MissingStartDelimiter_IsInvalid() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] bad = System.Text.Encoding.ASCII.GetBytes("X0103020001FB\r\n");
        bool ok = ModbusAsciiFraming.TryGetFrameLength(bad, out int len);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(ok);
        Assert.AreEqual(-1, len);
    }

    // LRC 损坏必须拒绝
    [TestMethod]
    public void Unwrap_BadLrc_IsRejected() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] bad = System.Text.Encoding.ASCII.GetBytes(":010302000100\r\n");
        var r = ModbusAsciiFraming.Unwrap(bad, 0x01);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsFalse(r.Success);
        StringAssert.Contains(r.ErrorMessage, "LRC");
    }

    // Wrap/Unwrap 往返必须还原原始 PDU
    [TestMethod]
    public void WrapUnwrap_RoundTrips() {
        // ============================================================================
        // Arrange / Act
        // ============================================================================
        byte[] pdu = { 0x03, 0x02, 0xAB, 0xCD };
        byte[] frame = ModbusAsciiFraming.Wrap(0x07, pdu);
        var r = ModbusAsciiFraming.Unwrap(frame, 0x07);

        // ============================================================================
        // Assert
        // ============================================================================
        Assert.IsTrue(r.Success);
        CollectionAssert.AreEqual(pdu, r.Value);
    }
}
