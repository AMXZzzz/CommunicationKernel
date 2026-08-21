using CommunicationDebuggingTools.Client;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CommunicationDebuggingTools.WebUI.Services;

/// <summary>
/// WebUI 与 EngineHost 的统一网关。
/// </summary>
public sealed class EngineGateway : IDisposable {
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly List<string> _logs = new();
    private CancellationTokenSource? _loopCts;
    private EngineClient? _client;

    /// <summary>连接状态变更事件。</summary>
    public event Action? StateChanged;

    /// <summary>当前 EngineHost 地址。</summary>
    public string HostAddress { get; private set; } = "http://127.0.0.1:5100";

    /// <summary>EngineHost 是否在线。</summary>
    public bool IsConnected { get; private set; }

    /// <summary>设备快照。</summary>
    public IReadOnlyList<DeviceInfo> Devices { get; private set; } = Array.Empty<DeviceInfo>();

    /// <summary>变量快照。</summary>
    public IReadOnlyList<VariableItem> Variables { get; private set; } = Array.Empty<VariableItem>();

    /// <summary>Host 返回的协议名称列表。</summary>
    public IReadOnlyList<string> ProtocolNames { get; private set; } = Array.Empty<string>();

    /// <summary>操作日志快照。</summary>
    public IReadOnlyList<string> Logs {
        get {
            lock (_logs) {
                return _logs.ToList();
            }
        }
    }

    /// <summary>
    /// 初始化连接并启动后台同步循环。
    /// </summary>
    public async Task InitializeAsync (string hostAddress, CancellationToken cancellationToken = default) {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            HostAddress = string.IsNullOrWhiteSpace(hostAddress) ? HostAddress : hostAddress.Trim();
            _client?.Dispose();
            _client = EngineClient.Connect(HostAddress);
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = new CancellationTokenSource();
            _ = Task.Run(() => SyncLoopAsync(_loopCts.Token));
            AddLog("连接", "初始化连接: " + HostAddress);
        } finally {
            _sync.Release();
        }

        await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 立即重连到指定地址。
    /// </summary>
    public Task ReconnectAsync (string hostAddress, CancellationToken cancellationToken = default) {
        return InitializeAsync(hostAddress, cancellationToken);
    }

    /// <summary>
    /// 新增设备。
    /// </summary>
    public async Task AddDeviceAsync (DeviceInfo device, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(device);
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var client = EnsureClient();
            client.Devices.Add(device);
            AddLog("设备", "新增: " + device.Name);
        } finally {
            _sync.Release();
        }
        await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 更新设备。
    /// </summary>
    public async Task UpdateDeviceAsync (DeviceInfo device, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(device);
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var client = EnsureClient();
            client.Devices.Update(device);
            AddLog("设备", "更新: " + device.Name);
        } finally {
            _sync.Release();
        }
        await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除设备。
    /// </summary>
    public async Task RemoveDeviceAsync (string id, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("设备 Id 不能为空", nameof(id));
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var client = EnsureClient();
            client.Devices.Remove(id);
            AddLog("设备", "删除: " + id);
        } finally {
            _sync.Release();
        }
        await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 连接设备。
    /// </summary>
    public async Task<bool> ConnectDeviceAsync (string id, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("设备 Id 不能为空", nameof(id));
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var client = EnsureClient();
            bool ok = await client.Devices.ConnectAsync(id, cancellationToken).ConfigureAwait(false);
            AddLog("连接", (ok ? "连接成功: " : "连接失败: ") + id);
            return ok;
        } finally {
            _sync.Release();
        }
    }

    /// <summary>
    /// 断开设备。
    /// </summary>
    public async Task DisconnectDeviceAsync (string id, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("设备 Id 不能为空", nameof(id));
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var client = EnsureClient();
            client.Devices.Disconnect(id);
            AddLog("连接", "断开: " + id);
        } finally {
            _sync.Release();
        }
    }

    /// <summary>
    /// 全部连接。
    /// </summary>
    public async Task ConnectAllAsync (CancellationToken cancellationToken = default) {
        var ids = Devices.Where(d => d != null && !d.IsConnected).Select(d => d.Id).ToList();
        foreach (var id in ids) {
            await ConnectDeviceAsync(id, cancellationToken).ConfigureAwait(false);
        }
        await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 全部断开。
    /// </summary>
    public async Task DisconnectAllAsync (CancellationToken cancellationToken = default) {
        var ids = Devices.Where(d => d != null && d.IsConnected).Select(d => d.Id).ToList();
        foreach (var id in ids) {
            await DisconnectDeviceAsync(id, cancellationToken).ConfigureAwait(false);
        }
        await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 新增变量。
    /// </summary>
    public async Task AddVariableAsync (VariableItem item, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(item);
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var client = EnsureClient();
            client.Variables.Add(item);
            AddLog("变量", "新增: " + item.Name);
        } finally {
            _sync.Release();
        }
        await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 更新变量。
    /// </summary>
    public async Task UpdateVariableAsync (VariableItem item, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(item);
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var client = EnsureClient();
            client.Variables.Update(item);
            AddLog("变量", "更新: " + item.Name);
        } finally {
            _sync.Release();
        }
        await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除变量。
    /// </summary>
    public async Task RemoveVariableAsync (string id, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("变量 Id 不能为空", nameof(id));
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var client = EnsureClient();
            client.Variables.Remove(id);
            AddLog("变量", "删除: " + id);
        } finally {
            _sync.Release();
        }
        await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读变量。
    /// </summary>
    public async Task<OperationResult> ReadVariableAsync (string id, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("变量 Id 不能为空", nameof(id));
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var client = EnsureClient();
            var result = await client.Variables.ReadAsync(id, cancellationToken).ConfigureAwait(false);
            AddLog("变量", (result.Success ? "读取成功: " : "读取失败: ") + id);
            return result;
        } finally {
            _sync.Release();
        }
    }

    /// <summary>
    /// 写变量。
    /// </summary>
    public async Task<OperationResult> WriteVariableAsync (string id, object value, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("变量 Id 不能为空", nameof(id));
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var client = EnsureClient();
            var result = await client.Variables.WriteAsync(id, value, cancellationToken).ConfigureAwait(false);
            AddLog("变量", (result.Success ? "写入成功: " : "写入失败: ") + id);
            return result;
        } finally {
            _sync.Release();
        }
    }

    /// <summary>
    /// 强制刷新一次设备和变量。
    /// </summary>
    public async Task RefreshNowAsync (CancellationToken cancellationToken = default) {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            var client = _client;
            if (client == null) return;

            bool connected = await client.PingAsync(cancellationToken).ConfigureAwait(false);
            IsConnected = connected;
            if (connected) {
                client.Devices.Load();
                client.Variables.Load();
                Devices = client.Devices.Devices.Select(CloneDevice).ToList();
                Variables = client.Variables.Variables.Select(CloneVariable).ToList();
                try {
                    ProtocolNames = await client.ListProtocolsAsync(cancellationToken).ConfigureAwait(false);
                } catch (Exception ex) {
                    ProtocolNames = Array.Empty<string>();
                    AddLog("连接", "获取协议列表失败: " + ex.Message);
                }
            } else {
                Devices = Array.Empty<DeviceInfo>();
                Variables = Array.Empty<VariableItem>();
                ProtocolNames = Array.Empty<string>();
            }

            StateChanged?.Invoke();
        } finally {
            _sync.Release();
        }
    }

    /// <summary>
    /// 获取协议下拉选项。
    /// </summary>
    public IReadOnlyList<string> GetProtocolNames () {
        return ProtocolNames;
    }

    public void Dispose () {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _sync.Dispose();
        _client?.Dispose();
    }

    private EngineClient EnsureClient () {
        return _client ?? throw new InvalidOperationException("请先初始化连接");
    }

    private async Task SyncLoopAsync (CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            try {
                await RefreshNowAsync(cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;
            } catch (Exception ex) {
                AddLog("连接", "后台同步失败: " + ex.Message);
            }

            try {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    private void AddLog (string source, string message) {
        lock (_logs) {
            _logs.Add($"[{DateTime.Now:HH:mm:ss}] [{source}] {message}");
            if (_logs.Count > 500) {
                _logs.RemoveRange(0, _logs.Count - 500);
            }
        }
    }

    private static DeviceInfo CloneDevice (DeviceInfo d) {
        return new DeviceInfo {
            Id = d.Id,
            Name = d.Name,
            Model = d.Model,
            Protocol = d.Protocol,
            Ip = d.Ip,
            Port = d.Port,
            StationNo = d.StationNo,
            ExtraSettingsJson = d.ExtraSettingsJson,
            Lane = d.Lane,
            StatusType = d.StatusType,
            IsConnected = d.IsConnected,
            ByteOrder = d.ByteOrder,
            WordOrder = d.WordOrder,
            StringEncoding = d.StringEncoding
        };
    }

    private static VariableItem CloneVariable (VariableItem v) {
        return new VariableItem {
            Id = v.Id,
            DeviceId = v.DeviceId,
            Name = v.Name,
            Address = v.Address,
            DataType = v.DataType,
            Access = v.Access,
            Length = v.Length,
            LastValue = v.LastValue,
            LastError = v.LastError,
            Quality = v.Quality,
            Unit = v.Unit,
            Category = v.Category,
            Description = v.Description,
            ScanRateMs = v.ScanRateMs,
            IsPollingEnabled = v.IsPollingEnabled
        };
    }
}
