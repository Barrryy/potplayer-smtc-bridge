#include "console.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <string>

// 控制台输出统一英文：按「格式串」整条映射，%ls/%lu 等占位符原样保留。
static const wchar_t* TranslatePrint(const wchar_t* fmt) {
    struct Entry { const wchar_t* zh; const wchar_t* en; };
    static const Entry kTable[] = {
        {L"SmtcLoader - PotPlayer SMTC 注入器\n", L"SmtcLoader - PotPlayer SMTC injector\n"},
        {L"用法:\n", L"Usage:\n"},
        {L"  SmtcLoader.exe [--name <进程名>] [--dll <DLL路径>]\n",
         L"  SmtcLoader.exe [--name <process>] [--dll <dll path>]\n"},
        {L"  SmtcLoader.exe --launch <目标EXE> [--dll <DLL路径>]\n",
         L"  SmtcLoader.exe --launch <target exe> [--dll <dll path>]\n"},
        {L"  SmtcLoader.exe --watch [--name <进程名>] [--dll <DLL路径>]\n",
         L"  SmtcLoader.exe --watch [--name <process>] [--dll <dll path>]\n"},
        {L"示例:\n", L"Examples:\n"},
        {L"[!] 位数不匹配：注入器与目标进程架构不同，注入会失败。\n",
         L"[!] Bitness mismatch: injector and target differ, injection will fail.\n"},
        {L"[!] VirtualAllocEx 失败: %lu\n", L"[!] VirtualAllocEx failed: %lu\n"},
        {L"[!] WriteProcessMemory 失败: %lu\n", L"[!] WriteProcessMemory failed: %lu\n"},
        {L"[!] 找不到 LoadLibraryW\n", L"[!] LoadLibraryW not found\n"},
        {L"[!] CreateRemoteThread 失败: %lu（常见原因：权限不足或被杀软拦截）\n",
         L"[!] CreateRemoteThread failed: %lu (usually insufficient rights or blocked by AV)\n"},
        {L"[!] 目标进程内 LoadLibraryW 返回失败（检查 DLL 位数与依赖）\n",
         L"[!] LoadLibraryW failed inside the target (check DLL bitness and dependencies)\n"},
        {L"[!] OpenProcess(PID=%lu) 失败: %lu（试试以管理员身份运行）\n",
         L"[!] OpenProcess(PID=%lu) failed: %lu (try running as administrator)\n"},
        {L"[*] 注入 PID %lu <- %ls\n", L"[*] injecting into PID %lu <- %ls\n"},
        {L"[+] 注入成功\n", L"[+] injection ok\n"},
        {L"[-] 注入失败\n", L"[-] injection failed\n"},
        {L"[*] 监听 %ls 出现后自动注入，Ctrl+C 退出\n",
         L"[*] watching for %ls, injecting when it appears; Ctrl+C to quit\n"},
        {L"[!] CreateProcessW 失败: %lu\n", L"[!] CreateProcessW failed: %lu\n"},
        {L"[*] 已挂起启动 PID %lu，准备注入\n", L"[*] started suspended, PID %lu, injecting\n"},
        {L"[+] 注入成功，进程已恢复运行\n", L"[+] injection ok, process resumed\n"},
        {L"[-] 注入失败，进程已恢复运行\n", L"[-] injection failed, process resumed\n"},
        {L"[!] 未知参数: %ls\n\n", L"[!] unknown argument: %ls\n\n"},
        {L"[!] 找不到 DLL: %ls\n", L"[!] DLL not found: %ls\n"},
        {L"[!] 没找到进程 %ls，可改用 --launch 或 --watch\n",
         L"[!] process %ls not found; use --launch or --watch instead\n"},
    };
    for (const Entry& e : kTable) {
        if (wcscmp(fmt, e.zh) == 0) return e.en;
    }
    return fmt;
}

void PrintW(const wchar_t* fmt, ...) {
    fmt = TranslatePrint(fmt);
    wchar_t buf[4096] = {};
    va_list args;
    va_start(args, fmt);
    vswprintf(buf, 4095, fmt, args);
    va_end(args);

    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    if (out == INVALID_HANDLE_VALUE || out == nullptr) return;

    DWORD mode = 0;
    if (GetConsoleMode(out, &mode)) {
        DWORD written = 0;
        WriteConsoleW(out, buf, static_cast<DWORD>(wcslen(buf)), &written, nullptr);
        return;
    }

    const int need = WideCharToMultiByte(CP_UTF8, 0, buf, -1, nullptr, 0, nullptr, nullptr);
    if (need <= 1) return;
    std::string text(static_cast<size_t>(need), '\0');
    WideCharToMultiByte(CP_UTF8, 0, buf, -1, &text[0], need, nullptr, nullptr);
    text.resize(static_cast<size_t>(need - 1));
    DWORD written = 0;
    WriteFile(out, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);
}
