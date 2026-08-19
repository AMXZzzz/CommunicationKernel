# EngineHost

C# 引擎进程：内部 Business / Core / Plugin，对外 **gRPC**（`http://127.0.0.1:5100`）。

## 运行

```bash
dotnet build ../Plugin.ModbusTcp -c Debug
dotnet build ../Plugin.Panasonic -c Debug
dotnet build ../Plugin.SiemensS7 -c Debug
dotnet run --project CommunicationDebuggingTools.EngineHost.csproj
```

浏览器打开 `/` 可见欢迎页（HTTP/1.1）。gRPC 请用客户端（grpcurl / 自写 Client）。

## 已实现 RPC

| 类别 | 方法 |
|------|------|
| 元数据 | Health, ListProtocols |
| 设备 | ListDevices, UpsertDevice, DeleteDevice, Connect, Disconnect, DisconnectAll |
| 变量 | ListVariables, UpsertVariable, DeleteVariable, ReadVariable, WriteVariable |
| 推送 | WatchDevices / WatchVariables（一期空流，立刻结束） |

## grpcurl 示例（需安装 grpcurl，反射未开时用 proto）

```bash
# 若未开 server reflection，可用后续加的反射或生成客户端测 Health
```

下一步：gRPC Reflection、Watch 真推送、WPF 可选改连 Host。
