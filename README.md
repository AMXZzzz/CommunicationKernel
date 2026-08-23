# CommunicationKernel

企业级上位机通信内核（.NET 8），面向多 UI 并发访问同一批 PLC 的场景。

## 两种形态

- **形态 A（嵌入）**：你的程序直接引用 `Engine.Runtime`，本进程连 PLC。不用 `Host.App`，也不用 `Host.Sdk`。
- **形态 B（独立宿主）**：现场跑 `Host.App`；上位机引用 `Host.Sdk`，通过 gRPC 远程读写。

## 解决方案项目

| 项目 | 角色 |
|------|------|
| `Core.Abstractions` | 错误码、结果模型、版本契约 |
| `Communication.Protocol` / `Communication.Transport` | 协议 / 传输抽象 |
| `Plugin.Loader` | 插件发现、校验、隔离加载 |
| `Engine.Router` | 路由与并发调度 |
| `Engine.Runtime` | 通讯内核库（形态 A 直接用） |
| `Host.App` | 现场进程：托管 Runtime + gRPC（形态 B） |
| `Host.Sdk` | 连 Host.App 的客户端库（形态 B 的 UI） |
| `Plugins.Protocol.*` | Modbus / Panasonic / Siemens S7 |
| `Plugins.Transport.*` | Tcp / SerialPort |
| `UI.Wpf` / `UI.Web` | 上位机界面 |
| `Tests` | 行为与 API 基线 |

gRPC 的 protobuf 包名仍是 `CommunicationKernel.EngineHost.Grpc.V1`（线契约，未改）。

## Web 上位机（UI.Web）

Blazor Server 操作员客户端。只持有 `route_id` 与 Host.Sdk DTO，不解析协议：

| 页 | 数据来源 |
|---|---|
| MES 监控 | `HostSession` 路由清单 + `WatchRouteStatus` 在线率 |
| 设备管理 | `QueryProtocols` / `QuerySerialPorts` / `RegisterRoute` / `RemoveRoute` |
| 变量配置 | 本地 `web-variables.json` + `Read` / `Write` |
| 通讯日志 | 进程内 `AppLogStore` |
| 系统设置 | 与 WPF 共用 `settings.json` 的 `HostAddress` |

进程内单例 `HostSession`：5 秒健康检查、全站一条状态流、Host 恢复后按 `web-devices.json` 对账。传输介质取值 `Tcp` / `Serial`。

```bash
dotnet run --project CommunicationKernel.UI.Web
```

默认连 `http://localhost:5000`（`appsettings.json` 的 `Host.App:Address`，可被已保存的 settings.json 覆盖）。

## 构建

```bash
dotnet build CommunicationKernel.slnx
```

远程：`https://github.com/AMXZzzz/CommunicationKernel.git`
