using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Plugin.ModbusTcp {
    /// <summary>
    /// Modbus TCP 插件：真异步会话 + 共性报文读写。
    /// </summary>
    [ProtocolName("Modbus TCP")]
    public sealed class ModbusTcpProtocol : IProtocol {
        private readonly ModbusTcpSession _session = new ModbusTcpSession();
        private bool _disposed;

        public bool IsConnected {
            get { return _session.IsConnected; }
        }

        public async Task<bool> ConnectAsync (
            ProtocolConnectionContext context,
            CancellationToken cancellationToken) {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            _session.Disconnect();
            if (string.IsNullOrWhiteSpace(context.Ip))
                return false;

            int station = context.StationNo;
            if (station < 0) station = 0;
            if (station > 255) station = 255;
            _session.UnitId = (byte)station;

            try {
                int timeout = context.TimeoutMs > 0 ? context.TimeoutMs : AppConfig.DefaultTimeoutMs;
                await _session.ConnectAsync(context.Ip, context.Port, timeout, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            } catch {
                _session.Disconnect();
                return false;
            }
        }

        public void Disconnect () {
            _session.Disconnect();
        }

        public async Task<ProtocolDataMessage> ReadAsync (
            ProtocolDataMessage request,
            CancellationToken cancellationToken) {
            if (request == null)
                throw new ArgumentNullException("request");

            if (!_session.IsConnected)
                return Fail(request, "未连接");

            try {
                cancellationToken.ThrowIfCancellationRequested();

                int addr = ModbusTcpSession.ParseAddress(request.Address);
                bool highFirst = request.WordOrder == WordOrder.HighWordFirst;

                switch (request.DataType) {
                    case VariableDataType.Bool: {
                        bool[] bits = await _session.ReadCoilsAsync(addr, 1, cancellationToken)
                            .ConfigureAwait(false);
                        request.Value = bits != null && bits.Length > 0 && bits[0];
                        break;
                    }
                    case VariableDataType.Int16: {
                        ushort[] r = await _session.ReadHoldingRegistersAsync(addr, 1, cancellationToken)
                            .ConfigureAwait(false);
                        request.Value = (short)r[0];
                        break;
                    }
                    case VariableDataType.UInt16: {
                        ushort[] r = await _session.ReadHoldingRegistersAsync(addr, 1, cancellationToken)
                            .ConfigureAwait(false);
                        request.Value = r[0];
                        break;
                    }
                    case VariableDataType.Int32: {
                        ushort[] r = await _session.ReadHoldingRegistersAsync(addr, 2, cancellationToken)
                            .ConfigureAwait(false);
                        request.Value = ModbusTcpSession.RegistersToInt32(r[0], r[1], highFirst);
                        break;
                    }
                    case VariableDataType.UInt32: {
                        ushort[] r = await _session.ReadHoldingRegistersAsync(addr, 2, cancellationToken)
                            .ConfigureAwait(false);
                        request.Value = ModbusTcpSession.RegistersToUInt32(r[0], r[1], highFirst);
                        break;
                    }
                    case VariableDataType.Int64: {
                        ushort[] r = await _session.ReadHoldingRegistersAsync(addr, 4, cancellationToken)
                            .ConfigureAwait(false);
                        request.Value = ModbusTcpSession.RegistersToInt64(r, highFirst);
                        break;
                    }
                    case VariableDataType.UInt64: {
                        ushort[] r = await _session.ReadHoldingRegistersAsync(addr, 4, cancellationToken)
                            .ConfigureAwait(false);
                        request.Value = ModbusTcpSession.RegistersToUInt64(r, highFirst);
                        break;
                    }
                    case VariableDataType.Float: {
                        ushort[] r = await _session.ReadHoldingRegistersAsync(addr, 2, cancellationToken)
                            .ConfigureAwait(false);
                        request.Value = ModbusTcpSession.RegistersToFloat(r[0], r[1], highFirst);
                        break;
                    }
                    case VariableDataType.Double: {
                        ushort[] r = await _session.ReadHoldingRegistersAsync(addr, 4, cancellationToken)
                            .ConfigureAwait(false);
                        request.Value = ModbusTcpSession.RegistersToDouble(r, highFirst);
                        break;
                    }
                    case VariableDataType.String: {
                        request.Value = await ReadStringValueAsync(request, addr, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    default:
                        return Fail(request, "暂不支持: " + request.DataType);
                }

                request.Success = true;
                request.Quality = DataQuality.Good;
                request.ErrorMessage = "";
                return request;
            } catch (OperationCanceledException) {
                return Fail(request, "已取消");
            } catch (Exception ex) {
                return Fail(request, ex.Message);
            }
        }

        public async Task<ProtocolDataMessage> WriteAsync (
            ProtocolDataMessage request,
            CancellationToken cancellationToken) {
            if (request == null)
                throw new ArgumentNullException("request");

            if (!_session.IsConnected)
                return Fail(request, "未连接");

            try {
                cancellationToken.ThrowIfCancellationRequested();

                int addr = ModbusTcpSession.ParseAddress(request.Address);
                bool highFirst = request.WordOrder == WordOrder.HighWordFirst;

                switch (request.DataType) {
                    case VariableDataType.Bool:
                        await _session.WriteSingleCoilAsync(
                            addr, Convert.ToBoolean(request.Value), cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case VariableDataType.Int16:
                    case VariableDataType.UInt16:
                        await _session.WriteSingleRegisterAsync(
                            addr, Convert.ToUInt16(request.Value), cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case VariableDataType.Int32: {
                        ushort hi, lo;
                        ModbusTcpSession.Int32ToRegisters(
                            Convert.ToInt32(request.Value), out hi, out lo, highFirst);
                        await _session.WriteMultipleRegistersAsync(
                            addr, new ushort[] { hi, lo }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    case VariableDataType.UInt32: {
                        ushort hi, lo;
                        ModbusTcpSession.UInt32ToRegisters(
                            Convert.ToUInt32(request.Value), out hi, out lo, highFirst);
                        await _session.WriteMultipleRegistersAsync(
                            addr, new ushort[] { hi, lo }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    case VariableDataType.Int64: {
                        ushort[] regs = new ushort[4];
                        ModbusTcpSession.Int64ToRegisters(
                            Convert.ToInt64(request.Value), regs, highFirst);
                        await _session.WriteMultipleRegistersAsync(addr, regs, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    case VariableDataType.UInt64: {
                        ushort[] regs = new ushort[4];
                        ModbusTcpSession.UInt64ToRegisters(
                            Convert.ToUInt64(request.Value), regs, highFirst);
                        await _session.WriteMultipleRegistersAsync(addr, regs, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    case VariableDataType.Float: {
                        ushort hi, lo;
                        ModbusTcpSession.FloatToRegisters(
                            Convert.ToSingle(request.Value), out hi, out lo, highFirst);
                        await _session.WriteMultipleRegistersAsync(
                            addr, new ushort[] { hi, lo }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    case VariableDataType.Double: {
                        ushort[] regs = new ushort[4];
                        ModbusTcpSession.DoubleToRegisters(
                            Convert.ToDouble(request.Value), regs, highFirst);
                        await _session.WriteMultipleRegistersAsync(addr, regs, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                    case VariableDataType.String:
                        await WriteStringValueAsync(request, addr, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    default:
                        return Fail(request, "暂不支持: " + request.DataType);
                }

                request.Success = true;
                request.ErrorMessage = "";
                return request;
            } catch (OperationCanceledException) {
                return Fail(request, "已取消");
            } catch (Exception ex) {
                return Fail(request, ex.Message);
            }
        }

        private async Task<string> ReadStringValueAsync (
            ProtocolDataMessage request, int addr, CancellationToken cancellationToken) {
            var enc = ModbusTcpSession.ToEncoding(request.StringEncoding);
            int length = request.Length > 0 ? request.Length : 32;
            int maxBytes = enc.GetMaxByteCount(length);
            int regCount = (maxBytes + 1) / 2;
            if (regCount < 1)
                regCount = 1;

            ushort[] regs = await _session.ReadHoldingRegistersAsync(addr, regCount, cancellationToken)
                .ConfigureAwait(false);
            byte[] bytes = ModbusTcpSession.RegistersToBytes(regs, request.ByteOrder);
            int n = 0;
            while (n < bytes.Length && bytes[n] != 0)
                n++;

            string s = enc.GetString(bytes, 0, n);
            if (s.Length > length)
                s = s.Substring(0, length);
            return s;
        }

        private async Task WriteStringValueAsync (
            ProtocolDataMessage request, int addr, CancellationToken cancellationToken) {
            var enc = ModbusTcpSession.ToEncoding(request.StringEncoding);
            string value = request.Value != null ? request.Value.ToString() : "";
            int maxLength = request.Length > 0 ? request.Length : value.Length;
            if (value.Length > maxLength)
                value = value.Substring(0, maxLength);

            byte[] raw = enc.GetBytes(value);
            int regCount = (maxLength + 1) / 2;
            if (regCount < 1)
                regCount = 1;

            byte[] padded = new byte[regCount * 2];
            int copy = Math.Min(raw.Length, padded.Length);
            Buffer.BlockCopy(raw, 0, padded, 0, copy);

            ushort[] regs = ModbusTcpSession.BytesToRegisters(padded, request.ByteOrder);
            await _session.WriteMultipleRegistersAsync(addr, regs, cancellationToken)
                .ConfigureAwait(false);
        }

        private static ProtocolDataMessage Fail (ProtocolDataMessage request, string message) {
            request.Success = false;
            request.Quality = DataQuality.Bad;
            request.ErrorMessage = message ?? "";
            return request;
        }

        public async Task<bool> PingAsync (CancellationToken cancellationToken) {
            if (!IsConnected) return false;
            try {
                cancellationToken.ThrowIfCancellationRequested();
                bool[] coils = await _session.ReadCoilsAsync(0, 1, cancellationToken)
                    .ConfigureAwait(false);
                return coils != null && coils.Length > 0;
            } catch {
                return false;
            }
        }

        public void Dispose () {
            if (_disposed)
                return;
            _disposed = true;
            _session.Dispose();
        }
    }
}