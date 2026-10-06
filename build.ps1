# 用 MinGW-w64 (x86_64) 构建全部组件。
#
#   .\build.ps1
#
# 产物输出到 .\build\，DLL 与 EXE 放在同一目录，直接运行 SmtcLoader.exe 即可。

param([switch]$Tests)

$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot
$outDir = Join-Path $root 'build'
$srcDir = Join-Path $root 'src'

$gxx = (Get-Command 'g++' -ErrorAction SilentlyContinue).Source
if (-not $gxx) {
    throw '找不到 g++，请先安装 MinGW-w64 并加入 PATH。'
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$commonFlags = @(
    '-std=c++17', '-O2', '-Wall', '-Wextra',
    '-Wno-cast-function-type',
    '-DWIN32_LEAN_AND_MEAN', '-DUNICODE', '-D_UNICODE',
    # 必须全静态：DLL 要能在一台没装 MinGW 的机器上加载。
    # 之前只加了 -static-libgcc/-static-libstdc++，仍会依赖 libwinpthread-1.dll，
    # 一旦 PATH 里没有 MinGW（或者被静态导入表引用）就会整个加载失败。
    '-static', '-static-libgcc', '-static-libstdc++'
)

Write-Host '[1/2] 构建 PotPlayerSmtcHook.dll ...' -ForegroundColor Cyan
$hookSources = @(
    "$srcDir\PotPlayerSmtcHook\dllmain.cpp",
    "$srcDir\PotPlayerSmtcHook\logger.cpp",
    "$srcDir\PotPlayerSmtcHook\pe_utils.cpp",
    "$srcDir\PotPlayerSmtcHook\probes.cpp",
    "$srcDir\PotPlayerSmtcHook\smtc_capture.cpp",
    "$srcDir\PotPlayerSmtcHook\smtc_writer.cpp"
)
& $gxx @commonFlags -shared -o "$outDir\PotPlayerSmtcHook.dll" @hookSources -lole32 -luuid -luser32 -lshell32
if ($LASTEXITCODE -ne 0) { throw 'DLL 构建失败' }

Write-Host '[2/2] 构建 SmtcLoader.exe ...' -ForegroundColor Cyan
$loaderSources = @(
    "$srcDir\SmtcLoader\main.cpp",
    "$srcDir\SmtcLoader\console.cpp"
)
& $gxx @commonFlags -municode -o "$outDir\SmtcLoader.exe" @loaderSources -lole32 -luser32
if ($LASTEXITCODE -ne 0) { throw 'EXE 构建失败' }

Write-Host ''
Write-Host '构建完成：' -ForegroundColor Green
Get-ChildItem $outDir | Select-Object Name, Length | Format-Table -AutoSize
Write-Host '用法: .\build\SmtcLoader.exe --help' -ForegroundColor Green

# 前端界面（.NET 10 WinForms）也发布到 build/，与 DLL、注入器同目录
Write-Host ''
Write-Host '[3/4] 构建前端界面 ...' -ForegroundColor Cyan
$dotnet = (Get-Command 'dotnet' -ErrorAction SilentlyContinue).Source
if ($dotnet) {
    & $dotnet publish "$root\src\SmtcBridge.App\SmtcBridge.App.csproj" -c Release -o $outDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw '前端界面构建失败' }
    Write-Host '前端界面已发布：build\PotPlayerSmtcBridge.exe' -ForegroundColor Green
} else {
    Write-Host '跳过：未找到 dotnet SDK，无法构建前端界面' -ForegroundColor Yellow
}

# 默认开启文件挂接：需要「当前播放文件的完整路径」这条线索来取元数据。
# 删掉 build\PotPlayerSmtcHook.hookfiles 即可关闭。
New-Item -ItemType File -Force -Path "$outDir\PotPlayerSmtcHook.hookfiles" | Out-Null

Write-Host ''
Write-Host '[4/5] 构建 IFEO 启动注入器 ...' -ForegroundColor Cyan
# 由 IFEO(Debugger) 在每次启动 PotPlayer 时自动拉起：用 DEBUG_ONLY_THIS_PROCESS 创建目标
# （绕开 IFEO 二次劫持），脱离调试后注入 DLL，然后立刻退出。不常驻、不留窗口。
# -mwindows：GUI 子系统，双击/被拉起时都不会闪控制台窗口。
& $gxx @commonFlags -mwindows -o "$outDir\PotPlayerSmtcInjector.exe" "$srcDir\SmtcBridge.Injector\main.cpp" -lpsapi -lshell32
if ($LASTEXITCODE -ne 0) { throw '启动注入器构建失败' }

if ($Tests) {
    Write-Host ''
    Write-Host '[4/4] 构建注入测试靶子 ...' -ForegroundColor Cyan
    $testDir = Join-Path $outDir 'test'
    New-Item -ItemType Directory -Force -Path $testDir | Out-Null

    # 靶子必须叫 PotPlayerMini64.exe 才会命中注入模块的模块白名单
    & $gxx @commonFlags -o "$testDir\PotPlayerMini64.exe" "$root\tests\SmtcHookTestTarget\main.cpp"
    if ($LASTEXITCODE -ne 0) { throw '测试靶子构建失败' }

    # 替身 MediaDB64.dll：用来验证「后加载模块补挂」
    & $gxx @commonFlags -shared -o "$testDir\MediaDB64.dll" "$root\tests\SmtcHookTestTarget\late_dll.cpp" -lole32 -luuid -lruntimeobject
    if ($LASTEXITCODE -ne 0) { throw '替身 MediaDB64.dll 构建失败' }

    Copy-Item "$outDir\PotPlayerSmtcHook.dll" $testDir -Force
    # 空文件即开关，用于启用 CreateFileW 挂接
    New-Item -ItemType File -Force -Path "$testDir\PotPlayerSmtcHook.hookfiles" | Out-Null

    Write-Host '安全验证流程（不要拿真实播放器试第一次）：' -ForegroundColor Green
    Write-Host '  $p = Start-Process .\build\test\PotPlayerMini64.exe -PassThru -WindowStyle Hidden'
    Write-Host '  .\build\SmtcLoader.exe --name PotPlayerMini64.exe --dll .\build\test\PotPlayerSmtcHook.dll'
    Write-Host '  python .\tools\summarize-log.py'
}
