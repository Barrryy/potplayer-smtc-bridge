# PotPlayer SMTC Bridge

为 PotPlayer 发布到 Windows **SMTC**（System Media Transport Controls，系统媒体传输控件）的会话补齐完整媒体元数据。

[English](README.md) · **中文**

---

## 项目目的

PotPlayer 发布的 SMTC 会话中有多项媒体属性为空或不可用，主要缺口如下：

| 属性 | PotPlayer 现状 |
| :--- | :--- |
| `Title` | 有值，但内容为**含扩展名的文件名** |
| `Subtitle`、`Artist`、`AlbumArtist`、`AlbumTitle`、`TrackNumber`、`AlbumTrackCount`、`Genres` | 空 |
| `Thumbnail` | 有值，但为未压缩的 BMP（通常在 1.5 MB 上下） |
| `PlaybackType` | 有值，但播放视频时同样上报 `Music` |

其余属性（播放状态、播放速率、循环与随机状态、时间轴、可用操作）由 PotPlayer 正确填充，本项目不做修改。

补齐上述属性后，任何读取 SMTC 的程序（Windows 媒体浮窗、Discord、Last.fm 客户端、各类歌词工具）都能获得正确的标题、歌手、专辑、曲目号与封面。

## 为什么需要进程注入

Windows SMTC 的读写接口是分离的：

- 写入接口 `Windows.Media.SystemMediaTransportControls` 是进程内对象，只有创建它的进程能够访问；
- 读取接口 `GlobalSystemMediaTransportControlsSessionManager` 全部为只读属性。

系统未提供任何公开 API 允许一个进程修改另一个进程的 SMTC 会话，因此 PotPlayer 会话的元数据只能由运行在 PotPlayer 进程内部的代码补全。

PotPlayer 的 SMTC 实现位于 `MediaDB64.dll`（内部代号 `StreetPlayer`）。本项目挂接该模块的 WinRT 激活调用与文件打开调用，并在宿主提交之前通过 display updater 写入媒体属性。

## 安装机制（IFEO 启动注入）

> v0.6 采用的「改写导入表」方式已废弃。`PotPlayerMini64.exe` 使用 Themida（WinLicense）加壳，会校验文件长度与内容；`PotPlayer64.dll` 与 `MediaDB64.dll` 带有 Kakao 代码签名证书，并通过 `WinVerifyTrust` 校验。修改 PotPlayer 的任何文件都会导致其拒绝启动。完整证据见 [docs/06-ifeo-install.md](docs/06-ifeo-install.md)。

安装时仅写入一个注册表键：

```
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PotPlayerMini64.exe
    Debugger = "<安装目录>\PotPlayerSmtcInjector.exe"
```

此后每次启动 PotPlayer，Windows 会先启动注入器。注入器的执行流程如下：

1. 以 `DEBUG_ONLY_THIS_PROCESS` 创建 PotPlayer 进程，以避免 IFEO 对子进程二次拦截（普通 `CreateProcess` 会导致无限递归）；
2. 在目标仍被内核挂起时调用 `DebugActiveProcessStop` 脱离调试，使主线程执行第一条指令前 `PEB.BeingDebugged` 已为 0，Themida 不会观测到调试器；
3. 等待目标加载器就绪（`kernel32.dll` 已映射）后，通过 `CreateRemoteThread(LoadLibraryW)` 注入 `PotPlayerSmtcHook.dll`；
4. 注入器立即退出。

该方案不使用常驻进程、Windows 服务或开机启动项，也不修改 PotPlayer 的任何文件。卸载只需删除上述注册表键；PotPlayer 自身更新后无需重新安装。

## 环境要求

- Windows 10 或 Windows 11，x64
- PotPlayer x64（注入器针对 `PotPlayerMini64.exe`）
- 安装需要管理员权限（IFEO 键位于 `HKLM`）
- 从源码构建需要 MinGW-w64 g++（x86_64）与 .NET 10 SDK

## 构建

```powershell
$env:Path = "D:\mingw64\bin;" + $env:Path
.\build.ps1            # 注入器、注入模块、启动注入器与图形界面
.\build.ps1 -Tests     # 额外构建测试靶子
```

产物输出至 `build/`：

| 文件 | 说明 |
| :--- | :--- |
| `PotPlayerSmtcInjector.exe` | 由 IFEO 规则启动的注入器 |
| `PotPlayerSmtcHook.dll` | 注入 PotPlayer 进程的挂接模块 |
| `PotPlayerSmtcBridge.exe` | 配置界面 |
| `SmtcLoader.exe` | 用于手动测试的独立注入器 |

## 安装与卸载

1. 运行 `build\PotPlayerSmtcBridge.exe`；
2. 在首次运行向导中指定 PotPlayer 主程序并确认元数据规则；
3. 在主界面点击「安装启动注入」，并在提权提示中确认。

安装完成后按平时方式启动 PotPlayer 即可，无需其他操作。

卸载时在主界面点击「卸载启动注入」，或执行：

```powershell
Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PotPlayerMini64.exe' -Recurse -Force
```

## 文件与位置

| 路径 | 内容 |
| :--- | :--- |
| `%LOCALAPPDATA%\PotPlayerSmtcBridge\` | 安装后的注入器、注入模块、规则文件与文件挂接标记 |
| 程序目录 | 源码、构建产物与配置界面 |
| `%TEMP%\potplayer-smtc-bridge\` | 运行日志（`injector.log`、`hook-<pid>-<时间戳>.log`） |

**请勿手动删除或移动安装目录中的文件。** IFEO 规则指向该目录中的注入器；文件缺失时 Windows 无法启动 PotPlayer。如已发生，删除上述注册表键即可恢复正常。

## 元数据规则

文件名不符合统一格式时，元数据通过 Windows 属性系统从文件标签读取。取值顺序如下：

| 字段 | 取值顺序 |
| :--- | :--- |
| `Title` | 标签 `Title` → 文件名中 `" - "` 之后的部分 → 整个文件名 |
| `Artist` | 标签 `Artist` → 文件名中 `" - "` 之前的部分 → 空 |
| `AlbumTitle` | 标签 `Album` → 空 |
| `AlbumArtist` | 标签 `AlbumArtist` → 标签 `Artist` → 文件名前半段 → 空 |
| `TrackNumber`、`AlbumTrackCount`、`Genres` | 标签 |

文件名模式使用占位符 `%title%`、`%artist%`、`%album%`、`%albumArtist%`、`%track%`；其余字符按字面量匹配，`%%` 表示字面量百分号。规则保存在注入模块同目录的 `PotPlayerSmtcHook.ini`，每两秒重新读取一次，修改后无需重启播放器。

曲目未提供某项属性时该属性会被清空，因此不会残留上一首曲目的信息。

## 命令行

| 命令 | 说明 |
| :--- | :--- |
| `PotPlayerSmtcBridge.exe` | 打开主界面 |
| `PotPlayerSmtcBridge.exe --silent` | 无窗口运行，直到收到唤醒信号 |
| `PotPlayerSmtcBridge.exe --install-ifeo <播放器.exe>` | 安装 IFEO 规则（需要提权） |
| `PotPlayerSmtcBridge.exe --uninstall-ifeo <播放器.exe>` | 移除 IFEO 规则（需要提权） |
| `PotPlayerSmtcBridge.exe --restore <播放器.exe>` | 还原被 v0.6 修改过的播放器主程序（需要提权） |
| `SmtcLoader.exe --name <进程名>` | 注入到已运行的进程 |
| `SmtcLoader.exe --launch <播放器.exe>` | 挂起启动播放器并在启动阶段注入 |
| `SmtcLoader.exe --watch` | 等待播放器启动后注入 |

## 使用内置靶子验证

测试靶子模拟 `PotPlayerMini64.exe`，打开若干占位媒体文件并等待注入，运行安全，建议在接触真实播放器之前先用于验证构建：

```powershell
.\build.ps1 -Tests
$p = Start-Process .\build\test\PotPlayerMini64.exe -PassThru
.\build\SmtcLoader.exe --name PotPlayerMini64.exe --dll .\build\test\PotPlayerSmtcHook.dll
python .\tools\summarize-log.py
```

正常情况下日志中应包含模块列表、窗口列表、已修补的导入项，以及每个已打开文件的 `[open]` 记录。

## 排障

| 现象 | 处理方式 |
| :--- | :--- |
| PotPlayer 无法启动 | IFEO 规则指向的注入器已不存在。删除上述注册表键。 |
| Windows 中无元数据 | 确认注入器日志包含 `inject ok`，并检查安装目录中四个文件是否齐全。 |
| 仍显示上一首的字段 | 升级到当前版本；早期版本不会清空空属性。 |
| 元数据取自文件名 | 文件没有标签，或主界面中的规则顺序已被修改。 |
| 在加固配置下注入失败 | 进程缓解策略（ACG/CIG）会阻止注入。 |

日志位于 `%TEMP%\potplayer-smtc-bridge\`；如宿主生成崩溃转储，位于 `%APPDATA%\DAUM\PotPlayer\Log\`。

## 目录结构

```
.
├─ build.ps1                      # 构建脚本（MinGW + .NET）
├─ docs/                          # 设计说明与字段映射
│  ├─ 01-feasibility.md           # 可行性论证与字段缺口分析
│  ├─ 02-architecture.md          # 技术架构与实时性设计
│  ├─ 03-smtc-field-map.md        # SMTC 字段清单与数据来源映射
│  ├─ 04-injection-risks.md       # 注入风险清单与缓解措施
│  ├─ 05-smtc-abi.md              # 接口布局与 vtable 下标
│  └─ 06-ifeo-install.md          # IFEO 安装机制与实测证据
├─ src/
│  ├─ SmtcBridge.App/             # 配置界面（.NET）
│  ├─ SmtcBridge.Injector/        # 启动注入器
│  ├─ PotPlayerSmtcHook/          # 挂接模块
│  └─ SmtcLoader/                 # 独立注入器
├─ tests/
│  ├─ SmtcHookTestTarget/         # 注入测试靶子
│  └─ IfeoProbe/                  # IFEO 与调试脱离子验证程序
└─ tools/
   ├─ uninstall-smtc-bridge.ps1   # 卸载脚本
   ├─ summarize-log.py            # 日志汇总
   └─ winmd-dump/                 # 从 Windows.Media.winmd 导出接口定义
```

## 已知风险

进程注入存在固有代价，完整清单见 [docs/04-injection-risks.md](docs/04-injection-risks.md)。

- **稳定性**：挂接模块与 PotPlayer 同进程运行，未处理的故障可能导致播放器退出；
- **杀软误报**：远程线程注入属于安全软件重点监控行为，可能需要添加白名单；
- **进程缓解策略**：启用 ACG/CIG 时注入会直接失败；
- **版本兼容性**：PotPlayer 更新可能改变内部行为；本项目挂接 COM vtable 与导出表，不依赖硬编码偏移。

## 免责声明

本项目通过进程注入修改 PotPlayer 的运行时行为，属于非官方个人自用工具，与 Kakao Corp. 无关，不受其支持。

请仅在自有设备上使用；请勿分发预编译的注入器。使用前请确认已理解上述风险。

## 许可

[MIT](LICENSE)
