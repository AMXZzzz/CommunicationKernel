# CommunicationKernel

企业级上位机通信内核（.NET 8），面向多 UI 并发访问同一批 PLC 的场景。

## 架构目标

- 多端 UI（WPF/WebUI/Mac/Android/鸿蒙/Linux）并行接入
- Host 作为统一入口（高性能 gRPC）
- Router 统一并发调度（同路由串行写、同键读合并、跨路由并行）
- Protocol / Transport 抽象解耦
- 插件 DLL 扩展协议与介质能力（重启生效）
- 全局严格质量门禁：`TreatWarningsAsErrors=true`

## 解决方案项目

- `CommunicationKernel.Core.Abstractions`：错误码、结果模型、版本契约
- `CommunicationKernel.Core.Runtime`：运行时承载（持续完善）
- `CommunicationKernel.Communication.Transport`：传输层抽象
- `CommunicationKernel.Communication.Protocol`：协议层抽象
- `CommunicationKernel.Plugin.Runtime`：插件发现/校验/隔离加载
- `CommunicationKernel.Engine.Router`：路由与并发调度核心
- `CommunicationKernel.EngineHost`：Host 门面与 gRPC 服务
- `CommunicationKernel.Contracts`：跨层 DTO 契约
- `CommunicationKernel.Tests`：核心行为测试

## 当前 gRPC 能力（EngineHost）

- `Health`
- `GetDiagnostics`
- `QueryRoutes`
- `Read`
- `Write`
- `RegisterRoute`（当前阶段未启用真实组装，严格返回阶段性失败）

## 构建

```bash
dotnet build CommunicationKernel.slnx
```

## 备注

- 本仓库已初始化独立 Git 仓库，并关联远程：
  - `origin = https://github.com/AMXZzzz/CommunicationKernel.git`
