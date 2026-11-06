# 技术架构

## 数据流

```
┌─────────────────────── PotPlayerMini64.exe ───────────────────────┐
│                                                                   │
│  PotPlayer64.dll          MediaDB64.dll                           │
│  （播放核心）              （媒体库 + SMTC 实现）                    │
│        │                        │                                 │
│        │ 打开媒体文件            │ 属性写入                          │
│        ▼                        ▼                                 │
│ CreateFileW / CreateFile2       ISystemMediaTransportControls      │
│        │  ◄── 挂接                   ▲                             │
│        │                             │ 补写字段                     │
│        ├────────► [PotPlayerSmtcHook.dll] ─────┘                   │
│        │                   │                                      │
│  扫描文件句柄              │ 读取 Windows 属性系统标签              │
│        └───────────────────┘                                      │
│                        ▼                                          │
└────────────────────────┼──────────────────────────────────────────┘
                         │
                         ▼
              Windows SMTC 会话属性
                         │
                         ▼
        所有 SMTC 消费者（媒体浮窗 / Discord / 歌词工具 …）
```

## 组件

| 组件 | 形态 | 职责 |
| :--- | :--- | :--- |
| `SmtcLoader.exe` | 独立进程 | 注入 DLL；支持挂起启动注入与后台监听注入 |
| `PotPlayerSmtcHook.dll` | 进程内模块 | 采集与写入。所有 hook 都在 PotPlayer 进程内，影响范围仅限该进程 |

## 注入时机与三种模式

| 模式 | 命令 | 特点 |
| :--- | :--- | :--- |
| 注入已运行进程 | `--name` | 最简单，但可能错过启动期的早期调用 |
| 挂起启动 | `--launch` | 用 `CREATE_SUSPENDED` 启动目标进程，注入后再 `ResumeThread`，**保证 DLL 早于目标进程的任意业务代码就位** |
| 后台监听 | `--watch` | 轮询等待目标进程出现即注入，覆盖"从任务栏/文件关联/拖拽启动"的情况 |

推荐日常使用 `--watch`，因为它不改变用户启动 PotPlayer 的习惯。

## 关于"晚注入"

如果注入发生在 PotPlayer 创建 SMTC 对象之后，就不能再靠挂接 `RoActivateInstance` 去截获对象。此时的可选路径：

```
ISystemMediaTransportControlsInterop::GetForWindow(hwnd, iid, ppv)
```

该 API 的语义是"**按窗口取，没有就创建**"。因此只要拿到 PotPlayer 的窗口句柄，再调用同一个 API，即可获得**同一个 SMTC 会话**的接口指针，无需提前挂接。

若 PotPlayer 使用的是隐藏消息窗口而非主窗口，则枚举该进程全部窗口逐个尝试，用 `PlaybackStatus` 是否与已知状态一致来判定。

这条路径需要在 v0.2 优先验证；验证通过后，`--watch` 模式的可靠性将不再依赖注入时机。

## 当前播放文件路径的获取

**即时路径：挂接 `CreateFileW` / `CreateFile2`**

目标播放器模块和各新版媒体模块里的文件导入会立即上报媒体路径。新版
PotPlayer 的部分文件读取由更早加载、已经解析完导入的 CRT 或系统模块完成，
因此这条路径不再是唯一来源。

**兜底路径：扫描进程自己的文件句柄**

每 250 ms 通过 `NtQuerySystemInformation(SystemExtendedHandleInformation)`
枚举当前进程的句柄，只复制文件类型句柄并用
`GetFinalPathNameByHandleW` 解析路径。这样不依赖 PotPlayer 最终通过哪一个
文件 API 打开媒体，也能覆盖启动时已经打开、注入后才开始处理的文件。

若有多个媒体句柄同时存在，优先选择与 PotPlayer 主窗口标题中的文件名/主文件名
匹配的一项；切歌时再优先选择新出现的句柄。

## 写入时机

补写必须在 PotPlayer 自己调用 `ISystemMediaTransportControlsDisplayUpdater::Update()`
**之前**写入 MusicProperties，再调用原 `Update()` 提交。因此挂接该方法的 vtable
槽位。路径或标签线程发现新信息时也会主动执行一次同样的提交，不依赖宿主下一次
切歌或刷新。

## 元数据解析

拿到路径后读取文件标签。需要覆盖的格式：MP3、FLAC、M4A、OGG、WMA、WAV、MP4、MKV。

字段映射见 [`03-smtc-field-map.md`](03-smtc-field-map.md)。

## 关键技术选择

| 决策 | 选择 | 理由 |
| :--- | :--- | :--- |
| 挂接方式 | IAT 挂接 + COM vtable 挂接 | 不依赖硬编码偏移，抗 PotPlayer 版本更新 |
| 是否用第三方 hook 库 | 否 | 自行实现约 200 行即可，避免引入 Detours 依赖 |
| 未导出符号的获取 | 运行时 `GetProcAddress` | 避免链接期依赖，绕开 MinGW 缺少 SDK 头的问题 |
