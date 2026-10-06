# potplayer-smtc-bridge

让 PotPlayer 向 Windows SMTC（System Media Transport Controls，系统媒体传输控件）输出**完整的媒体元数据**。

> 状态：**v0.1.1 侦察版**。当前版本能注入、能抓数据，但**还不会写字段**。原因见下方"为什么先做侦察版"。
>
> ⚠️ v0.1 曾因宽字符格式串误用导致宿主崩溃，详见 [CHANGELOG](CHANGELOG.md)。**首次验证请使用内置测试靶子，不要直接拿真实播放器试。**

---

## 这个项目要解决什么

PotPlayer 通过 SMTC 对外暴露的会话里，只有 36 个字段中的 30 个是填好的。缺的是媒体属性里最关键的 6 项：

| 字段 | PotPlayer 现状 |
| :--- | :--- |
| Title | 有，但内容是**带扩展名的文件名** |
| Subtitle / Artist / AlbumArtist / AlbumTitle / TrackNumber / AlbumTrackCount / Genres | **全空** |
| Thumbnail | 有，但是未压缩的 BMP（常见 1.5 MB 上下） |
| PlaybackType | 有，但放视频也报 Music |

其余 30 个字段（播放状态、倍速、循环/随机、时间轴、15 个可用操作）PotPlayer 都填得很完整，本项目不动它们。

补齐之后，任何读取 SMTC 的程序（Windows 媒体浮窗、Discord、Last.fm 客户端、各类歌词工具）都能拿到正确的歌手、专辑、曲目号和封面。

## 为什么需要注入

Windows 的 SMTC 是**"写自己的、读别人的"**：

- 写接口 `Windows.Media.SystemMediaTransportControls` 是进程内对象，只有创建它的进程能改；
- 读接口 `GlobalSystemMediaTransportControlsSessionManager` 属性全部只读。

**系统没有提供任何公开 API 能让外部程序修改别的进程的 SMTC 会话属性。** 所以要让 PotPlayer 自己的那个会话带上完整信息，代码就必须运行在 PotPlayer 进程内部。

PotPlayer 的 SMTC 实现位于 `MediaDB64.dll`（内部代号 StreetPlayer），本项目通过进程内挂接它的 WinRT 激活调用与文件打开调用来取得所需信息。

## 为什么先做侦察版

调用 SMTC 的写接口需要知道三个 COM 接口的 **IID** 和**方法在 vtable 中的顺序**。本机没有安装 Windows SDK 头文件，凭记忆硬写 IID 有静默失败的风险。

因此 v0.1 的任务是：**在真实运行中把 PotPlayer 请求的 IID 原样记录下来**，而不是猜。

拿到日志里的真实 IID 之后，v0.2 再补上写入逻辑，届时每个常量都有实证来源。

---

## 目录结构

```
.
├─ build.ps1                      # MinGW 一键构建
├─ docs/
│  ├─ 01-feasibility.md           # 可行性论证与字段缺口分析
│  ├─ 02-architecture.md          # 技术架构与实时性设计
│  ├─ 03-smtc-field-map.md        # SMTC 全字段清单与数据来源映射
│  ├─ 04-injection-risks.md       # 注入的完整风险清单与缓解措施
│  └─ 05-smtc-abi.md              # 接口布局与 vtable 下标（从 winmd 提取）
├─ src/
│  ├─ SmtcLoader/                 # 注入器（x64 控制台程序）
│  └─ PotPlayerSmtcHook/          # 注入到 PotPlayer 的侦察 DLL
├─ tests/
│  └─ SmtcHookTestTarget/         # 注入测试靶子（冒充 PotPlayerMini64.exe）
└─ tools/
   ├─ summarize-log.py            # 日志汇总，提取 IID 与关键线索
   └─ winmd-dump/                 # 从 Windows.Media.winmd 读取接口定义
```

## 构建

需要 **MinGW-w64 g++（x86_64）**，无需 Visual Studio。

```powershell
.\build.ps1            # 只构建注入器与注入模块
.\build.ps1 -Tests     # 额外构建测试靶子（推荐）
```

产物在 `build/`：

| 文件 | 说明 |
| :--- | :--- |
| `SmtcLoader.exe` | 注入器 |
| `PotPlayerSmtcHook.dll` | 被注入的侦察模块 |

## 使用

### 安装方式：IFEO 启动注入（v0.7 起）

> v0.6 的「改写主程序导入表」已废弃。实测 `PotPlayerMini64.exe` 带 Themida(WinLicense) 壳，
> 会自查文件长度与内容；`PotPlayer64.dll` / `MediaDB64.dll` 又都带 Kakao 签名、
> 主程序用 `WinVerifyTrust` 校验它们。**动 PotPlayer 的任何文件都会让它拒绝启动。**
> 证据见 [docs/06-ifeo-install.md](docs/06-ifeo-install.md)。

安装时只在注册表里写一个键：

```
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PotPlayerMini64.exe
    Debugger = "<本程序目录>\PotPlayerSmtcInjector.exe"
```

之后每次启动 PotPlayer，Windows 会先拉起 `PotPlayerSmtcInjector.exe`：

1. 用 `DEBUG_ONLY_THIS_PROCESS` 创建 PotPlayer —— 这是为了绕开 IFEO 的二次劫持
   （普通 `CreateProcess` 会 level 0→1→2→3 无限递归，实测）；
2. 目标进程由内核保持挂起，随即 `DebugActiveProcessStop` 脱离：
   主线程执行第一条指令之前 `PEB.BeingDebugged` 就已经是 0（实测），Themida 看不到调试器；
3. 等它的加载器就绪（`kernel32.dll` 已映射）后 `CreateRemoteThread(LoadLibraryW)`
   注入 `PotPlayerSmtcHook.dll`；
4. 注入器自己立刻退出。

没有常驻进程、没有服务、没有开机启动项，也不改动 PotPlayer 的任何文件。
卸载就是删掉那个注册表键（界面里一键完成）。PotPlayer 自己更新后也不需要重装。

### 图形界面（推荐）

`build\PotPlayerSmtcBridge.exe` 是深色主题的前端。**只有第一次运行会弹窗口**——
一个注册向导，用来指定 PotPlayer、校验组件、写入配置并登记开机自启。

之后它就完全静默：没有窗口、没有托盘图标、任务栏也看不到，只在后台每秒检查一次
PotPlayer 是否启动，发现新的进程就自动注入。用户不会察觉任何东西。

需要改设置时：

```powershell
.\build\PotPlayerSmtcBridge.exe --ui     # 唤出主界面（不会另起一份进程）
```

主界面里有元数据规则的编辑与**实时预览**，以及「退出后台程序」按钮。
关闭主界面只是收起，后台继续待命。

文件名解析支持自定义「伪正则」：`%title% %artist% %album% %albumArtist% %track%`
是占位符，其余字符按字面量匹配，`%%` 表示一个字面量百分号。
规则写在 `PotPlayerSmtcHook.ini`，DLL 每 2 秒重读，改完不必重启播放器。

### 命令行

### 第 0 步：先用测试靶子验证（强烈建议）

注入类工具最怕"第一次就跑在真实播放器上"。仓库自带一个无害靶子，它只打开几个假媒体文件然后挂着等你注入：

```powershell
.\build.ps1 -Tests
$p = Start-Process .\build\test\PotPlayerMini64.exe -PassThru -WindowStyle Hidden
.\build\SmtcLoader.exe --name PotPlayerMini64.exe --dll .\build\test\PotPlayerSmtcHook.dll
python .\tools\summarize-log.py
```

日志里应当能看到：模块列表（带 base/size）、窗口列表、`combase resolved`、`CreateFileW hooked in 1 module(s)`，以及若干 `[open] ...mp3` 记录。看到这些就说明整条链路是通的，可以进入下一步。

### 第 1 步：注入真实的 PotPlayer

**方式一：注入到已经运行的 PotPlayer**

```powershell
.\build\SmtcLoader.exe --name PotPlayerMini64.exe --dll .\build\PotPlayerSmtcHook.dll
```

**方式二：由注入器启动 PotPlayer（挂起注入，不丢早期调用）**

```powershell
.\build\SmtcLoader.exe --launch "C:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe" --dll .\build\PotPlayerSmtcHook.dll
```

**方式三：后台等待 PotPlayer 出现后自动注入**

```powershell
.\build\SmtcLoader.exe --watch --dll .\build\PotPlayerSmtcHook.dll
```

注入后在 PotPlayer 里播放任意一个文件，日志会写到：

```
%TEMP%\potplayer-smtc-bridge\hook-<pid>-<时间戳>.log
```

汇总查看：

```powershell
python .\tools\summarize-log.py
```

## 路线图

| 版本 | 内容 | 状态 |
| :--- | :--- | :--- |
| v0.1 | 注入器 + 侦察 DLL：记录 WinRT 激活 IID、文件打开、窗口列表、模块列表 | ✅ 已完成 |
| v0.1.1 | 修复崩溃缺陷；挂接收窄到模块白名单；文件挂接默认关闭；新增异常记录与测试靶子 | ✅ 本版本 |
| v0.2 | 依据实测 IID 拿到 SMTC 对象，写入 Artist / Album 等字段（先用文件名解析验证通路） | 待做 |
| v0.3 | 接入标签解析，补齐 6 个媒体属性 + JPEG 封面 | 待做 |
| v0.4 | 稳定性加固：只读模式、开关文件、minidump、异常隔离 | 待做 |
| v1.0 | 自动注入形态定稿 + 打包 | 待做 |

## 排障

**注入后宿主进程崩溃**

先看 `%TEMP%\potplayer-smtc-bridge\` 里最新日志的最后几行。若存在 `[veh]` 开头的行，它记录了异常码与出错地址所属模块，直接把它发出来即可定位。

崩溃产物在 `%APPDATA%\DAUM\PotPlayer\Log\`（`PotPlayer.exc.xml` + `.exc.dmp`）。

**日志里模块列表或窗口列表是空行**

说明碰到了 v0.1 的宽字符格式化缺陷，升级到 v0.1.1 即可。

**想临时关掉文件挂接**

删除 DLL 同目录下的 `PotPlayerSmtcHook.hookfiles` 标记文件，重启目标进程生效。

**注入成功但日志里没有任何 WinRT 激活记录**

先看 `[probe] 初次挂接完成，命中 N 个导入项` 这行的 N：

- `N = 0` 说明注入时 `PotPlayer64.dll` / `MediaDB64.dll` 都还没加载；
  正常情况下模块监视线程会在随后输出 `[watch] xxx 补挂 M 个导入项`。
  若始终没有，说明目标模块的名字不在白名单里。
- `N > 0` 但仍无激活记录，说明 PotPlayer 在本进程内**只创建一次** SMTC 对象，
  且创建时刻早于注入。此时必须用 `--watch`（先监听再启动播放器）或 `--launch`。

**重复注入同一个 DLL 不会生效**

`LoadLibraryW` 对已加载的模块只会增加引用计数，新代码不会运行。
换版本后必须先完全退出目标进程，再重新注入。

## 已知风险

本项目采用进程注入，代价是真实存在的，完整清单见 [`docs/04-injection-risks.md`](docs/04-injection-risks.md)。简要版本：

- **崩溃风险**：被注入的代码与 PotPlayer 同进程，任何异常都可能带崩播放器；
- **杀软误报**：远程线程注入是安全软件的重点监控行为，需要自行加白名单；
- **系统缓解措施**：开启 ACG/CIG 时注入会直接失败；
- **版本兼容**：PotPlayer 更新可能影响实现，故采用 COM vtable 挂接而非硬编码偏移。

## 免责声明

本项目通过进程注入方式修改 PotPlayer 的运行时行为，属于非官方的个人自用工具，与 Kakao Corp. 无关，不受其支持。

建议仅在自有设备上使用，不要分发预编译的注入器。使用前请确认已了解上述风险。

## 许可

[MIT](LICENSE)
