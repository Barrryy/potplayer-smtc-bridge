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
│   CreateFileW ◄──挂接      ISystemMediaTransportControls           │
│        │                        ▲                                 │
│        │ 记录路径                │ 补写字段                          │
│        └────────► [PotPlayerSmtcHook.dll] ─────┘                   │
│                        │                                          │
│                        │ 读取标签（TagLib）                        │
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

**主方案：挂接 `CreateFileW` + 文件名关联**

1. 记录所有扩展名为媒体格式的打开路径（环形缓冲，保留最近 N 条）；
2. 当 SMTC 的 Title 更新为 `X.mp3` 时，取 basename 等于 `X.mp3` 的最近一条。

关联键天然精确：PotPlayer 写入 SMTC 的 Title 就是"带扩展名的文件名"。

**兜底方案：进程内内存扫描**

已知文件名，扫描已提交内存中的 UTF-16 字符串，寻找以该文件名结尾、且含盘符与反斜杠的更长子串。需要三重校验：文件存在、扩展名合法、与当前 Title 一致。

## 写入时机

补写必须在 PotPlayer 自己调用 `ISystemMediaTransportControlsDisplayUpdater::Update()` **之后**执行，否则会被它覆盖。因此需要挂接该方法的 vtable 槽位。

## 元数据解析

拿到路径后读取文件标签。需要覆盖的格式：MP3、FLAC、M4A、OGG、WMA、WAV、MP4、MKV。

字段映射见 [`03-smtc-field-map.md`](03-smtc-field-map.md)。

## 关键技术选择

| 决策 | 选择 | 理由 |
| :--- | :--- | :--- |
| 挂接方式 | IAT 挂接 + COM vtable 挂接 | 不依赖硬编码偏移，抗 PotPlayer 版本更新 |
| 是否用第三方 hook 库 | 否 | 自行实现约 200 行即可，避免引入 Detours 依赖 |
| 未导出符号的获取 | 运行时 `GetProcAddress` | 避免链接期依赖，绕开 MinGW 缺少 SDK 头的问题 |
