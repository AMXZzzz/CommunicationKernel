using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Business.Device {
    /// <summary>
    /// 连接 / 断开 / 心跳 / 通讯计数。
    /// <para>
    /// 约定：凡修改 <see cref="DeviceInfo"/>（含 Mark*）必须在 UI 线程执行，
    /// 否则 WPF 绑定会抛「调用线程无法访问此对象」。
    /// 异步路径在 ConfigureAwait(false) 之后一律经 <see cref="RunOnUi"/> 回切。
    /// </para>
    /// </summary>
    public partial class DeviceService {

        /// <summary>
        /// 异步连接：端口探测 → 解析插件 → ProtocolConnectionContext → 建会话。
        /// </summary>
        public async Task<bool> ConnectAsync (string id, CancellationToken cancellationToken) {
            DeviceInfo device = FindRequired(id);
            if (device.IsConnected)
                return true;

            CancelConnect(id);

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _connectCts[id] = linkedCts;
            CancellationToken ct = linkedCts.Token;

            // MarkConnecting 可能在 UI 线程调用，也可能在后台；统一走 RunOnUi
            RunOnUi(() => MarkConnecting(device));
            LogInfo("开始连接: " + device.Name + " " + device.Ip + ":" + device.Port);

            IProtocol protocol = null;
            try {
                if (!await ProbeReachableAsync(device, ct).ConfigureAwait(false)) {
                    LogWarn("端口不可达: " + device.Name + " " + device.Ip + ":" + device.Port);
                    RunOnUi(() => MarkOffline(device));
                    return false;
                }

                ct.ThrowIfCancellationRequested();

                protocol = _resolver.Resolve(device.Protocol);
                if (protocol == null) {
                    LogError("未找到协议插件: " + device.Protocol + " @ " + device.Name);
                    RunOnUi(() => MarkError(device));
                    return false;
                }

                bool ok = await protocol
                    .ConnectAsync(BuildConnectionContext(device), ct)
                    .ConfigureAwait(false);

                if (ct.IsCancellationRequested) {
                    SafeDisconnectProtocol(protocol);
                    RunOnUi(() => MarkOffline(device));
                    LogInfo("连接已取消: " + device.Name);
                    return false;
                }

                if (ok) {
                    _sessions[id] = protocol;
                    RunOnUi(() => MarkConnected(device));
                    LogInfo("连接成功: " + device.Name + " [" + device.Protocol + "]");
                } else {
                    SafeDisconnectProtocol(protocol);
                    RunOnUi(() => MarkError(device));
                    LogError("协议握手失败: " + device.Name);
                }

                return ok;
            } catch (OperationCanceledException) {
                SafeDisconnectProtocol(protocol);
                RunOnUi(() => MarkOffline(device));
                LogInfo("连接取消: " + device.Name);
                return false;
            } catch (Exception ex) {
                SafeDisconnectProtocol(protocol);
                RunOnUi(() => MarkError(device));
                LogError("连接异常: " + device.Name, ex);
                return false;
            } finally {
                CleanupConnectCts(id, linkedCts);
            }
        }

        /// <summary>取消进行中的连接，并释放已建立的会话。</summary>
        public void Disconnect (string id) {
            if (string.IsNullOrEmpty(id))
                return;

            CancelConnect(id);

            IProtocol protocol;
            if (_sessions.TryRemove(id, out protocol))
                SafeDisconnectProtocol(protocol);

            DeviceInfo device = Devices.FirstOrDefault(d => d.Id == id);
            if (device != null) {
                // Disconnect 可能从心跳线程回调进来，必须回 UI
                RunOnUi(() => MarkOffline(device));
                LogInfo("已断开: " + device.Name);
            }
        }

        /// <summary>
        /// 通讯成功：清零连续失败计数；若处于 Error 则恢复 Success。
        /// </summary>
        public void ReportCommSuccess (string deviceId) {
            if (string.IsNullOrEmpty(deviceId))
                return;

            _commErrors[deviceId] = 0;

            DeviceInfo device = Devices.FirstOrDefault(d => d.Id == deviceId);
            if (device != null
                && device.IsConnected
                && device.StatusType == DeviceStatusType.Error) {
                // 可能被心跳线程调用；改 StatusType 必须在 UI
                RunOnUi(() => {
                    if (device.IsConnected && device.StatusType == DeviceStatusType.Error)
                        device.StatusType = DeviceStatusType.Success;
                });
            }
        }

        /// <summary>
        /// 累计通讯失败；达到阈值后标 ALARM。
        /// TCP 断线请直接 Disconnect，不要走此方法。
        /// </summary>
        public void ReportCommError (string deviceId) {
            if (string.IsNullOrEmpty(deviceId))
                return;

            int count = _commErrors.AddOrUpdate(deviceId, 1, (_, c) => c + 1);
            if (count < COMM_ERROR_THRESHOLD)
                return;

            DeviceInfo device = Devices.FirstOrDefault(d => d.Id == deviceId);
            if (device == null)
                return;

            RunOnUi(() => {
                if (device.IsConnected
                    && device.StatusType != DeviceStatusType.Error) {
                    device.StatusType = DeviceStatusType.Error;
                    LogWarn("通讯连续失败达阈值，ALARM: " + device.Name);
                }
            });
        }

        /// <summary>
        /// 检查已连接会话（定时器调用）。
        /// 上一轮未完成则跳过，避免 Ping 任务堆积。
        /// </summary>
        public void CheckConnections () {
            if (Interlocked.CompareExchange(ref _pinging, 1, 0) != 0)
                return;

            CancellationTokenSource old = _pingCts;
            if (old != null) {
                try { old.Cancel(); } catch (Exception ex) { LogWarn("取消上一轮心跳令牌失败: " + ex.Message); }
                try { old.Dispose(); } catch (Exception ex) { LogWarn("释放上一轮心跳令牌失败: " + ex.Message); }
            }

            _pingCts = new CancellationTokenSource();
            CancellationToken token = _pingCts.Token;
            List<string> ids = _sessions.Keys.ToList();

            if (ids.Count == 0) {
                Interlocked.Exchange(ref _pinging, 0);
                return;
            }

            Task.Run(async () => {
                try {
                    foreach (string id in ids) {
                        if (token.IsCancellationRequested)
                            break;

                        IProtocol protocol;
                        if (!_sessions.TryGetValue(id, out protocol) || protocol == null)
                            continue;

                        bool ok = false;
                        try {
                            ok = await protocol.PingAsync(token).ConfigureAwait(false);
                        } catch (OperationCanceledException) {
                            break;
                        } catch (Exception ex) {
                            ok = false;
                            LogWarn("Ping 异常: " + id + " - " + ex.Message);
                        }

                        string capturedId = id;
                        IProtocol capturedProto = protocol;
                        bool capturedOk = ok;

                        void handle () => OnPingResult(capturedId, capturedProto, capturedOk);

                        if (_uiContext != null)
                            _uiContext.Post(_ => handle(), null);
                        else
                            handle();
                    }
                } finally {
                    Interlocked.Exchange(ref _pinging, 0);
                }
            }, token);
        }

        /// <summary>Ping 结果回调（应在 UI 线程执行）。</summary>
        private void OnPingResult (string deviceId, IProtocol protocol, bool ok) {
            if (ok) {
                ReportCommSuccess(deviceId);
                return;
            }

            if (protocol == null || !protocol.IsConnected)
                Disconnect(deviceId);
            else
                ReportCommError(deviceId);
        }

        /// <summary>断开全部设备，并取消进行中的心跳。</summary>
        public void DisconnectAll () {
            CancellationTokenSource ping = Interlocked.Exchange(ref _pingCts, null);
            if (ping != null) {
                try { ping.Cancel(); } catch (Exception ex) { LogWarn("取消心跳令牌失败: " + ex.Message); }
                try { ping.Dispose(); } catch (Exception ex) { LogWarn("释放心跳令牌失败: " + ex.Message); }
            }

            foreach (string id in Devices.Select(d => d.Id).Where(x => !string.IsNullOrEmpty(x)).ToList())
                Disconnect(id);

            foreach (string id in _sessions.Keys.ToList())
                Disconnect(id);

            foreach (string id in _connectCts.Keys.ToList())
                CancelConnect(id);
        }

        /// <summary>获取已连接协议会话；未连接返回 null。</summary>
        public IProtocol GetProtocol (string deviceId) {
            if (string.IsNullOrEmpty(deviceId))
                return null;

            IProtocol p;
            return _sessions.TryGetValue(deviceId, out p) ? p : null;
        }

        /// <summary>
        /// 设备 → 建连上下文。只拷贝共性字段，不解析 ExtraSettingsJson。
        /// </summary>
        private static ProtocolConnectionContext BuildConnectionContext (DeviceInfo device) {
            return new ProtocolConnectionContext {
                Ip = device.Ip ?? "",
                Port = device.Port,
                StationNo = device.StationNo,
                ExtraSettingsJson = string.IsNullOrWhiteSpace(device.ExtraSettingsJson)
                    ? "{}"
                    : device.ExtraSettingsJson,
                ByteOrder = device.ByteOrder,
                WordOrder = device.WordOrder,
                StringEncoding = device.StringEncoding,
                TimeoutMs = AppConfig.DefaultTimeoutMs
            };
        }

        private Task<bool> ProbeReachableAsync (DeviceInfo device, CancellationToken ct) {
            return _tcpProbe.IsPortOpenAsync(
                device.Ip,
                device.Port,
                AppConfig.TcpProbeTimeoutMs,
                ct);
        }

        private void CancelConnect (string id) {
            CancellationTokenSource cts;
            if (!_connectCts.TryGetValue(id, out cts))
                return;

            try { cts.Cancel(); } catch (Exception ex) { LogWarn("取消连接令牌失败: " + id + " - " + ex.Message); }
            _connectCts.TryRemove(id, out _);
            try { cts.Dispose(); } catch (Exception ex) { LogWarn("释放连接令牌失败: " + id + " - " + ex.Message); }
        }

        private void CleanupConnectCts (string id, CancellationTokenSource linkedCts) {
            CancellationTokenSource existing;
            if (_connectCts.TryGetValue(id, out existing) && existing == linkedCts) {
                _connectCts.TryRemove(id, out _);
                linkedCts.Dispose();
            }
        }
    }
}