# =============================================================================
# 文件: installer/build-installer.ps1
# 作用: 一条命令完成「发布 → 打包」，产出可分发的安装程序。
#
# 用法（在仓库根目录）:
#     powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#
# 可选参数:
#     -SkipPublish      跳过 dotnet publish，直接用现有发布产物打包
#     -Version 1.2.3    覆盖安装包版本号（默认读 Directory.Build.props）
#
# 前置条件: 已安装 Inno Setup 6（https://jrsoftware.org/isdl.php）
# =============================================================================

[CmdletBinding()]
param(
    [switch] $SkipPublish,
    [string] $Version,

    # 是否自包含（把 .NET 运行时打进产物）。
    #
    # 默认 true，且刻意在命令行显式传给 dotnet publish，不依赖 .pubxml 里的值——
    # 那个文件由 Visual Studio 的发布界面维护，勾一下就会被改写，
    # 而改成 false 之后打出的包在没装 .NET 8 的机器上直接起不来，
    # 且安装过程一切正常，只有双击图标时"什么都不发生"（本程序是 WinExe，无控制台）。
    # 现场工控机常年不联网、也未必有管理员，让安装包自带运行时最省事。
    [bool] $SelfContained = $true
)

$ErrorActionPreference = 'Stop'

# 脚本在 installer\ 下，仓库根目录是它的上一级
$Root      = Split-Path -Parent $PSScriptRoot
$Project   = Join-Path $Root 'CommunicationKernel.UI.WebMaster'
$PublishIn = Join-Path $Project 'bin\Release\net8.0-windows\publish\win-x64'
$IssFile   = Join-Path $PSScriptRoot 'CommunicationKernel.iss'
$OutDir    = Join-Path $PSScriptRoot 'output'

# --- 版本号 -----------------------------------------------------------------
# 默认与解决方案统一：只在 Directory.Build.props 里维护一处，
# 避免 csproj 版本和安装包版本各说各话。
if (-not $Version) {
    $props = Get-Content (Join-Path $Root 'Directory.Build.props') -Raw
    if ($props -match '<Version>([^<]+)</Version>') {
        $Version = $Matches[1].Trim()
    } else {
        throw "无法从 Directory.Build.props 读取 <Version>，请用 -Version 显式指定"
    }
}
Write-Host "版本号: $Version" -ForegroundColor Cyan

# --- 定位 Inno Setup --------------------------------------------------------
# 含用户目录：winget 默认把 Inno Setup 装到 %LOCALAPPDATA%\Programs，
# 只找 Program Files 会误判成"没装"。
$IsccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) {
    throw @"
未找到 Inno Setup 6。

请先安装（约 5 MB，免费，需 6.3 或更新版本）:
  https://jrsoftware.org/isdl.php
或用 winget:
  winget install JRSoftware.InnoSetup

两点注意：
  · 安装时勾选简体中文语言包，否则 [Languages] 段会编译失败；
  · 需要 6.3+：脚本用了 ArchitecturesAllowed=x64compatible，
    6.0-6.2 不认这个值。
"@
}
Write-Host "Inno Setup: $Iscc" -ForegroundColor Cyan

# --- 发布 -------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host "`n[1/2] 发布自包含产物（首次约 1 分钟）…" -ForegroundColor Yellow

    # 先清掉上一次的产物：publish 不会删除已不再需要的文件，
    # 改过名或删掉的文件会一直留在目录里，被安装包原样打进去。
    if (Test-Path $PublishIn) { Remove-Item $PublishIn -Recurse -Force }

    $scArg = if ($SelfContained) { 'true' } else { 'false' }
    Write-Host "  自包含: $scArg" -ForegroundColor DarkGray

    & dotnet publish $Project -c Release -p:PublishProfile=FolderProfile -p:SelfContained=$scArg
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }
} else {
    Write-Host "`n[1/2] 已跳过发布（-SkipPublish）" -ForegroundColor DarkGray
}

# 打包前确认关键文件都在。缺了照样能编译出安装包，
# 但装到客户机上才会发现界面没样式或一个协议都加载不出来。
$MustExist = @(
    (Join-Path $PublishIn 'CommunicationKernel.UI.WebMaster.exe'),
    (Join-Path $PublishIn 'appsettings.json'),
    (Join-Path $PublishIn 'wwwroot'),
    (Join-Path $PublishIn 'plugins')
)
foreach ($p in $MustExist) {
    if (-not (Test-Path $p)) { throw "发布产物缺少: $p" }
}

$PluginCount = @(Get-ChildItem (Join-Path $PublishIn 'plugins') -Filter '*.dll').Count
if ($PluginCount -lt 5) {
    throw "plugins 目录只有 $PluginCount 个 DLL，应为 5 个（3 协议 + 2 传输）。插件缺失时程序不报错，只是一个协议都用不了。"
}

# 自包含产物的主 exe 必然上百 MB（.NET 运行时都在里面）。
# 只有几 MB 说明 SelfContained 实际没生效——多半是 .pubxml 里的值压过了命令行，
# 或改了发布配置。这一步必须拦住：漏过去的话安装包能正常打出来、能正常装完，
# 只有在没装 .NET 的目标机器上双击时"什么都不发生"，排查代价极高。
$ExePath = Join-Path $PublishIn 'CommunicationKernel.UI.WebMaster.exe'
$ExeMB   = [math]::Round((Get-Item $ExePath).Length / 1MB, 1)

if ($SelfContained -and $ExeMB -lt 50) {
    throw @"
自包含发布未生效：主 exe 只有 $ExeMB MB（自包含应为 150 MB 以上）。

请检查 $Project\Properties\PublishProfiles\FolderProfile.pubxml
里的 <SelfContained>。Visual Studio 的发布界面会改写该文件。
"@
}
if (-not $SelfContained -and $ExeMB -gt 50) {
    throw "指定了 -SelfContained `$false，但主 exe 有 $ExeMB MB，看起来仍是自包含产物。"
}

$Mode = if ($SelfContained) { '自包含（目标机无需安装 .NET）' }
        else { '框架依赖（目标机需 .NET 8 桌面运行时 + ASP.NET Core 运行时）' }
Write-Host "发布产物校验通过：插件 $PluginCount 个，主 exe $ExeMB MB，$Mode" -ForegroundColor Green

# --- 打包 -------------------------------------------------------------------
Write-Host "`n[2/2] 生成安装包（LZMA2 压缩 180 MB，约 1-2 分钟）…" -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

& $Iscc "/DAppVersion=$Version" $IssFile
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败（退出码 $LASTEXITCODE）" }

$Setup = Join-Path $OutDir "CommunicationKernel-Setup-$Version.exe"
if (-not (Test-Path $Setup)) { throw "未生成预期的安装包: $Setup" }

$SizeMB = [math]::Round((Get-Item $Setup).Length / 1MB, 1)
Write-Host "`n完成: $Setup ($SizeMB MB)" -ForegroundColor Green
