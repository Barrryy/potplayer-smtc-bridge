# 可行性论证

## 结论

让 PotPlayer 向 SMTC 输出完整元数据**可行**，但**无法通过 PotPlayer 官方扩展机制实现**，必须把代码注入到 PotPlayer 进程内部。

## 一、为什么官方扩展做不到

SMTC 的 API 模型是单向的：

| 方向 | 接口 | 能力 |
| :--- | :--- | :--- |
| 写 | `Windows.Media.SystemMediaTransportControls` | 进程内对象，只能由创建它的进程修改 |
| 读 | `GlobalSystemMediaTransportControlsSessionManager` | 属性全部只读，仅可发起传输控制（播放/暂停/跳转） |

**系统没有提供任何公开 API 能让外部进程修改另一个进程的 SMTC 会话属性。** 因此要改变 PotPlayer 自己的会话内容，代码必须在 PotPlayer 进程内运行。

PotPlayer 的 AngelScript 扩展（`Extension\` 目录）也走不通：

1. 扩展只在对应功能被调用时按需加载，没有常驻执行时机；
2. `HostLoadLibrary` / `HostCallProc` 只能调用 C 风格导出函数，无法构造 HSTRING、获取 COM 接口指针；
3. 拿不到 `MediaDB64.dll` 内部持有的 SMTC 接口指针。

## 二、PotPlayer 的 SMTC 实现在哪

在 64 位 PotPlayer（26.08.19.0）上做的实测：

**模块加载情况**（`PotPlayerMini64.exe` 进程内）：

```
PotPlayerMini64.exe
PotPlayer64.dll
MediaDB64.dll                     <- SMTC 实现方
Windows.Media.MediaControl.dll    <- 系统 SMTC 实现
```

**`MediaDB64.dll` 中的相关符号**：

- `Windows.Media.SystemMediaTransportControls`
- `Windows.Media.SystemMediaTransportControlsTimelineProperties`
- `RoActivateInstance` / `api-ms-win-core-winrt-l1-1-0.dll`
- WRL 事件处理器：`ButtonPressed`、`ShuffleEnabledChangeRequested`、`PlaybackRateChangeRequested`、`AutoRepeatModeChangeRequested`
- 缩略图处理：`ThumbnailAsyncState`、`JFIF extension marker`

PDB 路径进一步确认了归属：

```
F:\kakao\work\StreetPlayer\ExtraProgram\MediaDB\x64\Release\MediaDB64.pdb
```

`StreetPlayer` 是 PotPlayer 的内部代号。

## 三、缺口量化

SMTC 全部字段共 **36 个**，PotPlayer 已经填好 **30 个**，缺口是媒体属性里的 6 项。详见 [`03-smtc-field-map.md`](03-smtc-field-map.md)。

## 四、路径来源的取舍

补齐元数据需要拿到**当前播放文件的完整路径**。评估过的来源：

| 来源 | 结论 |
| :--- | :--- |
| `%APPDATA%\PotPlayerMini64\MediaInfo.sdb` | ❌ 否决。实测该库是**媒体库扫描缓存**：18 条记录 `time` 字段全部相同（一次扫描的时间戳），临时播放的文件完全不在其中，`dib` 封面字段全部为 0 |
| SMTC 暴露的 Title | ✅ 可用作**关联键**（内容是带扩展名的文件名） |
| 进程内挂接 `CreateFileW` | ✅ 主方案。PotPlayer 打开媒体文件必经此处，用 Title 做文件名匹配即可精确定位 |
| 进程内内存扫描 | ✅ 兜底方案 |
| 文件系统搜索 | ❌ 太慢，且无法覆盖任意位置 |

## 五、字段上限

"完整输出"的上限由 SMTC 规范决定，不是 PotPlayer 的限制。即使全部填满，也只能是 36 个字段——SMTC 不提供歌词、码率、音轨语言、文件路径等信息的字段。

## 六、待验证项

以下两项决定实现细节，必须实测确认后再写业务代码：

1. PotPlayer 请求 SMTC 时使用的**确切 IID**（`ISystemMediaTransportControlsInterop` 与 `ISystemMediaTransportControls`）；
2. PotPlayer 是在启动时创建一次 SMTC 对象，还是每次播放创建一次——这决定晚注入能否成功。

v0.1 的侦察模块就是为回答这两个问题而存在。
