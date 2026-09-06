# Copilot Instructions

本文件是**助手硬约束**。与 [`规范.md`](../规范.md) 同源；两者冲突时以 `规范.md` 为准，并应立即同步本文件。
逐类逐方法的实现细节见 [`架构.md`](../架构.md)。

## 项目指南

- 业务架构要求：同一项目需同时运行多个 UI 层（如 WPF/WebUI/Mac），分别承担不同业务，但共同读写同一设备。
- 用户要求持续按企业级规范思考与实现，避免在 EngineRuntime 持有具体协议耦合。
- 质量要求：整个上位机按企业级标准建设，要求极其严格规范并遵循依赖倒置等设计原则。
- 架构约束：Protocol 与 Transport 必须保持通用抽象层，不感知具体业务协议实现和具体连接路径细节；具体协议与连接参数由上层路由/配置/插件注入。
- 实现约束：Host 采用高性能 gRPC；单项目需支持几十到上百台 PLC 并发访问；插件更新策略为重启生效；代码需添加到代码块级别的详细注释与文件头；在保证分层前提下尽量收紧项目数量。
- 工作方式偏好：新方案的查缺补漏和后续实现必须严格沿用之前会话确定的架构思路与分层原则。
- 分层定义：通讯层下为插件层，通过 DLL 动态更新与扩展协议能力。
- 通讯层能力要求：需支持多种通讯介质。**当前已落地 Tcp 与 Serial**；`TransportKind` 枚举含 Wifi/Bluetooth/Custom，但没有对应插件——不得在 UI 里假装它们可用。
- 架构规范：通讯业务层统一维护所有 PLC 状态与任务；通讯层仅负责收发；协议解析只能在插件内部，其他层禁止出现任何协议解析。
- 架构链路（实际调用顺序）：
  UI → Hosting.Sdk / Hosting.App（gRPC）→ EngineRuntime → RouterOrchestrator
  → IProtocolDriver（插件 DLL）→ ITransportClient（Tcp / Serial 插件）。
  协议层来自 DLL 插件，外层一律禁知帧格式与地址语义。
  UI.WebMaster 把 Hosting.App 带进同一进程，HostingClient 打 127.0.0.1:5000；
  不要再给 Web 做「切换远端 Host 地址」——拆开时只改客户端目标。

## 结构纪律

每一条背后都有一次实际发生过的事故，档案见 `规范.md` 第七节。

1. **工程引用必须有实际 `using` 支撑**，禁止"以防万一"式声明——否则纸面依赖图比实际更脏。
2. **`*.Abstractions` 只放接口、委托、枚举与不可变契约**，有状态实现放同项目的其他子命名空间。
   判据是**有没有状态**，不是叫什么名：`RouteEntry` 持有信号量与时间戳 → `EngineRouter/Runtime/`；`FrameReader` 持有残留缓冲 → `Core.Transport/Framing/`。
   命名空间约定：`Abstractions/` → `.Abstractions`；`Models/` → `.Models`；`Runtime/` → **根命名空间，不加后缀**。
3. **UI 页面/视图不得直接持有传输客户端**，所有 I/O 经服务接口（`IDeviceService` / `IWebDeviceService` 等）。
4. **服务只发布事件，不认识视图类型**；切回 UI 线程是订阅方的责任，服务层不得出现 `Dispatcher`。
   当前唯一遗留点：`UI.Wpf/Services/VariablePollingService.cs`（3 处）。
   `UI.Wpf/ViewModels/DeviceListModel.cs` 里的 `Dispatcher` 是**合规的**——它是订阅方。
5. **构造注入优先**，`IServiceProvider` 只用于运行时才知道类型的场景（如按 `Type` 导航）；组合根除外。
6. **本地配置落盘一律经 `Hosting.Sdk.JsonFileStore`**，禁止直接 `File.WriteAllText`（非原子写掉电会丢整份配置）。

## 补充约定

- 对外结果类型统一用 `HostingOperationResult` 派生体系，**禁止在 UI 层另定义同形状的结果类型**；
  公共 API 禁止返回匿名 `ValueTuple`。
- 字节序换算一律走 `Hosting.Sdk.ValueCodec` 并显式传入设备配置的 `ByteOrder`，
  禁止直接用 `BitConverter`——本机是小端，协议插件上抛的是大端。
- 两个 UI 的平行实现**刻意不强行合并**（功能并不对等）；只抽取真正同源的 substrate。
  **但功能可以不对等、纪律必须对等**，不允许其中一方违纪。
- 改动 `Hosting.Sdk` / `Core.EngineRuntime` 公共 API 需同步更新 `ApiBaselines/` 并让 diff 进评审。
  用 `UPDATE_API_BASELINE=1 dotnet test --filter PublicApiSurface` 重生成，不要手改。
- **UI 不得硬编码协议列表或串口列表**。协议来自 `QueryProtocols`，串口来自 `QuerySerialPorts`（列的是宿主机器上的口）。
- 串口三层命名必须分开：引擎 `SerialPortInfo`、gRPC `SerialPortDescriptor`、SDK `SerialPortDto`。禁止再引入第四个同义类型。
- Panasonic 的 ProtocolId 是 `panasonic-mewtocol`（TCP 与串口共用），没有 `-tcp` / `-serial` 后缀。
- `RegisterRoute` 会真正 `ConnectAsync`；连不上则整条路由不入表。UI「添加设备」只写本地配置。
  失败用 `RegisterFailureKind` 区分不可达与配置错——不可达应保留配置。
- 读写互斥在 `RouteEntry.ExecuteExclusiveAsync`（读+写同一把锁，内含串口帧间静默）。
  **不要**再引入 `WriteScheduler` / `SerialIoGate` / `SubscriptionHub`——这三件已经从 Router 层删除。
- `length` 的单位一律是**字节**。插件自行换算到本协议计数单位；奇数长度不得静默向下取整。
- 站号只写在设备配置里，地址中不接受 `1:40001` 这类站号前缀。
- protobuf 只有根目录 `Protos/V1/hosting.proto` 一份，禁止再复制。
- `规范.md` 是架构与纪律，不是待办清单。进度以代码为准。

## 极易写错的几处语义

写到这些地方时**必须停下来核对**，它们都以"不报错但结果不对"的形式失败。

- **`minIoIntervalMs` 传 0 与省略不等价。** `IHostingClient.RegisterRouteAsync` 的签名默认值是 `100`，
  而引擎的判定是 `cmd.MinIoIntervalMs > 0 ? 用它 : (串口?默认:0)`——
  100 是正数，会被原样采用，**连 TCP 路由也被塞进 100ms 帧间静默**，等于把链路限死在 10 次 I/O／秒。
  调用 `RegisterRouteAsync` 必须显式传 `0` 或设备实际配置值。
- **`FrameReader` 的残留缓冲，在"读一帧之内"要保留、在"发新请求之前"要丢弃。** 两种相反处理取决于阶段。
  且 `DiscardResidual()` 够不着 socket 的**内核**缓冲——所以还需要 `DrainSocketBuffer()` 与 `_desynced` 标记。
- **同一个 `KernelErrorCode` 的可重连性必须全链路一致。**
  `EngineRuntime.ShouldAttemptReconnect`、`ProtocolProbe`、`Hosting.Sdk.RegisterFailure.Classify` 三处不得分叉。
  分叉过一次：引擎准备重连，UI 却已把本地配置删了。
- **`DeviceRecord → DeviceInfo` 的映射有三个入口**，新增字段必须三处同步。
  漏一处的表现是"改了能保存、下次打开又变回默认值"，不报任何错。
- **`IHostedService.StartAsync` 是串行 `await`**。在里面 `await` 长循环，排在后面的 HostedService 与
  `ApplicationStarted` 回调都不会执行。一律 `Task.Run` 后立即返回。
- **即发即忘的事件不缓存。** 在组合根里触发这类操作（如 `IDeviceService.Load()`）之前，
  必须先把订阅者解析出来，否则首次结果直接丢失。这类 bug 编译与单元测试都发现不了。

## 写代码时的硬性要求

- **文件头注释**：文件路径、所属层级、作用；若存在非显然的设计取舍，写清"为什么不是另一种写法"。
- **分支级注释**：解释**为什么**，不是复述代码在做什么。优先写清"这里写错会怎样"。
- 注释风格：文件内保持一致。本仓库同时存在 `///` XML 文档与 `//!` 行注释两种，**不要在同一文件里混用**。
- `TreatWarningsAsErrors=true`：未使用的 using、未使用的参数、不可达代码都会让构建失败。
- **WPF 层零自动化测试覆盖**（测试项目 `net8.0` 无法引用 `net8.0-windows`）。
  改 `UI.Wpf` 之后必须说明这一点，并给出手动冒烟的检查项。
- 行尾：默认 CRLF；`*.sh` / `*.yml` / `ApiBaselines/*.txt` 为 LF。由 `.gitattributes` 强制。
  新建文件后确认行尾符合规则——`.sh` 带 CRLF 会让树莓派上的脚本直接无法执行。
