# Copilot Instructions

## 项目指南
- 业务架构要求：同一项目需同时运行多个 UI 层（如 WPF/WebUI/Mac），分别承担不同业务，但共同读写同一设备。
- 用户要求持续按企业级规范思考与实现，避免在 EngineRuntime 持有具体协议耦合。
- 质量要求：整个上位机按企业级标准建设，要求极其严格规范并遵循依赖倒置等设计原则。
- 架构约束：Protocol 与 Transport 必须保持通用抽象层，不感知具体业务协议实现和具体连接路径细节；具体协议与连接参数由上层路由/配置/插件注入。
- 实现约束：Host 采用高性能 gRPC；单项目需支持几十到上百台 PLC 并发访问；插件更新策略为重启生效；代码需添加到代码块级别的详细注释与文件头；在保证分层前提下尽量收紧项目数量。
- 工作方式偏好：新方案的查缺补漏和后续实现必须严格沿用之前 Copilot 会话确定的架构思路与分层原则。
- 分层定义：通讯层下为插件层，通过 DLL 动态更新与扩展协议能力。
- 通讯层能力要求：需支持多种通讯介质。**当前已落地 Tcp 与 Serial**；`TransportKind` 枚举含 Wifi/Bluetooth/Custom，但没有对应插件——不得在 UI 里假装它们可用。
- 架构规范：通讯业务层统一维护所有 PLC 状态与任务；通讯层仅负责收发；协议解析只能在插件内部，其他层禁止出现任何协议解析。
- 架构链路（实际调用顺序）：
  UI → Host.Sdk / Host.App（gRPC）→ EngineRuntime → RouterOrchestrator
  → IProtocolDriver（插件 DLL）→ ITransportClient（Tcp / Serial 插件）。
  协议层来自 DLL 插件，外层一律禁知帧格式与地址语义。

## 结构纪律（2026-08-25 全量审查后确立）

每一条背后都有一次实际发生过的事故，详见 `计划.md` 第一节与第九节。

1. **工程引用必须有实际 `using` 支撑**，禁止"以防万一"式声明——否则纸面依赖图比实际更脏。
2. **`*.Abstractions` 只放接口、委托、枚举与不可变契约**，有状态实现放同项目的其他子命名空间。
3. **UI 页面/视图不得直接持有传输客户端**，所有 I/O 经服务接口（`IDeviceService` / `IWebDeviceService` 等）。
4. **服务只发布事件，不认识视图类型**；切回 UI 线程是订阅方的责任，服务层不得出现 `Dispatcher`。
5. **构造注入优先**，`IServiceProvider` 只用于运行时才知道类型的场景（如按 `Type` 导航）；组合根除外。
6. **本地配置落盘一律经 `Host.Sdk.JsonFileStore`**，禁止直接 `File.WriteAllText`（非原子写掉电会丢整份配置）。

补充约定：

- 对外结果类型统一用 `HostOperationResult` 派生体系，**禁止在 UI 层另定义同形状的结果类型**；
  公共 API 禁止返回匿名 `ValueTuple`。
- 字节序换算一律走 `Host.Sdk.ValueCodec` 并显式传入设备配置的 `ByteOrder`，
  禁止直接用 `BitConverter`——本机是小端，协议插件上抛的是大端。
- 两个 UI 的平行实现**刻意不强行合并**（功能并不对等）；只抽取真正同源的 substrate。
- 改动 `Host.Sdk` / `Engine.Runtime` 公共 API 需同步更新 `ApiBaselines/` 并让 diff 进评审。
- **UI 不得硬编码协议列表或串口列表**。协议来自 `QueryProtocols`，串口来自 `QuerySerialPorts`（列的是宿主机器上的口）。
- 串口三层命名必须分开：引擎 `SerialPortInfo`、gRPC `SerialPortDescriptor`、SDK `SerialPortDto`。禁止再引入第四个同义类型。
- Panasonic 的 ProtocolId 是 `panasonic-mewtocol`（TCP 与串口共用），没有 `-tcp` / `-serial` 后缀。
- `RegisterRoute` 会真正 `ConnectAsync`；连不上则整条路由不入表。UI「添加设备」只写本地配置。
  失败用 `RegisterFailureKind` 区分不可达与配置错——不可达应保留配置。
- 读写互斥在 `RouteEntry.ExecuteExclusiveAsync`（读+写同一把锁，内含串口帧间静默）。
  **不要**再引入 `WriteScheduler` / `SerialIoGate` / `SubscriptionHub`——这三件已经从 Router 层删除。
- `length` 的单位一律是**字节**。插件自行换算到本协议计数单位；奇数长度不得静默向下取整。
- 站号只写在设备配置里，地址中不接受 `1:40001` 这类站号前缀。
- protobuf 只有根目录 `Protos/V1/engine_host.proto` 一份，禁止再复制。
- `计划.md` 第十节清单是历史待办，不作为进度来源；进度以代码为准。
