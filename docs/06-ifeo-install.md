# IFEO 启动注入

记录 v0.7 的安装机制，以及为什么其它三条路都走不通。**这些都是实测结论，不是推测。**

## 一、不能碰 PotPlayer 的文件（三条死路）

测试方法：把 `C:\Program Files\DAUM\PotPlayer` 整个目录复制到 `build/pptest`，
只在副本上做实验，用窗口枚举抓错误对话框、用模块列表看 DLL 有没有被加载。

| 实验 | 结果 |
|---|---|
| 只翻转主程序末尾一个字节（证书表内） | 正常启动 |
| 只翻转证书表 +0x40 处一个字节 | 正常启动 |
| **尾部追加 4096 个零字节** | 拒绝启动，`Cannot find or init PotPlayer64.dll` |
| 改写导入表、**保持文件长度不变** | 拒绝启动，同上 |
| 把 `PotPlayer64.dll` 换成全量转发壳（67 个导出） | `PotPlayer64.dll is modified or hacked...`，转发壳根本没被加载 |

结论：

- `PotPlayerMini64.exe` 里有 `.themida` / `.boot` 两个节 —— Themida(WinLicense) 加壳，
  它自查**文件长度**与**自身内容**，两个都满足才继续跑。
- `PotPlayerMini64.exe` 的导入表里有 `WINTRUST.dll` + `imagehlp.dll` + `CRYPT32.dll`：
  它加载核心 DLL 之前会验签名。`PotPlayer64.dll`（导出仅 `GetPlayerFunctions`）
  和 `MediaDB64.dll`（67 个导出，其中 `CreateSMTC` 就是 SMTC 实现）都带
  `CN=Kakao Corp.` 的 Authenticode 签名 —— 替身一律判为被篡改。

## 二、可用的机制：IFEO + 注入器

注册表只写一个键（`HKLM`，需要管理员一次）：

```
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PotPlayerMini64.exe
    Debugger = "<本程序目录>\PotPlayerSmtcInjector.exe"
    SmtcBridgeInjector = "<本程序目录>"      ; 卸载时只认这个标记，不动别人的设置
```

`Debugger` 的值必须带引号；系统会把被劫持目标的完整路径作为第一个参数传进来。

### 注入器的四个动作

1. **`CreateProcess(target, ..., DEBUG_ONLY_THIS_PROCESS)`**
   - 不加这个标志会递归：实测 `tests/IfeoProbe/ifeotest.c`（`-municode -static` 编译）的日志出现
     `level=0 → level=1 → level=2 → level=3`，即注入器拉起的子进程又被 IFEO 拦回注入器。
   - 加了之后日志只有 `level=0`。
2. **立刻 `DebugActiveProcessStop`**：带这个调试标志创建出来的目标由内核保持挂起
   （调试器不调用 `ContinueDebugEvent`，目标一行代码都不跑）。脱离之后目标才启动，
   而且此时 `PEB.BeingDebugged` 已经是 0 —— 用 `tests/IfeoProbe/pebprobe.c` 实测：
   `pid=46832 BeingDebugged=0 NtGlobalFlag=0x00000000`。Themida 的反调试看不到我们。
3. **等加载器就绪再注入**：轮询目标模块列表直到出现 `kernel32.dll`。
   刚创建时静态导入还没解析完，此时 `CreateRemoteThread(LoadLibraryW)` 会让远程线程
   跳进未映射地址、把 PotPlayer 直接打崩。
4. **注入完就退出**：不等待目标进程、不留任何窗口（`-mwindows` 构建、无托盘）。

### 卸载与兜底

- 界面「卸载启动注入」= 删掉上面那个键，PotPlayer 立刻恢复原样（它的文件从头到尾没被改过）。
- 手动兜底：`Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PotPlayerMini64.exe' -Recurse -Force`
- 如果注入器被挪走或删掉，IFEO 会拉起失败、PotPlayer 也起不来 —— 删键即可。
  **所以装完不要移动本程序目录。**

## 三、验证要点

- 注入器日志：`%TEMP%\potplayer-smtc-bridge\injector.log`（每次运行覆盖）
- 注入模块日志：`%TEMP%\potplayer-smtc-bridge\hook-<pid>-<时间>.log`
- 模块列表里应该只有**一份** `PotPlayerSmtcHook.dll`；
  出现两份说明主程序还留着 v0.6 的导入表补丁，先用 `--restore` 还原。
- **只看到 `inject ok` 不代表元数据写成了**：日志里还要有
  `[attach] metadata writer attached` 和 `[writer] first write done`。
  会话对象刚建好时 `get_MusicProperties` 会返回 `0x80070032`（还没就绪），
  写入器靠重试挂载，所以这两行可能比 `inject ok` 晚 1～10 秒出现。
