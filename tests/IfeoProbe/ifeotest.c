/* 只用来看 IFEO(Debugger) 会不会递归：记录自己收到的参数，然后启动 argv[1]。
   用环境变量 SMTB_LEVEL 计数，超过 3 层就停手，防止真变成 fork 炸弹。 */
#include <windows.h>
#include <stdio.h>

int wmain(int argc, wchar_t **argv)
{
    wchar_t logPath[MAX_PATH];
    DWORD n = GetTempPathW(MAX_PATH, logPath);
    if (n == 0 || n > MAX_PATH - 40) return 1;
    wcscat(logPath, L"ifeotest.log");

    wchar_t levelBuf[16];
    DWORD len = GetEnvironmentVariableW(L"SMTB_LEVEL", levelBuf, 16);
    int level = (len > 0) ? _wtoi(levelBuf) : 0;

    FILE *f = _wfopen(logPath, L"a, ccs=UTF-8");
    if (f) {
        fwprintf(f, L"level=%d pid=%lu argc=%d arg1=%ls\n", level, GetCurrentProcessId(),
                 argc, (argc > 1) ? argv[1] : L"(none)");
        fclose(f);
    }

    if (argc < 2) return 0;
    if (level >= 3) return 0;

    wchar_t next[16];
    _snwprintf(next, 16, L"%d", level + 1);
    SetEnvironmentVariableW(L"SMTB_LEVEL", next);

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));
    /* DEBUG_ONLY_THIS_PROCESS：看看这样创建能不能绕过 IFEO，顺带验证脱离调试后
       PEB.BeingDebugged 会不会被清干净（PotPlayer 带 Themida，会查这个）。 */
    DWORD flags = DEBUG_ONLY_THIS_PROCESS;
    if (!CreateProcessW(argv[1], NULL, NULL, NULL, FALSE, flags, NULL, NULL, &si, &pi))
        return 2;
    Sleep(300);
    BOOL stopped = DebugActiveProcessStop(pi.dwProcessId);
    wchar_t logPath2[MAX_PATH];
    DWORD n2 = GetTempPathW(MAX_PATH, logPath2);
    if (n2) {
        wcscat(logPath2, L"ifeotest.log");
        FILE *g = _wfopen(logPath2, L"a, ccs=UTF-8");
        if (g) { fwprintf(g, L"level=%d detach=%d\n", level, (int)stopped); fclose(g); }
    }
    WaitForSingleObject(pi.hProcess, 6000);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;
}
