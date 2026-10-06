/* 报告自己的 PEB 状态：BeingDebugged / NtGlobalFlag。
   用来确认「以调试方式创建 + 脱离」之后，目标进程干不干净。
   mingw 的 winternl.h 里 PEB 字段不全，直接按 x64 偏移读。 */
#include <windows.h>
#include <intrin.h>
#include <stdio.h>

int wmain(int argc, wchar_t **argv)
{
    (void)argc;
    (void)argv;

    unsigned char *peb = (unsigned char *)__readgsqword(0x60);
    int beingDebugged = peb ? (int)peb[0x02] : -1;               /* PEB.BeingDebugged  */
    unsigned long ntGlobalFlag = peb ? *(unsigned long *)(peb + 0xBC) : 0; /* PEB.NtGlobalFlag */

    wchar_t path[MAX_PATH];
    DWORD n = GetTempPathW(MAX_PATH, path);
    if (n) {
        wcscat(path, L"pebprobe.log");
        FILE *f = _wfopen(path, L"a, ccs=UTF-8");
        if (f) {
            fwprintf(f, L"pid=%lu BeingDebugged=%d NtGlobalFlag=0x%08lX\n",
                     GetCurrentProcessId(), beingDebugged, ntGlobalFlag);
            fclose(f);
        }
    }
    return 0;
}
