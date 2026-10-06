# SMTC 字段清单与数据来源

SMTC 对外暴露的字段共 **36 个**，分四类。下表同时记录 PotPlayer 的填充现状与补齐方案。

## A. 媒体属性（10 个）

| 字段 | PotPlayer 现状 | 补齐来源 |
| :--- | :--- | :--- |
| `Title` | ✅ 但内容是**带扩展名的文件名** | 标签 Title，缺失时用文件名去扩展名 |
| `Subtitle` | ❌ 空 | 标签 Subtitle（多为版本/副标题信息） |
| `Artist` | ❌ 空 | 标签 Artist |
| `AlbumArtist` | ❌ 空 | 标签 AlbumArtist（与 Artist 是两个独立字段） |
| `AlbumTitle` | ❌ 空 | 标签 Album |
| `TrackNumber` | ❌ 0 | 标签 Track（SMTC 只接受整数） |
| `AlbumTrackCount` | ❌ 0 | 标签 TrackCount |
| `Genres` | ❌ 空 | 标签 Genre |
| `Thumbnail` | ✅ 但是未压缩 BMP（常达 1.5 MB） | 内嵌封面转 JPEG，建议 500×500 |
| `PlaybackType` | ✅ 但放视频也报 Music | 按扩展名区分 Music / Video |

## B. 会话级（5 个）

| 字段 | PotPlayer 现状 | 处理 |
| :--- | :--- | :--- |
| `PlaybackStatus` | ✅ | 不动 |
| `PlaybackRate` | ✅ | 不动 |
| `AutoRepeatMode` | ✅ | 不动 |
| `IsShuffleActive` | ✅ | 不动 |
| `Controls`（15 个 `Is*Enabled`） | ✅ 且随播放状态正确翻转 | 不动 |

15 个 Controls 明细：`IsPlayEnabled`、`IsPauseEnabled`、`IsPlayPauseToggleEnabled`、`IsStopEnabled`、`IsRecordEnabled`、`IsFastForwardEnabled`、`IsRewindEnabled`、`IsPreviousEnabled`、`IsNextEnabled`、`IsChannelUpEnabled`、`IsChannelDownEnabled`、`IsPlaybackPositionEnabled`、`IsPlaybackRateEnabled`、`IsShuffleEnabled`、`IsRepeatEnabled`。

## C. 时间轴（6 个）

| 字段 | PotPlayer 现状 | 处理 |
| :--- | :--- | :--- |
| `StartTime` | ✅ | 不动 |
| `EndTime` | ✅ | 不动 |
| `MinSeekTime` | ✅ | 不动 |
| `MaxSeekTime` | ✅ | 不动 |
| `Position` | ✅ | 不动 |
| `LastUpdatedTime` | ✅ | 不动 |

## D. 不在 SMTC 里的信息

SMTC 规范不提供以下内容，任何实现都无法通过它传递：

- 歌词文本
- 码率、采样率、位深
- 文件完整路径
- 音轨语言、字幕信息
- 播放列表内容

## 关于 `Genres` 的兼容性说明

部分应用（如歌词工具 BetterLyrics）约定在 `Genres` 数组中携带私有标记：

```
NCM-<歌曲ID>       网易云音乐家族
QQ-<歌曲ID>        QQ 音乐家族
FILENAME-<文件名>   文件名（不含扩展名）
```

本项目**默认不写入**这类私有标记：既然目标是"服务所有 SMTC 消费者"，把私有标记塞进公共的流派字段会让其他消费者把 `FILENAME-xxx` 当作流派显示。

如有需要，可通过配置项开启。
