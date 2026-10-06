# SMTC 的 ABI：接口布局与 vtable 下标

本文所有布局**不是凭记忆写的**，而是用 `tools/winmd-dump` 从系统自带的
`C:\Windows\System32\WinMetadata\Windows.Media.winmd` 里读出来的权威定义。

复现：

```powershell
dotnet run tools/winmd-dump/Program.cs
```

## vtable 下标怎么算

WinRT 接口都直接或间接派生自 `IInspectable`，后者占 6 个槽位：

| 槽 | 方法 |
| :--- | :--- |
| 0 | `QueryInterface` |
| 1 | `AddRef` |
| 2 | `Release` |
| 3 | `GetIids` |
| 4 | `GetRuntimeClassName` |
| 5 | `GetTrustLevel` |

因此**接口自身第 N 个方法的下标 = 6 + N**（N 从 0 开始）。

## 各接口的实际布局与下标

### `ISystemMediaTransportControls`

| 自身序号 | 方法 | **vtable 下标** |
| :---: | :--- | :---: |
| 0 | `get_PlaybackStatus` | 6 |
| 1 | `put_PlaybackStatus` | 7 |
| 2 | `get_DisplayUpdater` | **8** |
| 3 | `get_SoundLevel` | 9 |
| 4~25 | 各 `Is*Enabled` / 事件订阅 | 10~31 |

### `ISystemMediaTransportControlsDisplayUpdater`

| 自身序号 | 方法 | **vtable 下标** |
| :---: | :--- | :---: |
| 0/1 | `get_Type` / `put_Type` | 6 / **7** |
| 2/3 | `get_AppMediaId` / `put_AppMediaId` | 8 / 9 |
| 4/5 | `get_Thumbnail` / `put_Thumbnail` | 10 / **11** |
| 6 | `get_MusicProperties` | **12** |
| 7 | `get_VideoProperties` | 13 |
| 8 | `get_ImageProperties` | 14 |
| 9 | `CopyFromFileAsync` | 15 |
| 10 | `ClearAll` | 16 |
| 11 | `Update` | **17** |

### `IMusicDisplayProperties`（基础接口）

| 自身序号 | 方法 | **vtable 下标** |
| :---: | :--- | :---: |
| 0/1 | `get_Title` / `put_Title` | 6 / **7** |
| 2/3 | `get_AlbumArtist` / `put_AlbumArtist` | 8 / **9** |
| 4/5 | `get_Artist` / `put_Artist` | 10 / **11** |

### `IMusicDisplayProperties2`

| 自身序号 | 方法 | **vtable 下标** |
| :---: | :--- | :---: |
| 0/1 | `get_AlbumTitle` / `put_AlbumTitle` | 6 / **7** |
| 2/3 | `get_TrackNumber` / `put_TrackNumber` | 8 / **9** |
| 4 | `get_Genres` | 10 |

### `IMusicDisplayProperties3`

| 自身序号 | 方法 | **vtable 下标** |
| :---: | :--- | :---: |
| 0/1 | `get_AlbumTrackCount` / `put_AlbumTrackCount` | 6 / **7** |

### `ISystemMediaTransportControls2`

| 自身序号 | 方法 | **vtable 下标** |
| :---: | :--- | :---: |
| 0/1 | `get_AutoRepeatMode` / `put_AutoRepeatMode` | 6 / 7 |
| 2/3 | `get_ShuffleEnabled` / `put_ShuffleEnabled` | 8 / 9 |
| 4/5 | `get_PlaybackRate` / `put_PlaybackRate` | 10 / 11 |
| 6 | `UpdateTimelineProperties` | **12** |

## 三个必须知道的坑

### 1. 五个字段分散在三个接口里

`IMusicDisplayProperties` 只提供 Title / AlbumArtist / Artist。
AlbumTitle / TrackNumber / Genres 在 **`IMusicDisplayProperties2`**，
AlbumTrackCount 在 **`IMusicDisplayProperties3`**。

它们**不是同一个 vtable**，必须先 `QueryInterface` 到对应接口才能调用。
好在 `IInspectable::GetIids()`（vtable 下标 3）可以把对象实现的全部 IID 列出来，
运行时即可拿到，不需要预先硬编码。

### 2. `Genres` 没有 setter

metadata 里 `IMusicDisplayProperties2` 只有 `get_Genres`，**没有 `put_Genres`**。
因为它在 WinRT 层的类型是 `IVector<String>`（只读属性 + 可变集合）。

要写入流派，得先取到这个 `IVector<HSTRING>`，再调用它的 `Append`——
而 `IVector<T>` 是**参数化类型**，它的 IID 由类型签名经 PIID 算法推导，
不能从 winmd 直接读出来，需要用 `GetIids()` 在运行时确认。

这是六个字段里实现成本最高的一个，优先级可以放最后。

### 3. 读取方向是另一套接口

上面这些是**写入**用的（`Windows.Media.SystemMediaTransportControls`，进程内）。
读取用的 `GlobalSystemMediaTransportControlsSession*` 是另一组接口，
两者的方法顺序**完全不同**，不要混用。

例如读取侧的媒体属性顺序是 Title / Subtitle / AlbumArtist / Artist / AlbumTitle /
TrackNumber / Genres / AlbumTrackCount / PlaybackType / Thumbnail，
和写入侧的分组方式毫无关系。

## 已确认的 IID

### `ISystemMediaTransportControlsInterop`

```
{DDB0472D-C911-4A1F-86D9-DC3D71A95F5A}
```

两条独立证据：

1. 它是该接口在 Windows SDK 中声明的 IID；
2. 在 `MediaDB64.dll` 中搜到了完全相同的 16 字节序列（文件偏移 `0x3869B0`），
   说明 PotPlayer 自己正是用它去激活 SMTC。

**实测验证**：本项目的注入模块调用
`RoGetActivationFactory("Windows.Media.SystemMediaTransportControls", 上述 IID)`，
返回 `S_OK`；若 IID 有误会返回 `E_NOINTERFACE`（0x80004002）。

### 其余接口 IID（实测得到）

下表全部来自运行时的 `IInspectable::GetIids` 输出，**没有任何猜测**：

| 接口 | IID |
| :--- | :--- |
| `ISystemMediaTransportControls` | `{99FA3FF4-1742-42A6-902E-087D41F965EC}` |
| `ISystemMediaTransportControls2` | `{EA98D2F6-7F3C-4AF2-A586-72889808EFB1}` |
| `ISystemMediaTransportControlsDisplayUpdater` | `{8ABBC53E-FA55-4ECF-AD8E-C984E5DD1550}` |
| `IMusicDisplayProperties` | `{6BBF0C59-D0A0-4D26-92A0-F978E1D18E7B}` |
| `IMusicDisplayProperties2` | `{00368462-97D3-44B9-B00F-008AFCEFAF18}` |
| `IMusicDisplayProperties3` | `{4DB51AC1-0681-4E8C-9401-B8159D9EEFC7}` |

（同批输出里还有 `{00000038-…-C000-000000000046}` = `IAgileObject` 和
`{00000040-…-C000-000000000046}` = `IMarshal`，是通用接口。）

获取方式：

1. 用上面的 IID 拿到 interop 工厂；
2. 在该工厂 vtable 下标 6（`GetForWindow`）挂钩；
3. 拿到 SMTC 对象后调用 `IInspectable::GetIids`（下标 3），
   它会返回该对象实现的**全部 IID**。

### vtable 下标的实测校验

文中那些下标并非只有 winmd 推导，运行时也验证过：

```
get_DisplayUpdater  hr=0x00000000 ptr=...     ← 下标 8，成功
get_MusicProperties hr=0x00000000 ptr=...     ← 下标 12，成功
```

两个调用都返回 `S_OK` 并给出有效指针，说明下标 8 / 12 正确。

### vtable[6] 的实测校验

`GetForWindow` 的地址必然落在 `Windows.Media.MediaControl.dll` 的映像范围内。
注入模块在挂钩前会检查这一点，实测通过：

```
[capture] 工厂 vtable[6] -> 00007ffa4719c7b0 in C:\Windows\System32\Windows.Media.MediaControl.dll
```

## 为什么不靠"等 PotPlayer 自己激活"

实测 `MediaDB64.dll` 从加载到创建 SMTC 对象不足 10ms，
任何轮询式补挂都会慢一步。而 WinRT 的激活工厂在进程内是**缓存的**，
因此改成由注入模块**主动激活并挂钩**，MediaDB 随后的调用必然经过我们。
