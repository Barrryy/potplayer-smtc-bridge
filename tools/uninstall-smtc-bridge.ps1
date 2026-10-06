# 彻底卸载 PotPlayer SMTC Bridge。
#
# 用法（管理员 PowerShell）：
#   .\uninstall-smtc-bridge.ps1              # 只卸注册表（IFEO 启动注入）
#   .\uninstall-smtc-bridge.ps1 -Purge       # 连 PotPlayer 目录里的旧版残留和日志一起清
#
# 顺序很重要：先删 IFEO 键，再删程序目录。反过来会让 PotPlayer 起不来
# （系统会去拉起一个已经不存在的注入器）。

[CmdletBinding()]
param(
    [switch]$Purge,
    [string]$PotPlayerExe = 'C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe'
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请用管理员身份运行（写 HKLM 需要管理员权限）。'
}

# 1) 关掉我们自己的后台实例（新版安装方式不需要它，留着也无妨）
Get-Process PotPlayerSmtcBridge -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "关闭后台程序 PID $($_.Id)"
    $_ | Stop-Process -Force
}

# 2) 删 IFEO 键：只认自己写的标记，别人的设置不碰
$name = [IO.Path]::GetFileName($PotPlayerExe)
$keyPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\$name"
if (Test-Path $keyPath) {
    $item = Get-ItemProperty $keyPath -ErrorAction SilentlyContinue
    if ($null -ne $item.SmtcBridgeInjector) {
        Remove-Item $keyPath -Recurse -Force
        Write-Host "已删除 IFEO 键：$name" -ForegroundColor Green
    } else {
        Write-Warning "IFEO 里的 $name 不是本工具写的，已跳过（Debugger = $($item.Debugger)）"
    }
} else {
    Write-Host "IFEO 里没有 $name，跳过"
}

# 3) 清旧版（v0.6 导入表补丁）留下的东西
if ($Purge) {
    $dir = Split-Path -Parent $PotPlayerExe

    $dll = Join-Path $dir 'PotPlayerSmtcHook.dll'
    if (Test-Path $dll) { Remove-Item $dll -Force; Write-Host "已删除 $dll" }

    $bak = "$PotPlayerExe.smtb-backup"
    if (Test-Path $bak) {
        # 只有主程序已经是原版签名时才敢删备份，否则那是唯一的还原来源
        $sig = Get-AuthenticodeSignature $PotPlayerExe
        if ($sig.Status -eq 'Valid') {
            Remove-Item $bak -Force
            Write-Host "已删除 $bak（主程序签名正常，用不到它了）"
        } else {
            Write-Warning "主程序当前签名状态是 $($sig.Status)，保留备份 $bak 不要删！"
        }
    }

    $logs = Join-Path $env:TEMP 'potplayer-smtc-bridge'
    if (Test-Path $logs) { Remove-Item $logs -Recurse -Force; Write-Host "已删除日志目录 $logs" }
}

Write-Host ''
Write-Host '卸载完成。' -ForegroundColor Green
Write-Host '- 现在可以放心删除本程序目录了。'
Write-Host '- 已经开着的 PotPlayer 里，注入模块会留到那个进程关闭为止；重开一次就是干净的。'
