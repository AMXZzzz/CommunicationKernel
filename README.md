# CommunicationKernel

企业级上位机通信内核（.NET 8）。协议解析与物理连接只发生在现场宿主，
UI 只持有 `route_id` 与 SDK DTO。

## 文档

| 文件 | 内容 |
|------|------|
| 本 README | 怎么跑、怎么构建、分层一览 |
| [`计划.md`](计划.md) | 金字塔红线、数据流、协议地址、事故档案。**改代码前先读第一节。** |
| [`部署-Linux与树莓派.md`](部署-Linux与树莓派.md) | 现场网关发布、监听、串口、systemd |
| [`.github/copilot-instructions.md`](.github/copilot-instructions.md) | 助手硬约束（与结构纪律同源） |

## 金字塔（L0 → L7）

依赖只允许向下。唯一的反向流是 `EngineRuntime.RouteStatusChanged`（下层发布、上层订阅）。

```
L7  UI.Wpf / UI.WebMaster     渲染与编排；I/O 只经服务接口
L6  Hosting.Sdk / Hosting.App 唯一入口。Sdk 零工程引用；App 托管 Runtime + gRPC
L5  Core.EngineRuntime        路由生命周期、链路巡检、单次重连
L4  Core.EngineRouter         路由表 + 同键读合并；读写互斥在 RouteEntry
L3  Plugin.Context            ALC 隔离加载，只认 Core.Abstractions
L2  Plugins.Protocol.*        协议知识全部封在这里
L1  Core.Transport + 传输插件 字节级收发（Tcp / Serial）
L0  Core.Abstractions         契约根，零工程引用
```

## 两种运行形态

| | 形态 A | 形态 B |
|---|---|---|
| 谁持有引擎 | `UI.WebMaster` 同进程带上 `Hosting.App` | 现场独立 `Hosting.App.exe` |
| 口 | Blazor `:64000` + gRPC `:5000` | 仅 gRPC `:5000` |
| UI 怎么连 | `HostingClient` → `127.0.0.1:5000` | `HostingClient` → 现场地址 |
| 适用 | 本机监控、手机同一 WiFi | 树莓派网关 + 办公室 WPF；多台上位机 |

本机同时只能有一份引擎：开着 WebMaster 就不要再开 `Hosting.App.exe`。
第三方嵌入直连 `EngineRuntime`（`StaticRouteAssemblyService`）见部署文档形态 A。

## 怎么跑

```bash
# 本机上位机（托盘 + 浏览器 :64000，内含宿主 :5000）
dotnet run --project CommunicationKernel.UI.WebMaster

# 本机再开 WPF，默认连 http://localhost:5000
dotnet run --project CommunicationKernel.UI.Wpf

# 无界面现场网关（不要和 WebMaster 同时开）
dotnet run --project CommunicationKernel.Hosting.App
```

Web：`http://localhost:64000`，同一 WiFi 手机 `http://<电脑IP>:64000`。
不要把 Web 口改成 `5000`。配置在各 exe 旁 `config/`，互不影响。

## 协议（对照用，UI 禁止硬编码此表）

清单运行时来自 `QueryProtocols`。

| ProtocolId | 介质 | 默认端口 | 站号 |
|---|---|---|---|
| `modbus-tcp` | Tcp | 502 | 要 |
| `modbus-rtu` / `modbus-ascii` | Serial / Tcp | — | 要 |
| `panasonic-mewtocol` | Serial / Tcp | 9094 | 要（1–99） |
| `siemens-s7-1200` / `siemens-s7-200smart` | Tcp | 102 | 不要 |

Panasonic **没有** `-tcp` / `-serial` 后缀。地址格式见 [`计划.md`](计划.md) 第七节。
传输字面量只有 **`Tcp` / `Serial`**。

串口三层命名：引擎 `SerialPortInfo` · gRPC `SerialPortDescriptor` · SDK `SerialPortDto`。

protobuf 只有一份：[`Protos/V1/hosting.proto`](Protos/V1/hosting.proto)。

## 构建

```bash
dotnet build CommunicationKernel.slnx -c Release
dotnet test CommunicationKernel.Tests -c Release
```

`TreatWarningsAsErrors=true`。WPF 只能在 Windows 构建。
改 `Hosting.Sdk` / `Core.EngineRuntime` 公共 API 须同步 `Tests/ApiBaselines/`：

```bash
UPDATE_API_BASELINE=1 dotnet test CommunicationKernel.Tests -c Release
```

远程：`https://github.com/AMXZzzz/CommunicationKernel.git`
