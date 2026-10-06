// PotPlayerSmtcInjector.exe
//
// 由 IFEO(Image File Execution Options) 的 Debugger 值在每次启动 PotPlayerMini64.exe 时自动拉起：
//     HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PotPlayerMini64.exe
//         Debugger = "<本文件路径>"
// 系统会把被劫持的目标路径作为第一个参数传进来。
//
// 职责：把 PotPlayerSmtcHook.dll 注入到刚创建、还没跑起来的 PotPlayer 进程里，然后立刻退出。
//       不常驻、不留窗口、不改动 PotPlayer 的任何文件。
//
// 两个关键点（都实测验证过，见 build/proxytest/）：
//   1. 子进程必须用 DEBUG_ONLY_THIS_PROCESS 创建，否则 IFEO 会再拦一次 -> 无限递归。
//      这样创建出来的进程由内核保持挂起，我们立刻 DebugActiveProcessStop 脱离，
//      主线程第一次执行前 PEB.BeingDebugged 就已经是 0，Themida 看不到调试器。
//   2. 注入必须等目标进程的加载器起来（kernel32 已映射）再做，
//      否则远程线程会跳到一个还没映射的地址，直接把 PotPlayer 打崩。

#include <windows.h>
#include <psapi.h>
#include <shellapi.h>
#include <stdio.h>
#include <wchar.h>

#define HOOK_DLL_NAME L"PotPlayerSmtcHook.dll"
#define LOADER_WAIT_MS 20000
#define INJECT_WAIT_MS 15000

static FILE *g_log = nullptr;

static void LogOpen(void)
{
    wchar_t dir[MAX_PATH];
    DWORD n = GetTempPathW(MAX_PATH, dir);
    if (n == 0 || n > MAX_PATH - 40) return;

    wcscat(dir, L"potplayer-smtc-bridge");
    CreateDirectoryW(dir, nullptr);
    wcscat(dir, L"\\injector.log");

    // 只保留最近一次运行的内容，避免日志无限增长
    DeleteFileW(dir);
    g_log = _wfopen(dir, L"a, ccs=UTF-8");
}

static void Log(const wchar_t *fmt, ...)
{
    if (!g_log) return;
    va_list ap;
    va_start(ap, fmt);
    vfwprintf(g_log, fmt, ap);
    va_end(ap);
    fflush(g_log);
}

static bool DirOfSelf(wchar_t *out, size_t cch)
{
    if (GetModuleFileNameW(nullptr, out, (DWORD)cch) == 0) return false;
    wchar_t *slash = wcsrchr(out, L'\\');
    if (!slash) return false;
    *slash = 0;
    return true;
}

static bool DirOfFile(const wchar_t *file, wchar_t *out, size_t cch)
{
    if (wcslen(file) + 1 > cch) return false;
    wcscpy(out, file);
    wchar_t *slash = wcsrchr(out, L'\\');
    if (!slash) { out[0] = 0; return false; }
    *slash = 0;
    return true;
}

static bool Exists(const wchar_t *path)
{
    DWORD attr = GetFileAttributesW(path);
    return attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY);
}

// 按 Windows 命令行规则给单个参数加引号
static void AppendArg(wchar_t *cmd, size_t cch, const wchar_t *arg, bool first)
{
    size_t len = wcslen(cmd);
    if (!first && len + 1 < cch) { cmd[len++] = L' '; cmd[len] = 0; }

    bool needQuote = (wcschr(arg, L' ') || wcschr(arg, L'\t') || wcschr(arg, L'"') || arg[0] == 0);
    if (!needQuote) { wcsncat(cmd, arg, cch - wcslen(cmd) - 1); return; }

    size_t used = wcslen(cmd);
    if (used + 2 < cch) { cmd[used++] = L'"'; cmd[used] = 0; }

    size_t backslashes = 0;
    for (const wchar_t *p = arg; *p; ++p) {
        if (*p == L'\\') { backslashes++; continue; }
        if (*p == L'"') {
            for (size_t i = 0; i <= backslashes; i++)
                wcsncat(cmd, L"\\", cch - wcslen(cmd) - 1);
            wcsncat(cmd, L"\"", cch - wcslen(cmd) - 1);
            backslashes = 0;
            continue;
        }
        for (size_t i = 0; i < backslashes; i++)
            wcsncat(cmd, L"\\", cch - wcslen(cmd) - 1);
        backslashes = 0;
        wchar_t one[2] = { *p, 0 };
        wcsncat(cmd, one, cch - wcslen(cmd) - 1);
    }
    for (size_t i = 0; i < backslashes * 2; i++)
        wcsncat(cmd, L"\\", cch - wcslen(cmd) - 1);
    wcsncat(cmd, L"\"", cch - wcslen(cmd) - 1);
}

static bool HasModule(DWORD pid, const wchar_t *want)
{
    HANDLE proc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
    if (!proc) proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!proc) return false;

    HMODULE mods[1024];
    DWORD needed = 0;
    bool found = false;
    if (EnumProcessModulesEx(proc, mods, sizeof(mods), &needed, LIST_MODULES_ALL)) {
        DWORD count = needed / sizeof(HMODULE);
        if (count > 1024) count = 1024;
        for (DWORD i = 0; i < count; i++) {
            wchar_t name[MAX_PATH];
            if (GetModuleBaseNameW(proc, mods[i], name, MAX_PATH) && _wcsicmp(name, want) == 0) {
                found = true;
                break;
            }
        }
    }
    CloseHandle(proc);
    return found;
}

static bool WaitForLoader(DWORD pid, DWORD timeoutMs)
{
    DWORD start = GetTickCount();
    while (GetTickCount() - start < timeoutMs) {
        if (HasModule(pid, L"kernel32.dll")) return true;
        Sleep(20);
    }
    return false;
}

static bool Inject(const wchar_t *dllPath, DWORD pid)
{
    HANDLE proc = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                              PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
    if (!proc) {
        Log(L"OpenProcess 失败: %lu\n", GetLastError());
        return false;
    }

    SIZE_T bytes = (wcslen(dllPath) + 1) * sizeof(wchar_t);
    void *remote = VirtualAllocEx(proc, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) {
        Log(L"VirtualAllocEx 失败: %lu\n", GetLastError());
        CloseHandle(proc);
        return false;
    }
    if (!WriteProcessMemory(proc, remote, dllPath, bytes, nullptr)) {
        Log(L"WriteProcessMemory 失败: %lu\n", GetLastError());
        VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
        CloseHandle(proc);
        return false;
    }

    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    FARPROC loadLibrary = GetProcAddress(k32, "LoadLibraryW");
    HANDLE thread = CreateRemoteThread(proc, nullptr, 0,
                                       (LPTHREAD_START_ROUTINE)loadLibrary, remote, 0, nullptr);
    if (!thread) {
        Log(L"CreateRemoteThread 失败: %lu\n", GetLastError());
        VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
        CloseHandle(proc);
        return false;
    }

    bool ok = false;
    if (WaitForSingleObject(thread, INJECT_WAIT_MS) == WAIT_OBJECT_0) {
        DWORD code = 0;
        GetExitCodeThread(thread, &code);
        ok = (code != 0);
        Log(L"LoadLibraryW 返回 0x%08lX\n", code);
    } else {
        Log(L"等待远线程超时\n");
    }

    CloseHandle(thread);
    if (ok) VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
    CloseHandle(proc);
    return ok;
}

int WINAPI WinMain(HINSTANCE, HINSTANCE, LPSTR, int)
{
    LogOpen();

    int argc = 0;
    LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc < 2) {
        Log(L"没有目标程序参数（本程序由 IFEO 自动拉起，一般不需要手动运行）\n");
        if (argv) LocalFree(argv);
        return 0;
    }

    const wchar_t *target = argv[1];
    Log(L"目标: %ls\n", target);

    wchar_t selfDir[MAX_PATH];
    if (!DirOfSelf(selfDir, MAX_PATH)) return 1;
    wchar_t dllPath[MAX_PATH * 2];
    _snwprintf(dllPath, MAX_PATH * 2, L"%s\\%s", selfDir, HOOK_DLL_NAME);
    Log(L"注入模块: %ls\n", dllPath);

    wchar_t cmd[32768] = { 0 };
    AppendArg(cmd, 32768, target, true);
    for (int i = 2; i < argc; i++) AppendArg(cmd, 32768, argv[i], false);
    Log(L"命令行: %ls\n", cmd);

    wchar_t targetDir[MAX_PATH];
    const wchar_t *workDir = DirOfFile(target, targetDir, MAX_PATH) ? targetDir : nullptr;

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));

    // DEBUG_ONLY_THIS_PROCESS：绕开 IFEO 的二次劫持；内核会让目标进程保持挂起，
    // 我们不去 ContinueDebugEvent，因此目标在脱离之前一行代码都不会跑。
    if (!CreateProcessW(target, cmd, nullptr, nullptr, FALSE, DEBUG_ONLY_THIS_PROCESS,
                        nullptr, workDir, &si, &pi)) {
        Log(L"CreateProcess 失败: %lu（不重试，避免 IFEO 递归）\n", GetLastError());
        LocalFree(argv);
        return 1;
    }
    Log(L"已创建: pid=%lu\n", pi.dwProcessId);

    if (DebugActiveProcessStop(pi.dwProcessId)) Log(L"已脱离调试\n");
    else Log(L"DebugActiveProcessStop 失败: %lu\n", GetLastError());

    if (!Exists(dllPath)) {
        Log(L"找不到 %ls，跳过注入（PotPlayer 照常启动）\n", dllPath);
    } else if (!WaitForLoader(pi.dwProcessId, LOADER_WAIT_MS)) {
        Log(L"等待加载器超时，仍尝试注入\n");
        if (Inject(dllPath, pi.dwProcessId)) Log(L"注入成功\n");
        else Log(L"注入失败\n");
    } else {
        Log(L"加载器就绪\n");
        if (Inject(dllPath, pi.dwProcessId)) Log(L"注入成功\n");
        else Log(L"注入失败\n");
    }

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    LocalFree(argv);
    if (g_log) fclose(g_log);
    return 0;
}
