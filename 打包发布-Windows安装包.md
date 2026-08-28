# 打包发布 — Windows 安装包

面向现场交付：把 WebMaster 打成一个带向导的 `Setup.exe`，双击下一步就能装好。

Linux / 树莓派部署见 [部署-Linux与树莓派.md](部署-Linux与树莓派.md)，那边走的是发布目录 + systemd，不用安装包。

---

## 目录

- [一句话上手](#一句话上手)
- [准备：装一次 Inno Setup](#准备装一次-inno-setup)
- [打包](#打包)
- [安装包做了什么](#安装包做了什么)
  - [安装目录为什么不是 Program Files](#安装目录为什么不是-program-files)
  - [三个可选项](#三个可选项)
  - [升级：配置不会被冲掉](#升级配置不会被冲掉)
  - [卸载：问你要不要留配置](#卸载问你要不要留配置)
- [自包含还是框架依赖](#自包含还是框架依赖)
- [改版本号](#改版本号)
- [文件清单](#文件清单)
- [排障](#排障)

---

## 一句话上手

仓库根目录执行：

```bash
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

产出 `installer\output\CommunicationKernel-Setup-1.0.0.exe`，约 55 MB，可直接发给现场。

---

## 准备：装一次 Inno Setup

用的是 [Inno Setup 6](https://jrsoftware.org/isdl.php)（免费，约 5 MB）：

```bash
winget install JRSoftware.InnoSetup
```

两点注意：

- **必须 6.3 或更新**。脚本用了 `ArchitecturesAllowed=x64compatible`，6.0–6.2 不认这个值。
- 安装时**勾选简体中文语言包**，否则 `[Languages]` 段编译失败。

winget 默认装到 `%LOCALAPPDATA%\Programs\Inno Setup 6\`，构建脚本会自动找这个位置，不必配环境变量。

> 为什么选 Inno Setup 而不是 MSI/WiX：产出单个 exe、自带中文向导、脚本改起来快，
> 现场拷 U 盘就能装。如果你需要走域策略批量推送，那才值得换成 MSI（WiX），
> 代价是脚本复杂度上一个台阶。

---

## 打包

```bash
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

脚本干三件事：

1. **发布**：`dotnet publish` 自包含 win-x64 单文件产物（先清空上次的输出目录）
2. **校验**：主 exe 体积、插件数量、`wwwroot` 与 `appsettings.json` 是否齐全
3. **打包**：调 Inno Setup 编译成 `Setup.exe`

参数：

| 参数 | 用途 |
|---|---|
| `-SkipPublish` | 跳过发布，直接用现有产物打包。改 `.iss` 反复调试时用，省 1 分钟 |
| `-Version 1.2.3` | 覆盖版本号。默认读 `Directory.Build.props` 的 `<Version>` |
| `-SelfContained $false` | 打框架依赖版，见[下文](#自包含还是框架依赖) |

**那几步校验不是走过场。** 插件缺失、`wwwroot` 没跟上，安装包照样能编译、也能装完，
只有装到客户机上才发现界面没样式、或者一个协议都用不了。让它在你自己机器上就失败。

---

## 安装包做了什么

### 安装目录为什么不是 Program Files

默认装到 **`C:\CommunicationKernel`**，这是有意的。

本程序的配置——设备表、变量表、访问口令、穿透设置——存在 **exe 同目录的 `config\`** 下
（见 `Services/WebPaths.cs`，设计上就是「跟 exe 走，换机器拷贝整个目录即可」）。
装进 `Program Files` 会有两个后果：

- 普通用户对该目录只有读权限，**保存设备/变量会直接失败**；
- 内网穿透要求把 `frpc.exe` 放到 exe 旁边，每次都得管理员权限。

你仍然可以在向导里改成任意路径。安装包会给 `config\` 单独放开写权限
（`[Dirs]` 的 `users-modify`），所以即使装进 `Program Files`，配置也存得下去——
但 `frpc.exe` 仍需管理员才放得进去。

### 三个可选项

向导里三个复选框，都可以不选：

| 选项 | 默认 | 说明 |
|---|---|---|
| 创建桌面快捷方式 | 勾选 | — |
| 放行防火墙端口 64000 | 勾选 | 手机/局域网访问 Web 界面需要。只放行 Web 口 |
| 开机自动启动 | **不勾** | 写 `HKLM\...\Run`，任何账户登录都会起 |

**gRPC 的 5000 端口刻意不放行。** 它没有任何认证，能连上就能写 PLC，只应在本机回环使用。
WPF 上位机跨机连接请走 VPN 或内网直连，不要图省事开这个口。

开始菜单里除了主程序，还有一个「打开 Web 界面」的快捷方式，直接指向 `http://localhost:64000/`。

### 升级：配置不会被冲掉

直接用新版 `Setup.exe` 覆盖安装即可，不用先卸载。安装包会：

1. **先停掉正在运行的实例**（`taskkill /F /T`）。本程序是托盘常驻的，
   关掉浏览器不等于退出——不停掉的话文件被占用，Inno 会要求重启计算机才能完成升级。
2. 顺手收掉它托管的 `frpc`。WebMaster 被强杀时 `StopAsync` 不执行，
   frpc 会活下来继续占着服务器上的远端口。
3. 覆盖程序文件，**不碰 `config\`**（安装包根本不包含配置文件，只建空目录）。
4. **不碰 `frpc.exe`**。那是用户自己放的，不是我们装的。

> 已实测：升级前在 `config\` 里放一份设备配置、旁边放一个 `frpc.exe`，
> 升级后两者原样都在。

### 卸载：问你要不要留配置

卸载时弹一个确认框，**默认按钮是「否」= 保留配置**。

选「否」：程序文件全部删除，`config\` 原样保留，重装后接着用。
选「是」：连 `config\` 一起删干净。

`frpc.exe` 任何情况下都不删——它不是我们装的。

---

## 自包含还是框架依赖

默认打**自包含**包：.NET 运行时打进产物，目标机器什么都不用装。

| | 自包含（默认） | 框架依赖 |
|---|---|---|
| 安装包体积 | ~55 MB | ~3 MB |
| 目标机器要求 | **无** | 需装 .NET 8 **桌面运行时** + **ASP.NET Core 运行时**（两个都要） |
| 适用 | 现场工控机（常年不联网、未必有管理员） | 内网统一装过运行时的环境 |

现场机器多半不联网，让安装包自带运行时最省事，55 MB 换掉一整类「装完打不开」的问题很划算。

**注意 `.pubxml` 会被 Visual Studio 改写。** 在 VS 的发布界面点一下，
`<SelfContained>` 就可能被改掉。所以构建脚本**显式**用命令行参数指定，不依赖那个文件里的值：

```powershell
dotnet publish ... -p:SelfContained=true
```

并且校验主 exe 体积：自包含产物必然 150 MB 以上，只有几 MB 就直接报错。
**这一步救的是最难查的那种故障**——自包含没生效时，安装包能正常打出来、能正常装完，
只有在没装 .NET 的目标机器上双击时「什么都不发生」（本程序是 WinExe，没有控制台，
不会弹任何提示）。

真要打框架依赖版：

```bash
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -SelfContained $false
```

此时安装包**不会**自动装运行时。目标机器需要先自行安装：

```bash
winget install Microsoft.DotNet.DesktopRuntime.8
```

```bash
winget install Microsoft.DotNet.AspNetCore.8
```

> 想要「检测到没装运行时就自动下载安装」的那种引导程序，Inno Setup 可以做
> （下载 + 静默安装 + 检查返回码），但那要处理断网、下载失败、需要重启等一堆分支。
> 既然自包含只多 50 MB 就完全绕开这些，默认就不做了。

---

## 改版本号

只在一处维护：`Directory.Build.props` 的 `<Version>`。

```xml
<Version>1.0.0</Version>
```

构建脚本从这里读，用 `/DAppVersion=` 传给 Inno Setup，程序集版本和安装包版本因此不会各说各话。
临时打个测试包可以用 `-Version 1.0.1-test` 覆盖，不必改文件。

`.iss` 里的 `AppId` 是这个产品的唯一标识，**永远不要改**——
改了之后新版本会被 Windows 当成另一个软件，和旧版并存，出现两个卸载项。

---

## 文件清单

```
installer/
├── CommunicationKernel.iss     Inno Setup 脚本（安装逻辑都在这）
├── build-installer.ps1         发布 + 校验 + 打包
└── output/                     产物，未纳入版本控制
```

两个脚本都是 **UTF-8 带 BOM**。别存成不带 BOM 的：
Windows PowerShell 5.1 会把无 BOM 的 UTF-8 当 ANSI 读，
中文注释被打散成乱码字符，报出一串跟真实原因毫无关系的语法错。

安装包**不含**以下内容，都是刻意的：

| 不含 | 原因 |
|---|---|
| `*.pdb` | 调试符号，客户机上没用途 |
| `config\*` | 运行期配置，随包分发会冲掉现场数据 |
| `frpc.exe` | 穿透工具会被杀软误报，连累整个安装包。用户自行下载放入 |
| `CommunicationKernel.Hosting.App.exe` | 自包含发布时它是个孤儿——指向的 dll 已打进单文件，且它本身是框架依赖的，双击只会弹「必须安装 .NET」 |

---

## 排障

| 现象 | 原因 |
|---|---|
| `未找到 Inno Setup 6` | 没装，或装在别处。`winget install JRSoftware.InnoSetup` |
| `Unknown value "x64compatible"` | Inno Setup 版本低于 6.3，升级即可 |
| `Unknown filename or [Languages] error` | 装 Inno Setup 时没勾简体中文语言包 |
| 打出的包只有 2–3 MB | 自包含没生效。脚本已有体积校验会拦下，若手工调 ISCC 则不会 |
| 装完双击没反应 | 框架依赖包装到了没有 .NET 8 的机器上。本程序是 WinExe，无控制台不报错 |
| 界面能开但没样式 | `wwwroot` 没打进去。脚本的校验会拦，手工打包才可能出现 |
| 协议列表是空的 | `plugins` 没打进去，或共享契约泄漏进了 plugins 目录 |
| 升级时提示需要重启计算机 | 旧实例没停掉。正常路径下 `PrepareToInstall` 会先 taskkill |
