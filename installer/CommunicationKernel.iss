; =============================================================================
; 文件: installer/CommunicationKernel.iss
; 作用: WebMaster 的 Windows 安装包（Inno Setup 6）。
;
; 编译方式见同目录 build-installer.ps1，或：
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\CommunicationKernel.iss
;
; 输入：CommunicationKernel.UI.WebMaster\bin\Release\net8.0-windows\publish\win-x64
;      （必须先 dotnet publish，见 build-installer.ps1）
; 输出：installer\output\CommunicationKernel-Setup-<版本>.exe
; =============================================================================

#define AppName        "CommunicationKernel 通讯调试工具"
#define AppShortName   "CommunicationKernel"
#define AppPublisher   "CommunicationKernel"

; 版本号由 build-installer.ps1 用 /DAppVersion=x.y.z 传入（它从
; Directory.Build.props 读，保证与程序集版本一致）。必须用 ifndef 包住：
; 无条件 #define 会把命令行传进来的值覆盖掉，打出来的包永远是这里写死的版本。
#ifndef AppVersion
  #define AppVersion   "1.0.0"
#endif
#define AppExeName     "CommunicationKernel.UI.WebMaster.exe"
#define SrcDir         "..\CommunicationKernel.UI.WebMaster\bin\Release\net8.0-windows\publish\win-x64"

; 默认端口。与 appsettings.json 的 Web:ListenPort / Hosting:GrpcPort 保持一致；
; 操作员改过端口后防火墙规则需要自行调整，安装包不跟踪运行期改动。
#define WebPort        "64000"
#define GrpcPort       "5000"

[Setup]
AppId={{8F3A6C21-4B7D-4E29-9C15-2A6E8D7F4B90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; ---------------------------------------------------------------------------
; 安装目录：刻意不用 Program Files。
;
; 本程序的配置（设备表、变量表、访问口令、穿透设置）存放在
; 「exe 同目录\config\」——见 Services/WebPaths.cs，设计上就是「跟 exe 走，
; 换机器拷贝整个目录即可」。装进 Program Files 会有两个后果：
;   · 普通用户对该目录只有读权限，保存设备/变量会直接失败；
;   · 内网穿透要求把 frpc.exe 放到 exe 旁边，每次都得管理员权限。
; 因此默认装到 C:\CommunicationKernel。
;
; 若你确实要装进 Program Files，下面的 GrantConfigWritePermission 会给
; config\ 目录单独放开写权限，但 frpc.exe 仍需管理员才能放进去。
; ---------------------------------------------------------------------------
DefaultDirName=C:\{#AppShortName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; 需要管理员：要写 C:\ 根下的目录、加防火墙规则、改 ACL
PrivilegesRequired=admin

; 自包含单文件 exe 约 180 MB，压缩耗时较长但能把安装包压到 ~70 MB
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

OutputDir=output
OutputBaseFilename={#AppShortName}-Setup-{#AppVersion}
SetupIconFile=..\CommunicationKernel.UI.WebMaster\wwwroot\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

; 仅 64 位。自包含产物是 win-x64，装到 32 位系统上跑不起来
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

WizardStyle=modern
ShowLanguageDialog=no

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon";  Description: "创建桌面快捷方式"; GroupDescription: "快捷方式:"
Name: "firewall";     Description: "放行防火墙端口 {#WebPort}（局域网/手机访问 Web 界面需要）"; GroupDescription: "网络:"
Name: "autostart";    Description: "开机自动启动"; GroupDescription: "启动:"; Flags: unchecked

[Files]
; ---------------------------------------------------------------------------
; 主程序与静态资源。
;
; 排除项说明：
;   *.pdb        调试符号，客户机上没有用途，白占体积
;   config\*     运行期配置。绝不能随包分发——见下方 [Dirs] 与升级说明
;   frpc.exe     内网穿透工具，刻意不分发（会被杀软误报，连累整个包）
;                需要的用户自行下载放入，详见「部署-Linux与树莓派.md」
; ---------------------------------------------------------------------------
; 整目录打包，只排除不该发的。
;
; 刻意不逐类列举（*.exe / *.dll / *.json …）：自包含产物里还有一批原生
; 运行时 DLL，将来换 SDK 版本可能增减。漏掉一个的表现是装完直接闪退，
; 而安装包本身编译得好好的，事后极难定位。整目录 + 排除清单不会漏。
Source: "{#SrcDir}\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "*.pdb,*.xml,dotnet-tools.json,\config\*,frpc.exe,frpc.toml"

[Dirs]
; config 目录由安装包建好并放开写权限。
; 不建的话，程序首次启动时 WebPaths.Root 会去 CreateDirectory，
; 在无写权限的目录下抛异常，而那是个属性 getter，异常点离病因很远。
Name: "{app}\config"; Permissions: users-modify

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\打开 Web 界面"; Filename: "http://localhost:{#WebPort}/"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; 开机自启。用 HKLM\...\Run：本程序是现场工具，通常希望任何账户登录都起来。
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppShortName}"; ValueData: """{app}\{#AppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
; 防火墙：只放行 Web 端口。
; gRPC（{#GrpcPort}）刻意不放行——它没有任何认证，能连上就能写 PLC，
; 只应在本机回环使用。WPF 上位机跨机连接请走 VPN / 内网，不要图省事开这个口。
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""{#AppShortName} Web ({#WebPort})"" dir=in action=allow protocol=TCP localport={#WebPort}"; \
    Flags: runhidden; Tasks: firewall; StatusMsg: "正在添加防火墙规则…"

Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前先删防火墙规则。删失败不阻断卸载（规则可能已被手工删掉）
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall delete rule name=""{#AppShortName} Web ({#WebPort})"""; \
    Flags: runhidden; RunOnceId: "DelFirewallRule"

[Code]

{ ===========================================================================
  升级/卸载前必须先停掉正在运行的实例。

  两个原因：
    · 主 exe 被占用时文件替换失败，Inno 会要求重启计算机才能完成升级；
    · 本程序是托盘常驻的，关掉浏览器不等于退出，用户往往以为它没在跑。

  同时要收掉它托管的 frpc：WebMaster 被强杀时 StopAsync 不执行，
  frpc 会活下来继续占着服务器上的远端口。程序自身的 KillOrphans 只在
  「下次启动」时清理，卸载后就再也没有下次了。
  =========================================================================== }
procedure StopRunningInstance();
var
  ResultCode: Integer;
begin
  { /F 强制、/T 连同子进程。找不到进程时返回非 0，忽略即可 }
  Exec(ExpandConstant('{sys}\taskkill.exe'),
       '/F /T /IM CommunicationKernel.UI.WebMaster.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { 只杀装在本目录下的那一个 frpc：机器上可能还跑着用户自己的 frpc 干别的事 }
  Exec(ExpandConstant('{cmd}'),
       ExpandConstant('/C taskkill /F /IM frpc.exe /FI "IMAGENAME eq frpc.exe" 2>nul'),
       ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { 给文件句柄一点释放时间，否则紧接着的复制仍可能撞上占用 }
  Sleep(1500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningInstance();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningInstance();
  Result := True;
end;

{ ===========================================================================
  卸载时询问是否保留配置。

  config\ 里是设备表、变量表、访问口令、穿透设置——现场可能是花了很久
  一条条录进去的。默认保留：重装后原样还在；确实要清干净再选删除。

  注意 frpc.exe 一律不动：它不是我们装的，也不是我们的东西。
  =========================================================================== }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    ConfigDir := ExpandConstant('{app}\config');
    if DirExists(ConfigDir) then
    begin
      if MsgBox('是否一并删除配置数据？' + #13#10 + #13#10 +
                '配置包括：设备表、变量表、访问口令、内网穿透设置。' + #13#10 +
                '（位置：' + ConfigDir + '）' + #13#10 + #13#10 +
                '选「否」将保留配置，重新安装后可继续使用。',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ConfigDir, True, True, True);
    end;
  end;
end;
