# CommunicationKernel

企业级上位机通信内核（.NET 8）。协议解析与物理连接只发生在现场宿主，
UI 只持有 `route_id` 与 SDK DTO。

支持 Modbus（TCP / RTU / ASCII）、Panasonic MEWTOCOL、Siemens S7，
介质为 TCP 与串口，协议以 DLL 插件形式加载。单进程实测可支撑 120 台 PLC 满负荷并发。

## 文档

| 文件 | 内容 | 什么时候看 |
|------|------|---|
| 本 README | 怎么跑、怎么构建、分层一览 | 第一次接触这个仓库 |
| [`规范.md`](规范.md) | 金字塔红线、结构纪律、**事故档案**、易错语义、仓库约定 | **改代码前先读第一节与第七节** |
| [`架构.md`](架构.md) | 逐项目、逐类、逐方法的实现与调用链；端到端时序 | 要看懂某段代码在干什么 |
| [`部署-Linux与树莓派.md`](部署-Linux与树莓派.md) | 现场网关发布、监听、串口、systemd、公网中转 | 要把它装到现场 |
| [`打包发布-Windows安装包.md`](打包发布-Windows安装包.md) | Windows 安装向导：打包、升级、卸载 | 要发给别人装 |
| [`.github/copilot-instructions.md`](.github/copilot-instructions.md) | 助手硬约束（与结构纪律同源） | 让 AI 改这个仓库之前 |

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

本机同时只能有一份引擎：开着 WebMaster 就不要再开 `Hosting.App.exe`（有互斥量拦着，但会看到启动失败）。
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
全国访问 Web：车间照常跑 WebMaster，公网 1Panel/宝塔只做 HTTPS 中转，步骤见 [部署文档 · 公网中转](部署-Linux与树莓派.md#公网中转全国访问-web)。

**添加设备 ≠ 连接设备。** Web 端「添加」只写本地 JSON，点卡片上的「连接」才会真正建立物理连接；
WPF 端「添加」会立刻注册（连不上也保留卡片并显示离线，等对账自动补注册）。

## 协议（对照用，UI 禁止硬编码此表）

清单运行时来自 `QueryProtocols`。

| ProtocolId | 介质 | 默认端口 | 站号 |
|---|---|---|---|
| `modbus-tcp` | Tcp | 502 | 要 |
| `modbus-rtu` / `modbus-ascii` | Serial / Tcp | — | 要 |
| `panasonic-mewtocol` | Serial / Tcp | 9094 | 要（1–99） |
| `siemens-s7-1200` / `siemens-s7-200smart` | Tcp | 102 | 不要 |

Panasonic **没有** `-tcp` / `-serial` 后缀。地址格式见 [`规范.md` 第五节](规范.md#五协议地址)。
传输字面量只有 **`Tcp` / `Serial`**。

串口三层命名：引擎 `SerialPortInfo` · gRPC `SerialPortDescriptor` · SDK `SerialPortDto`。

protobuf 只有一份：[`Protos/V1/hosting.proto`](Protos/V1/hosting.proto)。

## 插件

协议与传输都是插件，从 exe 旁的 `plugins/` 目录经 `AssemblyLoadContext` 隔离加载，**重启生效**。

> **共享契约的四个 DLL 永远不能出现在 `plugins/` 里。**
> ALC 会为它们造出第二份类型，插件实现的 `IProtocolDriverFactory` 与宿主认识的不是同一个接口，
> 于是**所有插件静默注册失败**——没有异常、没有日志，只是协议列表变空。
> 启动日志里的「已加载 N 个协议」为 0 时先查这个。

## 构建与测试

```bash
dotnet build CommunicationKernel.slnx -c Release
dotnet test CommunicationKernel.Tests -c Release
```

`TreatWarningsAsErrors=true`——未使用的 using、未使用的参数、不可达代码都会让构建失败。
WPF 只能在 Windows 构建。

改 `Hosting.Sdk` / `Core.EngineRuntime` 公共 API 须同步 `Tests/ApiBaselines/`：

```bash
UPDATE_API_BASELINE=1 dotnet test CommunicationKernel.Tests -c Release --filter PublicApiSurface
```

> **测试项目是 `net8.0`，`UI.Wpf` 是 `net8.0-windows`，框架不兼容因而无法引用——
> WPF 层零自动化覆盖。** 改动 `UI.Wpf` 之后必须手动跑一遍界面验证。

### 压测

`tools/SoakHarness/` 刻意不在解决方案里（它直连 `EngineRuntime`，不经 gRPC，不属于产品代码）：

```bash
dotnet run -c Release --project tools/SoakHarness -- --minutes 10 --plcs 120
```

每台 PLC 一个独立回环从站、一条独立连接、一个独立门控，形态是「单机串行、多机并行」——
与现场一致。所有路由打同一个端口测出来的是另一回事。

## 仓库约定

- **行尾由 `.gitattributes` 强制**：默认 CRLF；`*.sh` / `*.yml` / `ApiBaselines/*.txt` 为 LF。
  `.sh` 带 CRLF 会让树莓派上的脚本报 `bad interpreter` 而无法执行。
  改 `.gitattributes` 必须同步改 `.editorconfig`。
- 源文件为 UTF-8。含中文的文件已在 `.gitattributes` 里显式声明为 `text`，避免被 git 的二进制启发式误判后损坏内容。
- 代码风格由 `.editorconfig` + `EnforceCodeStyleInBuild` 约束，违规在构建期可见。

远程：`https://github.com/AMXZzzz/CommunicationKernel.git`
