# CommunicationKernel

企业级上位机通信内核（.NET 8）。多台上位机并发访问同一批 PLC 时，
协议解析与物理连接只发生在现场宿主，UI 只持有 `route_id` 与 SDK DTO。

## 文档地图

| 文件 | 读什么 |
|------|--------|
| 本 README | 形态、分层、怎么跑、怎么构建 |
| [`计划.md`](计划.md) | 分层红线、结构纪律、数据流、协议地址、事故档案。**新增代码前先读第一节。** |
| [`部署-Linux与树莓派.md`](部署-Linux与树莓派.md) | 现场网关发布、监听地址、串口权限、systemd、升级回滚 |
| [`.github/copilot-instructions.md`](.github/copilot-instructions.md) | 给助手的硬约束（与结构纪律同源） |

第十节待办清单是历史存档，**进度以代码为准**，不要按勾选框推断实现状态。

## 两种形态

- **形态 A（嵌入）**：`UI.WebMaster` 把 `Hosting.App` 带进同一进程（Blazor :64000 + gRPC :5000）。UI 仍走 `Hosting.Sdk` 连 `127.0.0.1:5000`，以后要拆开只改地址。不必再开独立的 `Hosting.App.exe`。
- **形态 B（独立宿主）**：现场只跑 `Hosting.App`；上位机（WPF / 拆开后的 Web）引用 `Hosting.Sdk`，通过 gRPC 远程读写。
  多台上位机同时访问同一批 PLC **只能用形态 B**——形态 A 里引擎在 WebMaster 进程内。
  本机同时只能有一份宿主：开着 WebMaster 就不要再开 `Hosting.App.exe`。

跨机部署（树莓派当网关、办公室当上位机）走形态 B，步骤见 [部署文档](部署-Linux与树莓派.md)。

## 分层

调用链严格自上而下，依赖图无环。唯一的反向流是 `EngineRuntime.RouteStatusChanged`
事件——下层发布、上层订阅，不产生反向引用。

```
L7  UI.Wpf / UI.WebMaster          只持有 route_id 与 SDK DTO
L6  Hosting.Sdk / Hosting.App      唯一入口；Hosting.Sdk 零工程引用，UI 无法绕过它触达内部
L5  Core.EngineRuntime           路由生命周期、轮询、链路巡检、单次重连
L4  Core.EngineRouter            路由表 + 同键读合并；读写互斥在 RouteEntry 独占门
L3  Plugin.Context            ALC 隔离加载，只认 Core.Abstractions
L2  Plugins.Protocol.*       协议知识全部封在这里，外层一律禁知
L1  Core.Transport  字节级收发，不解释内容（现实现：Tcp / Serial）
L0  Core.Abstractions        契约根，零工程引用
```

完整的分层约束、职责红线与**结构纪律六条**见 [`计划.md`](计划.md) 第一节。

## 解决方案项目（16 个，含测试）

| 项目 | 角色 |
|------|------|
| `Core.Abstractions` | 错误码、结果模型、版本契约 |
| `Core.Protocol` / `Core.Transport` | 协议 / 传输抽象；`Abstractions` 只放契约，实现在 `Framing` 等子命名空间 |
| `Plugin.Context` | 插件发现、校验、隔离加载 |
| `Core.EngineRouter` | 路由表、读合并、`RouteEntry` 独占 I/O 门控 |
| `Core.EngineRuntime` | 通讯内核库（形态 A 直接用） |
| `Hosting.App` | 现场进程：托管 Runtime + gRPC（形态 B） |
| `Hosting.Sdk` | 连 Hosting.App 的客户端库；含两个 UI 共用的 `ValueCodec` 与 `JsonFileStore` |
| `Plugins.Protocol.*` | Modbus / Panasonic MEWTOCOL / Siemens S7 |
| `Plugins.Transport.*` | Tcp / SerialPort（`TransportKind` 枚举另有 Wifi/Bluetooth，尚无插件） |
| `UI.Wpf` / `UI.WebMaster` | 上位机界面，职责对称（见下） |
| `Tests` | 行为与 API 基线 |

gRPC 的 protobuf **只有一份**：根目录 [`Protos/V1/hosting.proto`](Protos/V1/hosting.proto)，
包名 `CommunicationKernel.Hosting.Grpc.V1`。

上一代解决方案 `old/` 已移出版本控制，需要查阅：
`git checkout archive/legacy-solution -- old/`

## 协议与默认端口

协议清单**只来自宿主** `QueryProtocols`，UI 禁止硬编码这份表。下列 ID 供对照文档与排障：

| ProtocolId | 介质 | 默认 TCP 端口 | 站号 |
|---|---|---|---|
| `modbus-tcp` | Tcp | 502 | 要 |
| `modbus-rtu` | Serial / Tcp | — | 要 |
| `modbus-ascii` | Serial / Tcp | — | 要 |
| `panasonic-mewtocol` | Serial / Tcp | 9094 | 要（1–99） |
| `siemens-s7-1200` | Tcp | 102 | 不要 |
| `siemens-s7-200smart` | Tcp | 102 | 不要 |

Panasonic **没有** `panasonic-mewtocol-tcp` 这个 ID——TCP 与串口共用一个 ProtocolId，
帧格式与介质无关。地址格式见 [`计划.md`](计划.md) 第七节。

传输介质取值 **`Tcp` / `Serial`**（大小写在装配时忽略，写入配置请用这对字面量）。

串口三层命名必须分开，禁止混用：

| 层 | 类型 |
|---|---|
| 引擎 / 传输 | `SerialPortInfo` |
| gRPC 线上契约 | `SerialPortDescriptor` |
| Hosting.Sdk / UI | `SerialPortDto` |

## 两个 UI 的职责划分

页面与视图**只做渲染与交互编排**，所有 I/O 经服务接口，不得直接持有 gRPC 客户端。

| 职责 | WPF | Web |
|---|---|---|
| 会话状态 / 健康轮询 | `HostingSessionService` | `EngineSession` |
| 设备操作 | `IDeviceService` | `IWebDeviceService`（`Connect` / `Disconnect` / 查询） |
| 变量读写 | `IVariableService` | `IWebVariableService` |
| 本地持久化 | `devices.json` / `variables.json` | `web-devices.json` / `web-variables.json` |
| Host 地址 | 本 exe 旁 `config/settings.json`（WPF / Web 各一份） | 同左，互不影响 |
| 落盘实现 | 设备/变量走 `Hosting.Sdk.JsonFileStore`（原子替换） | 三个 Store 均走 `JsonFileStore` |

两端**添加设备都先写本地配置**，PLC 未上电也能录。真正建链是 `RegisterRoute`（内部会 `ConnectAsync`，失败则整条路由不入表）：

- **Web**：卡片「连接」/「一键连接」才 `RegisterRoute`。宿主从离线恢复时 `EngineSession.ReconcileAsync` 按 `web-devices.json` 自动补注册。
- **WPF**：`Add` 会立刻尝试 `RegisterRoute`；失败且判定为「目标不可达」时仍保留本地卡片。宿主丢路由后由 `IRouteReconciler.EnsureRouteAsync` 在下次读写/轮询时补注册。

`RegisterRoute` 失败用 `RegisterFailureKind` 分类：`Unreachable` 保留配置，`BadConfiguration` 不该入库。

字节序：Web 按设备 `ByteOrder`（`ABCD` / `CDAB` / `BADC` / `DCBA`）走 `ValueCodec`；
WPF 变量编解码目前固定 `ABCD`，尚无按设备配置的界面。

## Web 上位机（UI.WebMaster）

Blazor Server 操作员客户端。只持有 `route_id` 与 Hosting.Sdk DTO，不解析协议：

| 页 | 路由 | 数据来源 |
|---|---|---|
| MES 监控 | `/` | `EngineSession` 路由清单 + `WatchRouteStatus` 在线率 |
| 设备管理 | `/devices` | `IWebDeviceService`：`QueryProtocols` / `QuerySerialPorts` / `Connect` / `Disconnect` |
| 变量配置 | `/variables` | 本地 `web-variables.json` + `IWebVariableService` 的 `Read` / `Write` |
| 通讯日志 | `/log` | 进程内 `AppLogStore` |
| 系统设置 | `/settings` | Web 监听端口；本进程已带 Hosting.App |

进程内单例 `EngineSession`：5 秒健康检查、全站一条状态流、按 `web-devices.json` 对账。
**形态 A**：WebMaster 本进程带上 `Hosting.App`（Blazor :64000 + gRPC :5000），UI 经 `Hosting.Sdk` 连回环。不必再开独立 exe。
WPF 可直接连本机 `http://localhost:5000`。树莓派无界面仍用独立的 `Hosting.App`。
Windows 下双击 exe 驻留在右下角托盘：关浏览器不会退出。托盘右键可打开界面、查看日志、退出。
再双击一次 exe 会唤出已在跑的实例，而不是再起一份。Visual Studio 按 Windows 应用启动，不再配控制台黑框。已有终端里 `dotnet run` 时日志仍打到该终端（那是你自己开的窗口）。

```bash
dotnet run --project CommunicationKernel.UI.WebMaster
```

默认听 `http://0.0.0.0:64000`：本机浏览器打开 `http://localhost:64000`，
同一 WiFi 的手机打开 `http://<电脑IP>:64000`（启动日志会打印这条地址）。
端口可在系统设置页改，写入本 exe 旁 `config/web-listen.json`，
重启 Web 后生效；不要改成 `5000`（本进程 gRPC / WPF 用这个口）。
双击 exe 同样生效，不必加 `--urls`，也不必先起 Hosting.App.exe。

## WPF 上位机（UI.Wpf）

页面：MES 监控 / 变量配置 / 设备管理 / 日志 / 系统设置。`net8.0-windows`，只能在 Windows 构建。

```bash
dotnet run --project CommunicationKernel.UI.Wpf
```

同样默认连 `http://localhost:5000`，出厂值在本项目 `appsettings.json` 的 `Hosting.App:Address`。
系统设置里保存的地址写入本 exe 旁 `config/settings.json`，与 Web 互不影响，优先于 appsettings。

## 运行宿主（形态 B）

本机已经开着 WebMaster 时**不要**再开 Hosting.App.exe（同一份引擎、同一 :5000）。
树莓派 / 无界面现场才单独起：

```bash
dotnet run --project CommunicationKernel.Hosting.App
```

默认绑 `http://localhost:5000`、明文 HTTP/2。浏览器打开该地址会失败，**这是正常的**
（gRPC 明文 h2c，浏览器不做）。跨机部署必须改成 `0.0.0.0`，见 [部署文档](部署-Linux与树莓派.md)。

启动日志应出现：

```
已加载 6 个协议：modbus-ascii, modbus-rtu, modbus-tcp, panasonic-mewtocol, siemens-s7-1200, siemens-s7-200smart
```

数量为 0 且无异常 = 四个共享契约被误拷进了 `plugins/`，插件静默注册不上。CI 有作业守这条。

## 构建与测试

```bash
dotnet build CommunicationKernel.slnx -c Release
dotnet test CommunicationKernel.Tests -c Release
```

`TreatWarningsAsErrors=true`，带警告即构建失败。WPF 只能在 Windows 上构建；
Linux CI 跑的是不含 WPF 的子集，见 [`.github/workflows/ci.yml`](.github/workflows/ci.yml)。

改动 `Hosting.Sdk` 或 `Core.EngineRuntime` 的公共 API 会让 `PublicApiSurfaceTests` 失败并列出增删的成员。
确认是有意变更后更新基线，并把 diff 一并提交：

```bash
UPDATE_API_BASELINE=1 dotnet test CommunicationKernel.Tests -c Release
```

基线文件：`CommunicationKernel.Tests/ApiBaselines/`。

远程：`https://github.com/AMXZzzz/CommunicationKernel.git`
