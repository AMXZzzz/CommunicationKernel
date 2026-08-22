# 部署到 Linux / 树莓派

本文覆盖两种部署形态。先确定你属于哪一种，再往下读对应章节——两者的产物、
依赖和排障方式都不同。

| | 形态 A：SDK 嵌入 | 形态 B：独立宿主 |
|---|---|---|
| 上位机在哪 | 与 PLC 通讯的同一台机器 | 另一台机器 |
| 进程数 | 1（上位机自己直连 PLC） | 2（上位机 + Host.App） |
| 通讯方式 | 进程内直接调用 | gRPC over HTTP/2 |
| 插件目录 | 不需要 | 需要 |
| 典型场景 | 树莓派上跑控制程序直连 PLC | 现场网关 + 远端上位机 |

---

## 形态 A：把内核当 SDK 嵌进上位机

树莓派直连 PLC 时不需要 Host.App，也不需要 gRPC——多一个进程和一趟本机
网络往返，只会增加故障面。直接引用 `CommunicationKernel.Engine.Runtime`，
用 `StaticRouteAssemblyService` 在编译期交出工厂即可。

```xml
<ItemGroup>
  <ProjectReference Include="../CommunicationKernel.Engine.Runtime/CommunicationKernel.Engine.Runtime.csproj" />
  <ProjectReference Include="../CommunicationKernel.Plugins.Protocol.Modbus/CommunicationKernel.Plugins.Protocol.Modbus.csproj" />
  <ProjectReference Include="../CommunicationKernel.Plugins.Transport.SerialPort/CommunicationKernel.Plugins.Transport.SerialPort.csproj" />
</ItemGroup>
```

```csharp
// 工厂由调用方直接提供，全程不触碰文件系统
var assembly = new StaticRouteAssemblyService(
    transportFactories: new ITransportFactory[] { new SerialPortTransportFactory() },
    protocolFactories:  new IProtocolDriverFactory[] { new ModbusRtuProtocolDriverFactory() });

await using var engine = new EngineRuntime(
    assembly,
    new RouterOrchestrator(new ConnectionRouter(), new ReadCoordinator()));

await engine.RegisterRouteAsync(new RegisterRouteCommand {
    RouteId       = "plc-1",
    ProtocolId    = "modbus-rtu",
    TransportKind = "Serial",
    SerialPort    = "/dev/ttyUSB0",
    BaudRate      = 9600,
    Station       = "1"
}, ct);

var read = await engine.ReadByRouteIdAsync("plc-1", "40001", 2, ct);
```

这条路径没有插件目录，因此也就没有插件目录带来的一整类故障：
共享契约泄漏、工作目录漂移、目录权限。协议集合在编译期就定死了，
换协议要重新编译——这是刻意的取舍。

需要不重新编译就扩展协议时，改用 `PluginRouteAssemblyService`（形态 B 用的那个），
两者实现同一个 `IRouteAssemblyService`，可直接替换。

### 发布

```bash
dotnet publish YourApp.csproj -c Release -r linux-arm64 --self-contained true -o ./out
```

树莓派 64 位系统用 `linux-arm64`；32 位（含 Raspberry Pi OS 的 32 位版）用 `linux-arm`。
搞错 RID 时程序会直接起不来，报的是 exec 格式错误，不指向 RID。

---

## 形态 B：独立宿主 Host.App

```bash
dotnet publish CommunicationKernel.Host.App/CommunicationKernel.Host.App.csproj \
  -c Release -r linux-arm64 --self-contained true -o ./publish/linux-arm64
```

`--self-contained true` 让产物自带运行时，目标机不必预装 .NET。
代价是体积约 100 MB。

### 产物结构

```
publish/linux-arm64/
├── CommunicationKernel.Host.App          # 可执行 apphost
├── appsettings.json
├── CommunicationKernel.Core.Abstractions.dll        ┐
├── CommunicationKernel.Communication.Protocol.dll   │ 四个共享契约
├── CommunicationKernel.Communication.Transport.dll  │ 必须在这一层
├── CommunicationKernel.Plugin.Loader.dll           ┘
└── plugins/
    ├── CommunicationKernel.Plugins.Protocol.Modbus.dll        # TCP / RTU / ASCII 三个变体同处一个程序集
    ├── CommunicationKernel.Plugins.Transport.*.dll
    └── runtimes/linux-arm64/native/libSystem.IO.Ports.Native.so
```

**四个共享契约绝不能出现在 `plugins/` 下。** 插件用独立 `AssemblyLoadContext`
加载，`AssemblyDependencyResolver` 优先在插件目录内解析依赖；契约一旦在那里
出现，插件就会加载到自己那一份类型，它的 `IProtocolDriverFactory` 与宿主的
将是两个不同类型，`IsAssignableFrom` 判定失败——**所有插件静默注册不上，
不抛任何异常**，表现为「协议列表是空的」。CI 有专门的作业守这条。

---

## 串口权限

Linux 上串口设备属于 `dialout` 组，普通用户默认无权打开。
缺权限时的报错是 `Access to the port '/dev/ttyUSB0' is denied`，
不会提示是组的问题。

```bash
sudo usermod -aG dialout $USER
```

**改完必须重新登录**（或 `newgrp dialout`）才生效——组成员身份在会话建立时
就确定了，`usermod` 不影响已有会话。这是最常见的「照做了还是不行」。

确认设备：

```bash
ls -l /dev/ttyUSB* /dev/ttyAMA* /dev/serial0
dmesg | grep -i tty        # 插拔 USB 串口线后看内核认到了什么
```

### 树莓派板载串口的额外两步

树莓派的 GPIO 串口默认被系统占用，直接用会读到登录提示符而不是 PLC 响应。

1. **关掉串口控制台**：`sudo raspi-config` → Interface Options → Serial Port
   → 「login shell over serial」选 **No**，「serial port hardware」选 **Yes**。
2. **认准设备名**：`/dev/serial0` 是指向当前主串口的符号链接，比直接写
   `/dev/ttyAMA0` 或 `/dev/ttyS0` 稳妥——后两者指向哪个硬件会随
   树莓派型号和蓝牙是否启用而变。

USB 转串口适配器（CH340/CP2102/FTDI 等）不受以上影响，直接是 `/dev/ttyUSB0`。

### 设备名稳定性

多个 USB 串口同时插着时，`/dev/ttyUSB0` 和 `ttyUSB1` 的编号取决于枚举顺序，
重启后可能对调——接错 PLC 的后果比读不到数据严重得多。用 by-id 路径固定：

```bash
ls -l /dev/serial/by-id/
# usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0 -> ../../ttyUSB0
```

配置里直接填 `/dev/serial/by-id/usb-FTDI_...-port0`。

---

## 监听地址与暴露面

默认绑定 `http://localhost:5000`，**只有本机能连**。形态 B 里上位机在另一台
机器上，必须改：

```json
"Kestrel": {
  "Endpoints": {
    "Grpc": { "Url": "http://0.0.0.0:5000", "Protocols": "Http2" }
  }
}
```

**`Protocols` 必须保持 `Http2`。** gRPC 只跑在 HTTP/2 上，而明文端点没有
TLS ALPN 可供协议协商——Kestrel 在明文上同时配 `Http1AndHttp2` 时无法区分
两种协议，会退回纯 HTTP/1.1，此后所有 gRPC 调用被服务端以
`HTTP_1_1_REQUIRED` 拒绝。启动日志里出现
`HTTP/2 is not enabled for ...` 就是踩到了这个坑。

代价是根路由的引导页在浏览器里打不开（浏览器不做明文 h2c）。
用 `systemctl status` 和 `journalctl` 看存活即可。

也可以不改文件，用环境变量覆盖：

```bash
Kestrel__Endpoints__Grpc__Url=http://0.0.0.0:5000 ./CommunicationKernel.Host.App
```

> **gRPC 端点没有任何认证与授权。** 能建立连接就能注册路由、读写 PLC 寄存器。
> 绑到 `0.0.0.0` 等于把现场设备的读写权限开放给整个网段。
> 宿主会在启动日志里就此发出警告，但拦不住——隔离由部署方负责：
> 防火墙白名单、独立 VLAN，或前置一层带认证的反向代理。
> 产线网络上尤其要当回事。

---

## systemd 服务

`/etc/systemd/system/communication-kernel.service`：

```ini
[Unit]
Description=CommunicationKernel Host.App
# network-online 而非 network：仅 network 只保证网络栈起来了，
# 不保证拿到地址，绑定固定 IP 时会启动失败
After=network-online.target
Wants=network-online.target

[Service]
# simple 而非 notify：宿主未引入 Microsoft.Extensions.Hosting.Systemd，
# 不会发送就绪通知，用 notify 会让 systemd 一直等到超时判定启动失败
Type=simple
WorkingDirectory=/opt/communication-kernel
ExecStart=/opt/communication-kernel/CommunicationKernel.Host.App
Restart=always
RestartSec=5

# 必须属于 dialout，否则打不开串口
User=pi
SupplementaryGroups=dialout

Environment=DOTNET_ENVIRONMENT=Production
# 日志直接进 journald，不用自己切割文件
StandardOutput=journal
StandardError=journal
SyslogIdentifier=comm-kernel

[Install]
WantedBy=multi-user.target
```

想升级成 `Type=notify`（让 systemd 准确知道服务何时真正就绪，
依赖它的其他单元才不会启动过早），需要给宿主加上：

```xml
<PackageReference Include="Microsoft.Extensions.Hosting.Systemd" Version="8.0.*" />
```

并在 `AppMain.cs` 里 `builder.Host.UseSystemd();`。两者缺一，`notify` 都会
让 systemd 等到超时。

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now communication-kernel
journalctl -u communication-kernel -f
```

---

## 排障速查

启动日志里必然有这一行，先看它：

```
info: Host.App.Startup[0]
      已加载 6 个协议：modbus-ascii, modbus-rtu, modbus-tcp, panasonic-mewtocol, siemens-s7-1200, siemens-s7-200smart
```

插件在启动时即完成加载（而非等到第一次调用），所以这行缺失或数量不对，
在有人操作之前就能发现。

| 现象 | 原因 |
|---|---|
| 协议列表是空的，无任何报错 | 共享契约泄漏进了 `plugins/`，插件静默注册不上 |
| gRPC 调用报 `HTTP_1_1_REQUIRED` | 端点 `Protocols` 不是 `Http2` |
| `Access to the port is denied` | 用户不在 `dialout` 组，或改完组没重新登录 |
| 串口打开就抛 `PlatformNotSupportedException` | `libSystem.IO.Ports.Native.so` 没随行 |
| 串口读回登录提示符 | 树莓派串口控制台没关 |
| 上位机连不上宿主 | 宿主还绑在 `localhost` |
| 进程起不来，exec 格式错误 | RID 搞错了（32 位系统用了 `linux-arm64`） |
| 重启后接到了错误的 PLC | 用了 `/dev/ttyUSB0` 而非 by-id 路径 |
