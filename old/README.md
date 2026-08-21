# CommunicationDebuggingTools

工业通信调试工具（WPF + EngineHost + WebUI）。

- **WPF 保留本地直连能力**（Business/PLC），用于后手保障。
- **EngineHost 提供统一后端能力**（gRPC + Web API）。
- **WebUI 通过 EngineHost 访问**，用于多端（浏览器/平板）使用。

---

## 1. 架构说明

### 1.1 运行链路

1) WPF 本地直连（兜底）  
`WPF -> Business -> Plugin -> PLC`

2) WPF 远端模式  
`WPF -> Client(gRPC) -> EngineHost -> Business -> Plugin -> PLC`

3) WebUI  
`WebUI -> Client(gRPC) -> EngineHost -> Business -> Plugin -> PLC`

### 1.2 端口

- `5100`：EngineHost gRPC（HTTP/2）
- `5101`：EngineHost Web API（HTTP/1.1）

---

## 2. 解决方案项目

- `CommunicationDebuggingTools`：WPF 主程序
- `CommunicationDebuggingTools.EngineHost`：后端宿主（gRPC + REST）
- `CommunicationDebuggingTools.WebUI`：Blazor Server 独立 Web 前端
- `CommunicationDebuggingTools.Client`：EngineHost 客户端 SDK
- `CommunicationDebuggingTools.Business`：业务实现（设备、变量、轮询、插件调度）
- `CommunicationDebuggingTools.Core`：核心契约（模型、枚举、接口）
- `CommunicationDebuggingTools.Contracts`：gRPC 协议契约（proto 生成）
- `Plugin.ModbusTcp` / `Plugin.Panasonic` / `Plugin.SiemensS7`：协议插件
- `CommunicationDebuggingTools.Tests`：测试项目

---

## 3. 已实现能力（当前）

### 3.1 EngineHost

- 设备 API：列表、新增、更新、删除、连接、断开、全部断开
- 变量 API：列表、新增、更新、删除、读、写
- 状态 API：服务状态、协议列表
- gRPC 服务：与 Contracts 同步

### 3.2 WebUI

- 设备管理：新增/编辑弹窗、单删/批删、连接控制、状态提示
- 变量配置：新增/编辑弹窗、单删/批删、读写、状态提示
- 变量 CSV：导入/导出
- 日志页、设置页（Host 地址保存与重连）

### 3.3 WPF

- 保留本地直连链路（后手保障）
- 可探测 EngineHost 在线状态

---

## 4. 快速启动

### 4.1 构建

```bash
dotnet build CommunicationDebuggingTools.sln
```

### 4.2 启动 EngineHost

```bash
dotnet run --project CommunicationDebuggingTools.EngineHost/CommunicationDebuggingTools.EngineHost.csproj
```

### 4.3 启动 WebUI

```bash
dotnet run --project CommunicationDebuggingTools.WebUI/CommunicationDebuggingTools.WebUI.csproj
```

> WebUI 实际监听地址以控制台输出为准（开发环境通常是 `http://localhost:5096` 一类端口）。

### 4.4 启动 WPF

在 Visual Studio 启动 `CommunicationDebuggingTools` 项目即可。

---

## 5. 开发约定（重要）

- 标题栏连接状态仅表示 **EngineHost 在线/离线**，不表示本地/远程模式。
- WPF 必须保留本地直连 Business/PLC 能力，作为后手保障。
- Web/移动端统一通过 EngineHost 访问。

---

## 6. 后续建议

- 增加鉴权（JWT/角色）
- 增强审计日志（写操作留痕）
- 将 WebUI 与 EngineHost 发布脚本统一
