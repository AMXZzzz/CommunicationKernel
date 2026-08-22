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

## 构建

```bash
dotnet build CommunicationKernel.slnx
```

远程：`https://github.com/AMXZzzz/CommunicationKernel.git`
