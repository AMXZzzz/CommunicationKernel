using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using CommunicationDebuggingTools.Business.Device;
using CommunicationDebuggingTools.Business.Persistence;
using CommunicationDebuggingTools.Business.Plugins;
using CommunicationDebuggingTools.Business.Variable;
using CommunicationDebuggingTools.Core;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Logging;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.EngineHost.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CommunicationDebuggingTools.EngineHost {

    /// <summary>
    /// 引擎进程入口：加载 Business + 插件。
    /// 5100 提供 gRPC（HTTP/2）；5101 提供 Web API + 轻量监控页（HTTP/1.1）。
    /// </summary>
    public static class Program {

        public const int DefaultGrpcPort = AppConfig.DefaultEngineHostGrpcPort;
        public const int DefaultWebPort = AppConfig.DefaultEngineHostWebPort;

        public static void Main (string[] args) {
            string baseDir = AppContext.BaseDirectory;

            var builder = WebApplication.CreateBuilder(args);

            int configuredGrpcPort = ReadPort(builder.Configuration["EngineHost:GrpcPort"], DefaultGrpcPort);
            int configuredWebPort = ReadPort(builder.Configuration["EngineHost:WebPort"], DefaultWebPort);

            int grpcPort = FindAvailablePort(configuredGrpcPort);
            int webPort = FindAvailablePort(configuredWebPort, grpcPort);

            builder.WebHost.ConfigureKestrel(options => {
                options.ListenAnyIP(grpcPort, o => {
                    o.Protocols = HttpProtocols.Http2;
                });
                options.ListenAnyIP(webPort, o => {
                    o.Protocols = HttpProtocols.Http1;
                });
            });

            builder.Services.AddGrpc();
            RegisterBusiness(builder.Services, baseDir);

            var app = builder.Build();

            var log = app.Services.GetRequiredService<IAppLogger>();
            try {
                app.Services.GetRequiredService<IDeviceService>().Load();
                app.Services.GetRequiredService<IVariableService>().Load();
                app.Services.GetRequiredService<IPollingEngine>().Start();
                log.Info("EngineHost", "设备/变量已加载，轮询已启动");
            } catch (Exception ex) {
                log.Error("EngineHost", "启动加载失败: " + ex.Message, ex);
            }

            app.MapGrpcService<EngineGrpcService>();

            app.MapGet("/", () => Results.Ok(new {
                name = "CommunicationDebuggingTools.EngineHost",
                grpc = "http://0.0.0.0:" + grpcPort,
                webApi = "http://0.0.0.0:" + webPort,
                message = "独立 Web UI 请访问 CommunicationDebuggingTools.WebUI 项目"
            }));

            app.MapGet("/api/status", (IDeviceService devices, IVariableService variables) => Results.Ok(new {
                serverTime = DateTimeOffset.Now,
                deviceCount = devices?.Devices?.Count ?? 0,
                connectedDeviceCount = devices?.Devices?.Count(d => d?.IsConnected == true) ?? 0,
                variableCount = variables?.Variables?.Count ?? 0
            }));

            app.MapGet("/api/protocols", (IProtocolResolver protocols) => Results.Ok(
                protocols?.GetProtocolNames()?.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? Array.Empty<string>()));

            app.MapGet("/api/devices", (IDeviceService devices) => Results.Ok(
                devices?.Devices?.Where(d => d != null)
                    .Select(d => (object)new {
                        id = d.Id,
                        name = d.Name,
                        model = d.Model,
                        protocol = d.Protocol,
                        ip = d.Ip,
                        port = d.Port,
                        stationNo = d.StationNo,
                        lane = d.Lane.ToString(),
                        isConnected = d.IsConnected,
                        status = d.StatusText
                    })
                ?? Array.Empty<object>()));

            app.MapPost("/api/devices", (IDeviceService devices, DeviceUpsertRequest req) => {
                if (req == null || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Protocol))
                    return Results.BadRequest("名称和协议不能为空");

                var info = new DeviceInfo {
                    Name = req.Name.Trim(),
                    Model = req.Model?.Trim() ?? string.Empty,
                    Protocol = req.Protocol.Trim(),
                    Ip = req.Ip?.Trim() ?? string.Empty,
                    Port = req.Port,
                    StationNo = req.StationNo,
                    ExtraSettingsJson = "{}"
                };

                if (TryParseEnum(req.Lane, out LaneType lane)) {
                    info.Lane = lane;
                }

                devices.Add(info);
                return Results.Ok(new { id = info.Id });
            });

            app.MapPut("/api/devices/{id}", (IDeviceService devices, string id, DeviceUpsertRequest req) => {
                var existing = devices?.Devices?.FirstOrDefault(d => d?.Id == id);
                if (existing == null) return Results.NotFound();
                if (req == null || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Protocol))
                    return Results.BadRequest("名称和协议不能为空");

                existing.Name = req.Name.Trim();
                existing.Model = req.Model?.Trim() ?? string.Empty;
                existing.Protocol = req.Protocol.Trim();
                existing.Ip = req.Ip?.Trim() ?? string.Empty;
                existing.Port = req.Port;
                existing.StationNo = req.StationNo;
                if (TryParseEnum(req.Lane, out LaneType lane)) {
                    existing.Lane = lane;
                }

                devices.Update(existing);
                return Results.Ok();
            });

            app.MapDelete("/api/devices/{id}", (IDeviceService devices, string id) => {
                var existing = devices?.Devices?.FirstOrDefault(d => d?.Id == id);
                if (existing == null) return Results.NotFound();
                devices.Remove(id);
                return Results.Ok();
            });

            app.MapPost("/api/devices/{id}/connect", async (IDeviceService devices, string id, CancellationToken ct) => {
                var existing = devices?.Devices?.FirstOrDefault(d => d?.Id == id);
                if (existing == null) return Results.NotFound();
                bool ok = await devices.ConnectAsync(id, ct).ConfigureAwait(false);
                return Results.Ok(new { success = ok });
            });

            app.MapPost("/api/devices/{id}/disconnect", (IDeviceService devices, string id) => {
                var existing = devices?.Devices?.FirstOrDefault(d => d?.Id == id);
                if (existing == null) return Results.NotFound();
                devices.Disconnect(id);
                return Results.Ok();
            });

            app.MapGet("/api/variables", (IVariableService variables) => Results.Ok(
                variables?.Variables?.Where(v => v != null)
                    .Select(v => (object)new {
                        id = v.Id,
                        deviceId = v.DeviceId,
                        name = v.Name,
                        address = v.Address,
                        dataType = v.DataType.ToString(),
                        access = v.Access.ToString(),
                        length = v.Length,
                        value = v.LastValue != null ? Convert.ToString(v.LastValue) : null,
                        quality = v.Quality.ToString(),
                        unit = v.Unit,
                        category = v.Category,
                        description = v.Description,
                        error = v.LastError
                    })
                ?? Array.Empty<object>()));

            app.MapPost("/api/variables", (IVariableService variables, VariableUpsertRequest req) => {
                if (req == null || string.IsNullOrWhiteSpace(req.DeviceId) || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Address))
                    return Results.BadRequest("设备、名称、地址不能为空");

                var item = BuildVariable(req);
                variables.Add(item);
                return Results.Ok(new { id = item.Id });
            });

            app.MapPut("/api/variables/{id}", (IVariableService variables, string id, VariableUpsertRequest req) => {
                var existing = variables?.Variables?.FirstOrDefault(v => v?.Id == id);
                if (existing == null) return Results.NotFound();
                if (req == null || string.IsNullOrWhiteSpace(req.DeviceId) || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Address))
                    return Results.BadRequest("设备、名称、地址不能为空");

                existing.DeviceId = req.DeviceId.Trim();
                existing.Name = req.Name.Trim();
                existing.Address = req.Address.Trim();
                existing.Length = req.Length;
                existing.Unit = req.Unit?.Trim() ?? string.Empty;
                existing.Category = req.Category?.Trim() ?? string.Empty;
                existing.Description = req.Description?.Trim() ?? string.Empty;
                if (TryParseEnum(req.DataType, out VariableDataType dt)) existing.DataType = dt;
                if (TryParseEnum(req.Access, out VariableAccess access)) existing.Access = access;

                variables.Update(existing);
                return Results.Ok();
            });

            app.MapDelete("/api/variables/{id}", (IVariableService variables, string id) => {
                var existing = variables?.Variables?.FirstOrDefault(v => v?.Id == id);
                if (existing == null) return Results.NotFound();
                variables.Remove(id);
                return Results.Ok();
            });

            app.MapPost("/api/variables/{id}/read", async (IVariableService variables, string id, CancellationToken ct) => {
                var result = await variables.ReadAsync(id, ct).ConfigureAwait(false);
                var value = variables?.Variables?.FirstOrDefault(v => v?.Id == id)?.LastValue;
                return Results.Ok(new {
                    success = result.Success,
                    errorCode = result.ErrorCode.ToString(),
                    message = result.ErrorMessage,
                    value = value != null ? Convert.ToString(value) : null
                });
            });

            app.MapPost("/api/variables/{id}/write", async (IVariableService variables, string id, VariableWriteRequest req, CancellationToken ct) => {
                if (req == null) return Results.BadRequest("写入值不能为空");
                var result = await variables.WriteAsync(id, req.Value, ct).ConfigureAwait(false);
                return Results.Ok(new {
                    success = result.Success,
                    errorCode = result.ErrorCode.ToString(),
                    message = result.ErrorMessage
                });
            });

            app.MapGet("/api/logs", (IAppLogger logger) => Results.Ok(
                logger.GetRecent()
                    .Select(e => (object)new {
                        time = e.Time,
                        level = e.LevelText,
                        source = e.Source,
                        message = e.Message
                    })));


            log.Info("EngineHost", "gRPC 监听 http://0.0.0.0:" + grpcPort);
            log.Info("EngineHost", "Web API 监听 http://0.0.0.0:" + webPort);
            app.Run();
        }

        private static int ReadPort (string raw, int fallback) {
            if (int.TryParse(raw, out int port) && port >= 1 && port <= 65535) {
                return port;
            }
            return fallback;
        }

        private static int FindAvailablePort (int preferred, params int[] excluded) {
            var excludedSet = new HashSet<int>(excluded ?? Array.Empty<int>());
            int candidate = preferred;
            for (int i = 0; i < 200; i++, candidate++) {
                if (candidate > 65535) candidate = 1024;
                if (excludedSet.Contains(candidate)) continue;
                if (IsPortAvailable(candidate)) return candidate;
            }

            throw new InvalidOperationException("未找到可用端口，请检查端口占用情况。");
        }

        private static bool IsPortAvailable (int port) {
            TcpListener listener = null;
            try {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                return true;
            } catch (SocketException) {
                return false;
            } finally {
                try { listener?.Stop(); } catch { }
            }
        }

        private static void RegisterBusiness (IServiceCollection sc, string baseDir) {
            sc.AddSingleton<IAppLogger>(_ => new MemoryAppLogger(AppConfig.LogCapacity));

            sc.AddSingleton<IProtocolResolver>(sp => {
                string dir = Path.Combine(baseDir, "plugins");
                var log = sp.GetRequiredService<IAppLogger>();
                var resolver = new ProtocolResolver(log);
                resolver.LoadFromFolder(dir);
                int n = resolver.GetProtocolNames()?.Count ?? 0;
                log.Info("Protocol", "已加载协议 " + n + " 个，目录=" + dir);
                return resolver;
            });

            sc.AddSingleton<IDeviceRepository>(_ =>
                new JsonDeviceRepository(Path.Combine(baseDir, "config", "devices.json")));
            sc.AddSingleton<IVariableRepository>(_ =>
                new JsonVariableRepository(Path.Combine(baseDir, "config", "variables.json")));

            sc.AddSingleton<ITcpProbe, TcpProbe>();
            sc.AddSingleton<IDeviceService, DeviceService>();
            sc.AddSingleton<IVariableService, VariableService>();
            sc.AddSingleton<IPollingEngine, PollingEngine>();
        }

        private static VariableItem BuildVariable (VariableUpsertRequest req) {
            var item = new VariableItem {
                DeviceId = req.DeviceId.Trim(),
                Name = req.Name.Trim(),
                Address = req.Address.Trim(),
                Length = req.Length,
                Unit = req.Unit?.Trim() ?? string.Empty,
                Category = req.Category?.Trim() ?? string.Empty,
                Description = req.Description?.Trim() ?? string.Empty
            };

            if (TryParseEnum(req.DataType, out VariableDataType dt)) item.DataType = dt;
            if (TryParseEnum(req.Access, out VariableAccess access)) item.Access = access;
            return item;
        }

        private static bool TryParseEnum<TEnum> (string value, out TEnum result) where TEnum : struct {
            if (string.IsNullOrWhiteSpace(value)) {
                result = default;
                return false;
            }
            return Enum.TryParse(value, true, out result);
        }

        public sealed class DeviceUpsertRequest {
            public string Name { get; set; }
            public string Model { get; set; }
            public string Protocol { get; set; }
            public string Ip { get; set; }
            public int Port { get; set; }
            public int StationNo { get; set; } = 1;
            public string Lane { get; set; }
        }

        public sealed class VariableUpsertRequest {
            public string DeviceId { get; set; }
            public string Name { get; set; }
            public string Address { get; set; }
            public string DataType { get; set; }
            public string Access { get; set; }
            public int Length { get; set; }
            public string Unit { get; set; }
            public string Category { get; set; }
            public string Description { get; set; }
        }

        public sealed class VariableWriteRequest {
            public string Value { get; set; }
        }
    }
}
