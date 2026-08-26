// -----------------------------------------------------------------------------
// 文件: Services/InProcessHostClient.cs
// 层级: UI 层 — WebMaster 组合根内侧
// 作用: IHostClient 的进程内实现。UI 仍然只看见 route_id 与 DTO，
//       真正的协议/传输由本进程的 EngineRuntime 承担，不再走 gRPC。
// -----------------------------------------------------------------------------

using System.Threading.Channels;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Runtime;
using CommunicationKernel.Engine.Runtime.Models;
using CommunicationKernel.EngineHost.Sdk;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>把 EngineRuntime 适配成 UI 已经在用的 <see cref="IHostClient"/>。</summary>
internal sealed class InProcessHostClient : IHostClient
{
    private const string HostVersion = "1.0.0-embedded";
    private const int StatusChannelCapacity = 256;

    private readonly EngineRuntime _engine;
    private readonly IRouteAssemblyService _assembly;

    public InProcessHostClient(EngineRuntime engine, IRouteAssemblyService assembly)
    {
        _engine = engine;
        _assembly = assembly;
    }

    public Task<HealthResultDto> HealthAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(new HealthResultDto(true, HostVersion, _engine.RouteCount));
    }

    public async Task<RegisterRouteResultDto> RegisterRouteAsync(
        string routeId,
        string protocolId,
        string transportKind,
        string address,
        int port,
        string station,
        string serialPort = "",
        int baudRate = 0,
        int minIoIntervalMs = 100,
        CancellationToken ct = default)
    {
        var command = new RegisterRouteCommand
        {
            RouteId = routeId ?? string.Empty,
            ProtocolId = protocolId ?? string.Empty,
            TransportKind = transportKind ?? string.Empty,
            Address = address ?? string.Empty,
            Port = port,
            Station = station ?? string.Empty,
            SerialPort = serialPort ?? string.Empty,
            BaudRate = baudRate,
            MinIoIntervalMs = minIoIntervalMs,
        };

        OperationResult<string> result = await _engine
            .RegisterRouteAsync(command, ct)
            .ConfigureAwait(false);

        return new RegisterRouteResultDto(
            result.Success,
            result.ErrorCode.ToString(),
            result.Success ? string.Empty : result.ErrorMessage,
            result.Success ? result.Value ?? string.Empty : string.Empty);
    }

    public async Task<RemoveRouteResultDto> RemoveRouteAsync(
        string routeId, CancellationToken ct = default)
    {
        OperationResult result = await _engine
            .UnregisterRouteAsync(routeId, ct)
            .ConfigureAwait(false);

        return new RemoveRouteResultDto(
            result.Success,
            result.ErrorCode.ToString(),
            result.Success ? string.Empty : result.ErrorMessage);
    }

    public Task<IReadOnlyList<RouteDto>> QueryRoutesAsync(
        string routeId = "",
        string protocolId = "",
        string transportKind = "",
        string address = "",
        CancellationToken ct = default)
    {
        _ = ct;
        IReadOnlyList<RouteRuntimeInfo> routes = _engine.SnapshotRoutes();
        List<RouteDto> filtered = new();
        foreach (RouteRuntimeInfo r in routes)
        {
            if (!string.IsNullOrWhiteSpace(routeId)
                && !string.Equals(r.RouteId, routeId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(protocolId)
                && !string.Equals(r.RouteKey.ProtocolId, protocolId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(transportKind)
                && !string.Equals(r.RouteKey.TransportKind.ToString(), transportKind, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(address)
                && !string.Equals(r.RouteKey.Address, address, StringComparison.OrdinalIgnoreCase))
                continue;

            filtered.Add(new RouteDto(
                r.RouteId,
                r.RouteKey.ProtocolId,
                r.RouteKey.TransportKind.ToString(),
                r.RouteKey.Address,
                r.RouteKey.Port,
                r.RouteKey.Station ?? string.Empty,
                r.Endpoint.SerialPort ?? string.Empty,
                r.Endpoint.BaudRate ?? 0));
        }

        return Task.FromResult<IReadOnlyList<RouteDto>>(filtered);
    }

    public Task<IReadOnlyList<ProtocolDescriptorDto>> QueryProtocolsAsync(
        CancellationToken ct = default)
    {
        _ = ct;
        List<ProtocolDescriptorDto> list = new();
        foreach (ProtocolMetadata meta in _assembly.GetAvailableProtocols())
        {
            string[] transports = meta.SupportedTransports is { Count: > 0 }
                ? meta.SupportedTransports.Select(t => t.ToString()).ToArray()
                : new[] { TransportKind.Tcp.ToString() };

            list.Add(new ProtocolDescriptorDto(
                meta.ProtocolId,
                string.IsNullOrWhiteSpace(meta.DisplayName) ? meta.ProtocolId : meta.DisplayName,
                transports,
                meta.RequiresStation,
                meta.StationHint ?? string.Empty));
        }

        return Task.FromResult<IReadOnlyList<ProtocolDescriptorDto>>(list);
    }

    public Task<IReadOnlyList<SerialPortDto>> QuerySerialPortsAsync(
        CancellationToken ct = default)
    {
        _ = ct;
        List<SerialPortDto> list = new();
        foreach (SerialPortInfo port in _assembly.GetAvailableSerialPorts())
        {
            list.Add(new SerialPortDto(
                port.PortName ?? string.Empty,
                port.Description ?? string.Empty));
        }

        return Task.FromResult<IReadOnlyList<SerialPortDto>>(list);
    }

    public async Task<ReadResultDto> ReadAsync(
        string routeId, string address, int length, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(routeId))
            return new ReadResultDto(false, "InvalidArgument", "route_id is required", Array.Empty<byte>());

        OperationResult<byte[]> result = await _engine
            .ReadByRouteIdAsync(routeId, address, length, ct)
            .ConfigureAwait(false);

        return new ReadResultDto(
            result.Success,
            result.ErrorCode.ToString(),
            result.Success ? string.Empty : result.ErrorMessage,
            result.Success ? result.Value ?? Array.Empty<byte>() : Array.Empty<byte>());
    }

    public async Task<WriteResultDto> WriteAsync(
        string routeId, string address, byte[] data, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(routeId))
            return new WriteResultDto(false, "InvalidArgument", "route_id is required");

        OperationResult result = await _engine
            .WriteByRouteIdAsync(routeId, address, data ?? Array.Empty<byte>(), ct)
            .ConfigureAwait(false);

        return new WriteResultDto(
            result.Success,
            result.ErrorCode.ToString(),
            result.Success ? string.Empty : result.ErrorMessage);
    }

    public async Task WatchRouteStatusAsync(
        string routeId,
        Func<RouteStatusDto, Task> onStatus,
        Func<Task> onDisconnected = null!,
        CancellationToken ct = default)
    {
        Channel<RouteStatusDto> channel = Channel.CreateBounded<RouteStatusDto>(
            new BoundedChannelOptions(StatusChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        void OnStatus(RouteStatusSnapshot snapshot)
        {
            if (!string.IsNullOrWhiteSpace(routeId)
                && !string.Equals(routeId, snapshot.RouteId, StringComparison.OrdinalIgnoreCase))
                return;
            channel.Writer.TryWrite(ToDto(snapshot));
        }

        _engine.RouteStatusChanged += OnStatus;
        try
        {
            foreach (RouteStatusSnapshot snapshot in _engine.SnapshotStatuses(routeId))
                await onStatus(ToDto(snapshot)).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                RouteStatusDto dto = await channel.Reader.ReadAsync(ct).ConfigureAwait(false);
                await onStatus(dto).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 会话停止：正常退出，不当成断线
        }
        finally
        {
            _engine.RouteStatusChanged -= OnStatus;
            channel.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static RouteStatusDto ToDto(RouteStatusSnapshot snapshot) =>
        new(
            snapshot.RouteId,
            snapshot.Online,
            snapshot.ErrorCode.ToString(),
            snapshot.ErrorMessage,
            snapshot.TimestampUtc.ToLocalTime().DateTime);
}
