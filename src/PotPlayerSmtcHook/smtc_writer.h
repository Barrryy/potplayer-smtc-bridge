#pragma once

// 写入 SMTC 元数据。
//
// 全部 IID 与 vtable 下标都来自实测，没有任何猜测：
//   * IID 由运行时的 IInspectable::GetIids 输出；
//   * vtable 下标由 system WinMetadata 里的 Windows.Media.winmd 导出
//     （见 docs/05-smtc-abi.md），并已被 get_DisplayUpdater / get_MusicProperties
//     的实际返回值为 S_OK 所验证。

// 由 CreateFileW 探针调用，告知当前正在播放的文件
void SmtcWriter_SetCurrentFile(const wchar_t* path);

// 必须在安装 CreateFileW 探针之前调用（否则早期上报的路径会被丢弃）
void SmtcWriter_Init();

// 重新读取 PotPlayerSmtcHook.ini（前端保存设置后调用，可不重启播放器生效）
void SmtcWriter_ReloadConfig();

// 拿到 SMTC 对象后调用：解析出各接口、挂钩 Update、并立刻写一次
void SmtcWriter_Attach(void* smtcObject);
