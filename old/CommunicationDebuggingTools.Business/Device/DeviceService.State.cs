using System;
using System.Linq;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Business.Device {
    /// <summary>
    /// 设备运行时状态标记、字段拷贝、查找与安全断开。
    /// 不包含任何协议语义解析。
    /// </summary>
    public partial class DeviceService {

        /// <summary>
        /// 加载/刷新后：清连接态与运行时状态。
        /// 连接结果不能持久化，启动一律视为离线。
        /// </summary>
        private static void ResetRuntimeState (DeviceInfo d) {
            if (d == null)
                return;

            d.IsConnected = false;
            // 必须包含 Error / Warning，否则上次 ALARM 会在启动时残留
            d.StatusType = DeviceStatusType.Offline;

            if (d.StationNo < 0)
                d.StationNo = 1;
            if (string.IsNullOrWhiteSpace(d.ExtraSettingsJson))
                d.ExtraSettingsJson = "{}";
        }

        private static void MarkConnecting (DeviceInfo d) {
            d.StatusType = DeviceStatusType.Connecting;
            d.IsConnected = false;
        }

        private static void MarkConnected (DeviceInfo d) {
            d.IsConnected = true;
            d.StatusType = DeviceStatusType.Success;
        }

        private static void MarkOffline (DeviceInfo d) {
            d.IsConnected = false;
            d.StatusType = DeviceStatusType.Offline;
        }

        private static void MarkError (DeviceInfo d) {
            d.IsConnected = false;
            d.StatusType = DeviceStatusType.Error;
        }

        /// <summary>连接相关配置是否变化（变化则需先断开再连）。</summary>
        private static bool IsConnectionConfigChanged (DeviceInfo old, DeviceInfo device) {
            return old.Ip != device.Ip
                || old.Port != device.Port
                || old.Protocol != device.Protocol
                || old.StationNo != device.StationNo
                || old.ExtraSettingsJson != device.ExtraSettingsJson;
        }

        /// <summary>可编辑字段写回同一实例，保留 Id 与运行时连接状态由调用方处理。</summary>
        private static void CopyDeviceFields (DeviceInfo source, DeviceInfo target) {
            target.Name = source.Name;
            target.Model = source.Model;
            target.Protocol = source.Protocol;
            target.Ip = source.Ip;
            target.Port = source.Port;
            target.StationNo = source.StationNo;
            target.ExtraSettingsJson = string.IsNullOrWhiteSpace(source.ExtraSettingsJson)
                ? "{}"
                : source.ExtraSettingsJson;
            target.Lane = source.Lane;
            target.ByteOrder = source.ByteOrder;
            target.WordOrder = source.WordOrder;
            target.StringEncoding = source.StringEncoding;
        }

        private DeviceInfo FindRequired (string id) {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("Id 不能为空");
            DeviceInfo d = Devices.FirstOrDefault(x => x.Id == id);
            if (d == null)
                throw new InvalidOperationException("设备不存在: " + id);
            return d;
        }


        // ── 测试辅助（仅对 CommunicationDebuggingTools.Tests 可见）────

        /// <summary>
        /// 跳过 TcpProbe 和握手，直接把协议会话注入会话表并标记设备已连接。
        /// 供单元测试使用，避免真实网络请求。
        /// [assembly: InternalsVisibleTo("CommunicationDebuggingTools.Tests")] 已在 AssemblyInfo.cs 声明。
        /// </summary>
        internal void AttachSessionForTest (string deviceId, IProtocol protocol) {
            if (string.IsNullOrEmpty(deviceId) || protocol == null)
                throw new ArgumentException("deviceId 和 protocol 不能为空");

            DeviceInfo device = Devices.FirstOrDefault(d => d != null && d.Id == deviceId);
            if (device == null)
                throw new InvalidOperationException("设备不存在: " + deviceId);

            // 直接注入会话
            _sessions[deviceId] = protocol;

            // 标记已连接（绕过正常流程，仅测试用）
            RunOnUi(() => MarkConnected(device));
        }

        /// <summary>
        /// 安全释放协议实例：先断开会话，再 Dispose 托管资源。
        /// IProtocol : IDisposable，直接调 Dispose（实现内部会先 Disconnect）。
        /// </summary>
        private static void SafeDisconnectProtocol (IProtocol protocol) {
            if (protocol == null)
                return;
            try { protocol.Dispose(); } catch { }
        }
    }
}