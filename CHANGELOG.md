# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.7.0] - 2026-10-06

### 变更

- **安装方式改为 IFEO 启动注入，不再改动 PotPlayer 的任何文件。**
  实测 `PotPlayerMini64.exe` 带 Themida(WinLicense) 壳并自查文件：
  - 尾部追加 4096 个零字节 → 拒绝启动，报 `Cannot find or init PotPlayer64.dll`；
  - 保持文件长度不变、只改导入表 → 同样拒绝启动（说明它连内容一起校验）；
  - 用转发壳顶掉 `PotPlayer64.dll` → 报 `PotPlayer64.dll is modified or hacked...`
    （主程序导入 `WINTRUST.dll`，会用 `WinVerifyTrust` 校验核心 DLL 的 Kakao 签名）。
- 新增 `PotPlayerSmtcInjector.exe`：由 IFEO 在每次启动 PotPlayer 时拉起，注入完立刻退出。
  - 必须用 `DEBUG_ONLY_THIS_PROCESS` 创建目标，否则 IFEO 会二次劫持 → 无限递归（实测 0→1→2→3）。
  - 创建后立即 `DebugActiveProcessStop` 脱离调试，实测目标 `PEB.BeingDebugged=0`、`NtGlobalFlag=0`。
  - 等目标加载器就绪（枚举到 `kernel32.dll`）再注入，避免远程线程跳到未映射地址把 PotPlayer 打崩。
- 新增 `IfeoInstaller`（HKLM 注册表读写、只删自己写的键）与
  `--install-ifeo` / `--uninstall-ifeo` 命令行。

### 停用

- `PePatcher.Install`（导入表补丁）停用并改为抛异常，只保留 `Restore`，
  供已经被打过补丁的机器还原回原版。
- 界面文案同步：`安装到 PotPlayer` → `安装启动注入`，`还原 PotPlayer` → `卸载启动注入`。

## [0.6.0] - 2026-10-06

### 新增

- **一次性安装：改写 PotPlayer 主程序的导入表**，让它每次启动时自己加载
  `PotPlayerSmtcHook.dll`。装一次之后**不需要任何常驻进程、不需要开机运行任何东西**，
  重启也照样生效；只有 PotPlayer 更新覆盖主程序后才需要重打一次。
  - 自动备份为 `<exe>.smtb-backup`，界面里可一键还原。
  - 做法：在最后一个节里追加一条导入描述符，并把新的导入表接在原有描述符之后。

### 修复（这是整条路走通的关键）

- **IAT 数组必须用 INT 的初值填充，不能留全零**。
  之前只写了 INT、把 IAT 留成零等着加载器回填——文件结构自检完全正常、
  导入目录指向正确、每个字段都能解析，但加载器会**静默跳过整条导入**：
  把被引用的 DLL 藏起来进程照样能启动，说明它根本没读这一条。
  改成 IAT 初值等于 INT 之后（链接器的惯例写法），产物与 CFF Explorer
  Rebuilder 的输出**逐字节一致**，加载器正常解析。
- 新数据的 RVA 需按 `SectionAlignment` 对齐（不是只按文件 `FileAlignment`），
  并截掉文件末尾不属于任何节的叠加数据（MinGW 的 COFF 符号表），
  把腾出来的区间清零。

### 变更

- 注入模块改为 **全静态链接**（`-static`），此前仍依赖 `libwinpthread-1.dll`，
  在没有 MinGW 的机器上会加载失败。

## [0.5.0] - 2026-10-06

### 修复

- **致命隐患：注入模块依赖 `libwinpthread-1.dll`**。
  此前只加了 `-static-libgcc -static-libstdc++`，仍会动态依赖 MinGW 的 pthread 运行库；
  在没装 MinGW、PATH 里也没有它的机器上，DLL 会直接加载失败。
  现已改为 `-static` 全静态链接，依赖只剩系统 DLL（KERNEL32 / msvcrt / ole32 / SHELL32 / USER32）。

### 新增（实验性，尚未完成）

- `PePatcher`：给 PotPlayer 主程序打「导入表补丁」，让 Windows 加载器每次启动
  PotPlayer 时自动载入 `PotPlayerSmtcHook.dll`。目标是「注册一次、之后无需任何常驻进程」。
  - 已实现：备份（`.smtb-backup`）、还原、PE 结构自检、GUI 按钮与 `--patch` / `--restore` 命令行。
  - **当前状态：未成功**。补丁文件的结构自检通过（导入目录指向新表、节大小与 SizeOfImage 一致），
    但实测加载器不会解析新增的导入项——把 DLL 藏起来后进程仍能正常启动，说明它根本没读到这条导入。
    原因待查。GUI 中该操作已**禁用**，不会误操作真实文件。

## [0.4.1] - 2026-10-06

### 变更

- **改为完全静默的后台驻留**：不再使用托盘图标。注册后进程没有窗口、没有托盘图标、
  任务栏也看不到，只在后台每秒检查一次 PotPlayer 是否启动，发现新进程就自动注入。
- 单实例：重复启动只会把已有实例的主界面叫出来，不会起第二份。
  需要改设置时运行 `PotPlayerSmtcBridge.exe --ui`，或在主界面点「退出后台程序」。
- 开机自启项改为注册界面程序自身（此前注册的是 `SmtcLoader.exe --watch`）；
  后台启动时会顺手校正该注册项，程序被移动过也能自愈。
- 自动注入改为传入**实际检测到的进程名**，兼容 `PotPlayer64` / `PotPlayerMini` 等版本。
- 注入模块的兜底等待从 8 秒缩短到 3 秒，自动注入后的首屏信息更新更快。

## [0.4.0] - 2026-10-06

### 新增

- **前端界面**（.NET 10 WinForms，深色主题）`PotPlayerSmtcBridge.exe`：
  - **首次使用向导**：说明用途与风险 → 自动检测 / 浏览指定 PotPlayer →
    校验组件是否就位 → 可选随系统启动自动注入 → 完成注册。
  - **主界面**：状态卡片（PotPlayer 路径、组件与运行状态）、元数据规则卡片、
    操作按钮（启动并注入 / 注入到已运行实例 / 后台监听 / 打开日志目录）、运行记录。
  - 深色配色为纯手工绘制（卡片圆角、自绘输入框与按钮、DWM 深色标题栏），
    同时调用 `Application.SetColorMode(Dark)` 让系统绘制的部分（滚动条、复选框）跟随。
  - 注入直接复用已经验证过的 `SmtcLoader.exe`，界面不重复实现注入逻辑。
- **规则可配置**：写入与 DLL 同目录的 `PotPlayerSmtcHook.ini`，DLL 每 2 秒重读一次，
  改规则不必重启播放器。

  ```ini
  [meta]
  tag_first=1
  name_pattern=%artist% - %title%
  ```

- **文件名「伪正则」**：用户可自己写规则，语法为 `%字段%` 占位符 + 字面量，`%%` 表示字面量百分号。

  | 规则 | 对 `[周杰伦] 夜曲.mp3` | 说明 |
  | :--- | :--- | :--- |
  | `%artist% - %title%` | 歌手段 / 标题段 | 默认 |
  | `[%artist%] %title%` | `周杰伦` / `夜曲` | 字面量按顺序查找 |
  | `%title%` | 整体作为标题 | 不做拆分 |

  界面里带**实时预览**：输入一个示例文件名立刻看到解析结果，与 DLL 用的是同一套算法。

### 变更

- `build.ps1` 增加前端构建步骤，`dotnet publish` 输出到 `build/`，与 DLL、注入器同目录。

## [0.3.0] - 2026-10-06

### 新增

- **接入标签读取，六个媒体属性一次补齐**：Title / Artist / AlbumArtist / Album /
  TrackNumber / Genres。
- 标签读取走 **Windows 自带的属性系统**（`SHGetPropertyStoreFromParsingName` +
  `IPropertyStore`），不引入任何第三方库：
  - MP3(ID3v2) / FLAC(Vorbis) / M4A / WMA 都由系统解码器覆盖；
  - `PROPERTYKEY` 用规范名（`System.Music.Artist` 等）在运行时通过
    `PSGetPropertyKeyFromName` 解析，**不硬编码任何 GUID**；
  - 多值字段（如 Artist）按 `; ` 连接。
- 取名规则（标签优先，文件名兜底）：

  | 字段 | 优先级 |
  | :--- | :--- |
  | Title | 标签 Title > 文件名 `" - "` 后半段 > 文件名整体 |
  | Artist | 标签 Artist > 文件名前半段 > 留空 |
  | AlbumArtist | 标签 AlbumArtist > 标签 Artist > 文件名前半段 > 留空 |
  | Album / TrackNumber / Genres | 标签，无则留空 |

- `Genres` 写入：取 `get_Genres` 返回的 `IVector<String>`，先 `Clear` 再逐项 `Append`；
  槽位下标 15 / 13 由 `Windows.Foundation.winmd` 导出的方法顺序推出。
- 标签读取放在独立线程（250ms 轮询当前文件），避免在 `CreateFileW` 探针里做磁盘 I/O；
  标签尚未读回时先用文件名兜底，保证切歌瞬间不出现空值。

### 修复

- **修复「第一首歌不生效、切歌后才生效」**：路径存储的临界区原先在兜底阶段才初始化，
  早于它的路径上报全部被丢弃。现在在安装文件探针之前就初始化。

## [0.1.2] - 2026-10-06

### 新增

- `tools/winmd-dump`：从系统自带的 `Windows.Media.winmd` 读取 WinRT 接口的权威定义。
- `docs/05-smtc-abi.md`：接口布局与 vtable 下标，全部由 winmd 实测导出，不再依赖记忆。

### 说明

- 实测确认：`IMusicDisplayProperties` 只含 Title / AlbumArtist / Artist，
  AlbumTitle / TrackNumber 在 `IMusicDisplayProperties2`，AlbumTrackCount 在 `IMusicDisplayProperties3`，
  三者需分别 `QueryInterface`。
- 实测确认：`Genres` **没有 setter**，只能通过 `IVector<HSTRING>` 追加。
- 首轮真机注入日志显示：钩子安装成功但从未触发，说明 PotPlayer 在一次运行中只创建一次 SMTC 对象。
  要捕获它必须让注入早于对象创建，即使用 `--watch` 或 `--launch`，不能在播放中后期注入。
- **不依赖任何硬编码 IID 的捕获链**：挂钩 `RoGetActivationFactory` 拿到激活工厂，
  再在其 vtable 下标 6 挂钩 `GetForWindow`，从而原样记录 `riid` 与 SMTC 对象指针，
  最后调用 `IInspectable::GetIids`（下标 3）让对象自己汇报全部接口 IID。
  仅当地址确认落在 `Windows.Media.MediaControl.dll` 时才挂钩，避免误挂。

### 修复

- **致命**：早期注入时钩子完全失效。
  PotPlayer 是分阶段加载模块的：注入时进程内只有 37 个模块，
  `PotPlayer64.dll` / `MediaDB64.dll` 尚未加载，因此 IAT 补丁一个都没打上
  （日志表现为 `hooked in 0 module(s)`）；等这两个模块后来加载时，
  它们的导入表是干净的，钩子等于没装。
  现在新增模块监视线程：前 6 秒每 10ms 轮询一次，之后每 200ms 一次（持续约 10 分钟），
  发现目标模块加载即补挂。
- IAT 补丁增加「已解析」校验：若目标槽位尚未被加载器填充，则本轮跳过、下轮重试，
  避免补丁被随后的导入解析覆盖。
- 新增回归测试：靶子在运行中途加载一个替身 `MediaDB64.dll`，用于验证补挂逻辑。
- **改用「主动激活 + 挂钩」取代被动等待**：实测 `MediaDB64.dll` 从加载到创建 SMTC
  对象不足 10ms，轮询必然慢一步。现在注入模块自己调用
  `RoGetActivationFactory("Windows.Media.SystemMediaTransportControls", IID_interop)`
  拿到进程内缓存的 interop 工厂，并在其 vtable 下标 6（`GetForWindow`）挂钩。
- 首次确认 interop IID `{DDB0472D-C911-4A1F-86D9-DC3D71A95F5A}`：
  既能在 `MediaDB64.dll` 偏移 `0x3869B0` 找到该 GUID 的字节序列，
  实际激活也返回 `S_OK`（IID 有误会返回 `E_NOINTERFACE`）。
- 挂钩前校验 `vtable[6]` 地址是否落在 `Windows.Media.MediaControl.dll` 内，实测通过。
- 新增 `LdrRegisterDllNotification` 即时补挂（轮询降为兜底）。
- 向量化异常处理器不再记录 `OutputDebugString` / 线程命名产生的伪异常。
- **修复「第一首歌不生效、切歌后才生效」**：记录当前文件路径用的临界区原先在
  `SmtcWriter_Attach`（注入后 8 秒的兜底阶段）才初始化，
  而 `CreateFileW` 探针在那之前就已经开始上报路径，全部被 `if (!g_fileLockReady)`
  丢掉，导致首次写入无数据可写。改用 `INIT_ONCE` 惰性初始化，两侧都调用。
- **改用导出表（EAT）跳转，彻底消除时序竞争**：IAT 补丁只能覆盖「此刻已加载」的模块，
  而实测 `MediaDB64.dll` 从加载到创建 SMTC 会话不足 3ms，DLL 加载通知回调
  （在 DllMain 之后才触发）根本来不及。
  现在改为在 `combase.dll` 的导出表上做跳转：先在模块附近 4GB 内分配跳板内存，
  写入 `mov rax, <钩子>; jmp rax`，再把导出地址改指跳板。
  之后任何模块（含尚未加载的）在解析导入或 `GetProcAddress` 时拿到的都是钩子地址，
  不再存在时间窗口。
  注：`MEM_FREE` 区段起始地址只保证 4KB 对齐，需向上取整到分配粒度（64KB），
  否则 `VirtualAlloc` 返回 `ERROR_INVALID_ADDRESS`。

## [0.1.1] - 2026-10-06

### 修复

- **致命**：宽字符格式串里混用 `%s` 导致宿主进程崩溃。
  MinGW 在 `-std=c++17` 下按 MSVCRT 语义解析格式串，宽字符函数的 `%s` 期望 `char*`；
  传 `wchar_t*` 会被当作窄字符串解引用，轻则输出为空（模块列表 148 行全空），
  重则读到野指针崩在 `msvcrt.dll`。日志模块改为全程窄字符 + 显式 `WideToUtf8`。
- **致命**：IAT 遍历在 `OriginalFirstThunk == 0` 的导入描述符上按名称表解析，
  会把 IAT 中已解析的函数地址当成 RVA，得到野指针。
  现在跳过此类描述符，并对导入目录 / thunk 数组 / 名称 RVA 全部做越界校验。
- 注入器 `PrintW` 的同一处 `%s` / `%ls` 混用，导致 DLL 路径只打印出首字母。

### 变更

- IAT 挂接从「全部模块」（实测 148 个）收窄到**模块白名单**（PotPlayer / MediaDB 共 8 项）。
- `CreateFileW` 挂接改为**默认关闭**，需在 DLL 同目录放置 `PotPlayerSmtcHook.hookfiles` 才启用。
- 新增向量化异常处理器：崩溃时记录异常码与出错地址所属模块。
- 新增注入测试靶子 `tests/SmtcHookTestTarget`，可在不接触真实播放器的前提下验证全流程。

## [0.1.0] - 2026-10-06

### 新增

- 仓库初始化：README、MIT 许可、`.gitignore`
- `SmtcLoader.exe`：支持三种注入方式（指定进程名、挂起启动、后台监听）
- `PotPlayerSmtcHook.dll`：侦察版注入模块
  - 记录 PotPlayer 的 WinRT 激活调用（类名 + 请求的 IID + 返回指针）
  - 记录 `CreateFileW` 打开的媒体文件路径（作为当前播放文件路径的来源）
  - 记录进程内窗口列表与已加载模块列表
  - 全部输出到 `%TEMP%\potplayer-smtc-bridge\`
- `tools/summarize-log.py`：日志汇总脚本
- `docs/`：可行性论证、技术架构、字段映射、注入风险四份文档

### 说明

- 本版本**不写入任何 SMTC 字段**，只做数据采集。写入逻辑依赖本版本采集到的真实 IID，将在 v0.2 实现。
