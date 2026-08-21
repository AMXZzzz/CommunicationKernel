# Copilot Instructions

## 项目指南
- 用户明确要求进行彻底重构，不接受最小落地改造方案。
- 标题栏连接状态仅用于显示 EngineHost 在线/离线，不表示本地/远程模式。
- WPF 必须保留本地直连 Business/PLC 能力，作为后手保障；多端 Web/移动端通过 EngineHost 访问。
- 项目应作为通用模板：Business层仅负责通讯能力与通用读写，不承载场景业务；场景业务（如一键修改所有设备宽度）应放在上层可替换逻辑中，以便复用同一Business并仅替换UI/上层逻辑。
- 针对 WPF 项目，后续涉及变量页交互问题时应优先检查 CommunicationDebuggingTools 的 WPF 代码。