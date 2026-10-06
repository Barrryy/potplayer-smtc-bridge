// SmtcLoader —— 把 PotPlayerSmtcHook.dll 注入到 PotPlayer 进程。
//
// 三种模式：
//   --name <进程名>   注入到已在运行的进程（默认 PotPlayerMini64.exe）
//   --launch <路径>   以挂起方式启动目标进程，注入后再恢复线程（不会丢早期调用）
//   --watch           常驻等待，目标进程一出现就注入（覆盖从任何位置启动的情况）

#include <windows.h>
#include <tlhelp32.h>

#include <set>
#include <string>

#include "console.h"

namespace {

const wchar_t* kDefaultProcess = L"PotPlayerMini64.exe";
const wchar_t* kDefaultDll = L"PotPlayerSmtcHook.dll";

void PrintUsage() {
    PrintW(L"SmtcLoader - PotPlayer SMTC 注入器\n");
    PrintW(L"\n");
    PrintW(L"用法:\n");
    PrintW(L"  SmtcLoader.exe [--name <进程名>] [--dll <DLL路径>]\n");
    PrintW(L"  SmtcLoader.exe --launch <目标EXE> [--dll <DLL路径>]\n");
    PrintW(L"  SmtcLoader.exe --watch [--name <进程名>] [--dll <DLL路径>]\n");
    PrintW(L"\n");
    PrintW(L"示例:\n");
    PrintW(L"  SmtcLoader.exe\n");
    PrintW(L"  SmtcLoader.exe --launch \"C:\\Program Files\\DAUM\\PotPlayer\\PotPlayerMini64.exe\"\n");
    PrintW(L"  SmtcLoader.exe --watch\n");
}

std::wstring ExeDirectory() {
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::wstring s(path);
    const size_t pos = s.find_last_of(L"\\/");
    return pos == std::wstring::npos ? std::wstring() : s.substr(0, pos);
}

std::wstring ResolveDllPath(const std::wstring& given) {
    if (!given.empty()) {
        wchar_t full[MAX_PATH] = {};
        if (GetFullPathNameW(given.c_str(), MAX_PATH, full, nullptr) > 0) return full;
        return given;
    }
    return ExeDirectory() + L"\\" + kDefaultDll;
}

DWORD FindProcessId(const wchar_t* name) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;

    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    DWORD pid = 0;
    if (Process32FirstW(snap, &entry)) {
        do {
            if (lstrcmpiW(entry.szExeFile, name) == 0) {
                pid = entry.th32ProcessID;
                break;
            }
        } while (Process32NextW(snap, &entry));
    }
    CloseHandle(snap);
    return pid;
}

bool CheckArchitecture(HANDLE process) {
    BOOL targetWow64 = FALSE;
    BOOL selfWow64 = FALSE;
    IsWow64Process(process, &targetWow64);
    IsWow64Process(GetCurrentProcess(), &selfWow64);
    if (targetWow64 != selfWow64) {
        PrintW(L"[!] 位数不匹配：注入器与目标进程架构不同，注入会失败。\n");
        return false;
    }
    return true;
}

bool InjectIntoProcess(HANDLE process, const std::wstring& dllPath) {
    const SIZE_T bytes = (dllPath.size() + 1) * sizeof(wchar_t);

    void* remote =
        VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) {
        PrintW(L"[!] VirtualAllocEx 失败: %lu\n", GetLastError());
        return false;
    }

    if (!WriteProcessMemory(process, remote, dllPath.c_str(), bytes, nullptr)) {
        PrintW(L"[!] WriteProcessMemory 失败: %lu\n", GetLastError());
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        return false;
    }

    HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
    auto loadLibrary =
        reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(kernel32, "LoadLibraryW"));
    if (!loadLibrary) {
        PrintW(L"[!] 找不到 LoadLibraryW\n");
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        return false;
    }

    HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
    if (!thread) {
        PrintW(L"[!] CreateRemoteThread 失败: %lu（常见原因：权限不足或被杀软拦截）\n",
               GetLastError());
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        return false;
    }

    WaitForSingleObject(thread, 15000);
    DWORD exitCode = 0;
    GetExitCodeThread(thread, &exitCode);
    CloseHandle(thread);
    VirtualFreeEx(process, remote, 0, MEM_RELEASE);

    if (exitCode == 0) {
        PrintW(L"[!] 目标进程内 LoadLibraryW 返回失败（检查 DLL 位数与依赖）\n");
        return false;
    }
    return true;
}

bool InjectByPid(DWORD pid, const std::wstring& dllPath) {
    HANDLE process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
                                     PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
                                 FALSE, pid);
    if (!process) {
        PrintW(L"[!] OpenProcess(PID=%lu) 失败: %lu（试试以管理员身份运行）\n", pid, GetLastError());
        return false;
    }
    if (!CheckArchitecture(process)) {
        CloseHandle(process);
        return false;
    }

    PrintW(L"[*] 注入 PID %lu <- %ls\n", pid, dllPath.c_str());
    const bool ok = InjectIntoProcess(process, dllPath);
    CloseHandle(process);
    PrintW(ok ? L"[+] 注入成功\n" : L"[-] 注入失败\n");
    return ok;
}

int WatchLoop(const wchar_t* processName, const std::wstring& dllPath) {
    PrintW(L"[*] 监听 %ls 出现后自动注入，Ctrl+C 退出\n", processName);
    std::set<DWORD> injected;
    for (;;) {
        const DWORD pid = FindProcessId(processName);
        if (pid != 0 && injected.find(pid) == injected.end()) {
            if (InjectByPid(pid, dllPath)) injected.insert(pid);
        }
        Sleep(1000);
    }
}

int LaunchAndInject(const std::wstring& exePath, const std::wstring& dllPath) {
    std::wstring commandLine = L"\"" + exePath + L"\"";
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};

    // 挂起启动：保证 DLL 在目标进程执行任何代码之前就位。
    if (!CreateProcessW(exePath.c_str(), &commandLine[0], nullptr, nullptr, FALSE,
                        CREATE_SUSPENDED, nullptr, nullptr, &si, &pi)) {
        PrintW(L"[!] CreateProcessW 失败: %lu\n", GetLastError());
        return 1;
    }

    PrintW(L"[*] 已挂起启动 PID %lu，准备注入\n", pi.dwProcessId);
    const bool ok = InjectIntoProcess(pi.hProcess, dllPath);
    ResumeThread(pi.hThread);

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    PrintW(ok ? L"[+] 注入成功，进程已恢复运行\n" : L"[-] 注入失败，进程已恢复运行\n");
    return ok ? 0 : 1;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    std::wstring processName = kDefaultProcess;
    std::wstring dllArg;
    std::wstring launchPath;
    bool watch = false;

    for (int i = 1; i < argc; ++i) {
        const std::wstring arg = argv[i];
        if (arg == L"--help" || arg == L"-h") {
            PrintUsage();
            return 0;
        } else if (arg == L"--watch") {
            watch = true;
        } else if (arg == L"--name" && i + 1 < argc) {
            processName = argv[++i];
        } else if (arg == L"--dll" && i + 1 < argc) {
            dllArg = argv[++i];
        } else if (arg == L"--launch" && i + 1 < argc) {
            launchPath = argv[++i];
        } else {
            PrintW(L"[!] 未知参数: %ls\n\n", arg.c_str());
            PrintUsage();
            return 1;
        }
    }

    const std::wstring dllPath = ResolveDllPath(dllArg);
    if (GetFileAttributesW(dllPath.c_str()) == INVALID_FILE_ATTRIBUTES) {
        PrintW(L"[!] 找不到 DLL: %ls\n", dllPath.c_str());
        return 1;
    }

    if (!launchPath.empty()) return LaunchAndInject(launchPath, dllPath);
    if (watch) return WatchLoop(processName.c_str(), dllPath);

    const DWORD pid = FindProcessId(processName.c_str());
    if (pid == 0) {
        PrintW(L"[!] 没找到进程 %ls，可改用 --launch 或 --watch\n", processName.c_str());
        return 1;
    }
    return InjectByPid(pid, dllPath) ? 0 : 1;
}
