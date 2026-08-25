# CommunicationKernel

企业级上位机通信内核（.NET 8），面向多 UI 并发访问同一批 PLC 的场景。

## 两种形态

- **形态 A（嵌入）**：你的程序直接引用 `Engine.Runtime`，本进程连 PLC。不用 `Host.App`，也不用 `Host.Sdk`。
- **形态 B（独立宿主）**：现场跑 `Host.App`；上位机引用 `Host.Sdk`，通过 gRPC 远程读写。

## 分层

调用链严格自上而下，依赖图无环。唯一的反向流是 `EngineRuntime.RouteStatusChanged`
事件——下层发布、上层订阅，不产生反向引用。

```
L7  UI.Wpf / UI.Web          只持有 route_id 与 SDK DTO
L6  Host.Sdk / Host.App      唯一入口；Host.Sdk 零工程引用，UI 无法绕过它触达内部
L5  Engine.Runtime           路由生命周期、轮询、链路巡检
L4  Engine.Router            按 RouteKey 分发，无协议分支
L3  Plugin.Loader            ALC 隔离加载，只认 Core.Abstractions
L2  Plugins.Protocol.*       协议知识全部封在这里，外层一律禁知
L1  Communication.Transport  字节级收发，不解释内容
L0  Core.Abstractions        契约根，零工程引用
```

完整的分层约束、职责红线与**结构纪律六条**见 [`计划.md`](计划.md) 第一节。
新增代码前必须先读那一节。

## 解决方案项目（16 个）

| 项目 | 角色 |
|------|------|
| `Core.Abstractions` | 错误码、结果模型、版本契约 |
| `Communication.Protocol` / `Communication.Transport` | 协议 / 传输抽象；`Abstractions` 只放契约，实现在 `Framing` 等子命名空间 |
| `Plugin.Loader` | 插件发现、校验、隔离加载 |
| `Engine.Router` | 路由与并发调度 |
| `Engine.Runtime` | 通讯内核库（形态 A 直接用） |
| `Host.App` | 现场进程：托管 Runtime + gRPC（形态 B） |
| `Host.Sdk` | 连 Host.App 的客户端库；含两个 UI 共用的 `ValueCodec`（字节序）与 `JsonFileStore`（原子落盘） |
| `Plugins.Protocol.*` | Modbus / Panasonic / Siemens S7 |
| `Plugins.Transport.*` | Tcp / SerialPort |
| `UI.Wpf` / `UI.Web` | 上位机界面，职责对称（见下） |
| `Tests` | 行为与 API 基线，264 个测试 |

gRPC 的 protobuf 包名仍是 `CommunicationKernel.EngineHost.Grpc.V1`（线契约，未改）。

上一代解决方案 `old/` 已移出版本控制，需要查阅：
`git checkout archive/legacy-solution -- old/`

## 两个 UI 的职责划分

页面与视图**只做渲染与交互编排**，所有 I/O 经服务接口，不得直接持有 gRPC 客户端。

| 职责 | WPF | Web |
|---|---|---|
| 会话状态 / 健康轮询 | `HostSessionService` | `HostSession` |
| 设备操作 | `IDeviceService` | `IWebDeviceService` |
| 变量读写 | `IVariableService` | `IWebVariableService` |
| 本地持久化 | `DeviceConfigStore` | `WebDeviceStore` / `WebVariableStore` |
| 落盘实现 | 共用 `Host.Sdk.JsonFileStore`（原子替换） | 同左 |

## Web 上位机（UI.Web）

Blazor Server 操作员客户端。只持有 `route_id` 与 Host.Sdk DTO，不解析协议：

| 页 | 数据来源 |
|---|---|
| MES 监控 | `HostSession` 路由清单 + `WatchRouteStatus` 在线率 |
| 设备管理 | `IWebDeviceService`：`QueryProtocols` / `QuerySerialPorts` / `RegisterRoute` / `RemoveRoute` |
| 变量配置 | 本地 `web-variables.json` + `IWebVariableService` 的 `Read` / `Write` |
| 通讯日志 | 进程内 `AppLogStore` |
| 系统设置 | 与 WPF 共用 `settings.json` 的 `HostAddress` |

进程内单例 `HostSession`：5 秒健康检查、全站一条状态流、Host 恢复后按 `web-devices.json` 对账。
传输介质取值 `Tcp` / `Serial`。

添加设备**只写本地配置、不建连接**——设备未上线时也能提前配好；
连接是显式动作，走卡片上的「连接」或工具栏「一键连接」。

```bash
dotnet run --project CommunicationKernel.UI.Web
```

默认连 `http://localhost:5000`（`appsettings.json` 的 `Host.App:Address`，可被已保存的 settings.json 覆盖）。

## 构建

```bash
dotnet build CommunicationKernel.slnx -c Release
```

```bash
dotnet test CommunicationKernel.Tests -c Release
```

`TreatWarningsAsErrors=true`，带警告即构建失败。

改动 `Host.Sdk` 或 `Engine.Runtime` 的公共 API 会让 `PublicApiSurfaceTests` 失败并列出增删的成员。
确认是有意变更后更新基线，并把 diff 一并提交：

```bash
UPDATE_API_BASELINE=1 dotnet test CommunicationKernel.Tests -c Release
```

远程：`https://github.com/AMXZzzz/CommunicationKernel.git`
