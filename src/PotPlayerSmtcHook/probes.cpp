#include "probes.h"

#include <windows.h>

#include <cwchar>
#include <set>
#include <string>
#include <vector>

#include "logger.h"
#include "pe_utils.h"
#include "smtc_capture.h"
#include "smtc_writer.h"

namespace {

// 只挂接这几个模块。v0.1 曾经对全部 148 个模块打补丁，
// 那既没必要也放大了风险（会碰到系统 CRT 的导入表）。
const std::vector<std::wstring> kTargetModules = {
    L"PotPlayerMini64.exe",
    L"PotPlayer64.exe",
    L"PotPlayer32.exe",
    L"PotPlayerMini.exe",
    L"PotPlayer64.dll",
    L"PotPlayer.dll",
    L"MediaDB64.dll",
    L"MediaDB.dll",
};

// ---------------------------------------------------------------------------
// WinRT 激活探针
//
// MediaDB64.dll 通过 api-ms-win-core-winrt-l1-1-0.dll 导入
// RoGetActivationFactory / RoActivateInstance 来创建 SMTC 对象。
// 这里把这两个导入项换成自己的函数，把「类名 + IID + 返回指针」原样记下来，
// 后续实现就不需要凭记忆硬写 IID。
// ---------------------------------------------------------------------------

typedef HRESULT(WINAPI* PFN_RoGetActivationFactory)(void* classId, REFIID iid, void** factory);
typedef HRESULT(WINAPI* PFN_RoActivateInstance)(void* classId, void** instance);

PFN_RoGetActivationFactory Real_RoGetActivationFactory = nullptr;
PFN_RoActivateInstance Real_RoActivateInstance = nullptr;

HRESULT WINAPI Hook_RoGetActivationFactory(void* classId, REFIID iid, void** factory) {
    LogF("--- RoGetActivationFactory ---");
    LogHString("  class", classId);
    LogGuid("  iid  ", iid);
    const HRESULT hr = Real_RoGetActivationFactory
                           ? Real_RoGetActivationFactory(classId, iid, factory)
                           : HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
    LogF("  hr=0x%08lX out=%p", static_cast<unsigned long>(hr),
         (factory && SUCCEEDED(hr)) ? *factory : nullptr);
    if (SUCCEEDED(hr) && factory && *factory) {
        SmtcCapture_OnActivation(classId, *factory);
    }
    return hr;
}

HRESULT WINAPI Hook_RoActivateInstance(void* classId, void** instance) {
    LogF("--- RoActivateInstance ---");
    LogHString("  class", classId);
    const HRESULT hr = Real_RoActivateInstance ? Real_RoActivateInstance(classId, instance)
                                               : HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
    LogF("  hr=0x%08lX out=%p", static_cast<unsigned long>(hr),
         (instance && SUCCEEDED(hr)) ? *instance : nullptr);
    return hr;
}

// ---------------------------------------------------------------------------
// CreateFileW 探针（默认关闭）
//
// 作为「当前播放文件完整路径」的来源，但它是高频调用，风险明显高于 WinRT 探针。
// 因此改成显式开关：DLL 同目录下存在 PotPlayerSmtcHook.hookfiles 才启用。
// ---------------------------------------------------------------------------

typedef HANDLE(WINAPI* PFN_CreateFileW)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD,
                                        DWORD, HANDLE);
PFN_CreateFileW Real_CreateFileW = nullptr;

const wchar_t* kMediaExts[] = {
    L".mp3", L".flac", L".wav",  L".m4a", L".aac", L".ogg",  L".opus", L".wma",
    L".ape", L".wv",   L".tta",  L".mp4", L".mkv", L".avi",  L".mov",  L".wmv",
    L".flv", L".ts",   L".m2ts", L".webm", L".mpg", L".mpeg", L".rmvb", L".rm",
};

bool IsMediaPath(LPCWSTR path) {
    if (!path) return false;
    const size_t len = wcslen(path);
    if (len < 4) return false;

    const wchar_t* dot = nullptr;
    for (const wchar_t* p = path + len - 1; p >= path; --p) {
        if (*p == L'.') {
            dot = p;
            break;
        }
        if (*p == L'\\' || *p == L'/') return false;
    }
    if (!dot) return false;

    for (const wchar_t* ext : kMediaExts) {
        if (_wcsicmp(dot, ext) == 0) return true;
    }
    return false;
}

HANDLE WINAPI Hook_CreateFileW(LPCWSTR fileName, DWORD access, DWORD share,
                               LPSECURITY_ATTRIBUTES sa, DWORD creation, DWORD flags,
                               HANDLE templateFile) {
    const HANDLE handle = Real_CreateFileW
                              ? Real_CreateFileW(fileName, access, share, sa, creation, flags,
                                                 templateFile)
                              : INVALID_HANDLE_VALUE;
    if (IsMediaPath(fileName)) {
        // 当前播放文件的路径就是元数据的来源
        SmtcWriter_SetCurrentFile(fileName);
        LogTagW("  [open]", fileName);
        LogF("         -> handle=%p err=%lu", handle, GetLastError());
    }
    return handle;
}

// ---------------------------------------------------------------------------

std::wstring OwnModuleDirectory() {
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(&OwnModuleDirectory), &self)) {
        return std::wstring();
    }
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(self, path, MAX_PATH);
    std::wstring s(path);
    const size_t pos = s.find_last_of(L"\\/");
    return pos == std::wstring::npos ? std::wstring() : s.substr(0, pos);
}

bool FileHookEnabled() {
    const std::wstring dir = OwnModuleDirectory();
    if (dir.empty()) return false;
    const std::wstring marker = dir + L"\\PotPlayerSmtcHook.hookfiles";
    return GetFileAttributesW(marker.c_str()) != INVALID_FILE_ATTRIBUTES;
}

struct WindowSearchContext {
    DWORD pid;
};

BOOL CALLBACK EnumWindowProc(HWND hwnd, LPARAM param) {
    auto* ctx = reinterpret_cast<WindowSearchContext*>(param);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ctx->pid) return TRUE;

    wchar_t cls[256] = {};
    wchar_t title[512] = {};
    GetClassNameW(hwnd, cls, 256);
    GetWindowTextW(hwnd, title, 512);

    char cls8[1024] = {};
    char title8[2048] = {};
    snprintf(cls8, sizeof(cls8) - 1, "%s", WideToUtf8(cls).c_str());
    snprintf(title8, sizeof(title8) - 1, "%s", WideToUtf8(title).c_str());
    LogF("  hwnd=%p visible=%d class=\"%s\" title=\"%s\"", hwnd, IsWindowVisible(hwnd) ? 1 : 0,
         cls8, title8);
    return TRUE;
}

long volatile g_vehBusy = 0;

LONG WINAPI ExceptionLogger(EXCEPTION_POINTERS* info) {
    if (InterlockedExchange(&g_vehBusy, 1) != 0) return EXCEPTION_CONTINUE_SEARCH;
    // MinGW 没有 __try/__except，这里只做最简单的写入。
    if (info && info->ExceptionRecord) {
        // 这几个是正常流程就会抛出的「伪异常」，不是崩溃，记下来只会淹没真正有用的信息：
        //   0x40010006 / 0x4001000A = OutputDebugString(A/W)
        //   0x406D1388             = 线程命名
        const DWORD code = info->ExceptionRecord->ExceptionCode;
        const bool benign = code == 0x40010006UL || code == 0x4001000AUL || code == 0x406D1388UL;
        if (benign) {
            InterlockedExchange(&g_vehBusy, 0);
            return EXCEPTION_CONTINUE_SEARCH;
        }
        LogF("[veh] code=0x%08lX flags=0x%08lX",
             static_cast<unsigned long>(info->ExceptionRecord->ExceptionCode),
             static_cast<unsigned long>(info->ExceptionRecord->ExceptionFlags));
        LogAddressModule("[veh] fault at", info->ExceptionRecord->ExceptionAddress);
    }
    InterlockedExchange(&g_vehBusy, 0);
    return EXCEPTION_CONTINUE_SEARCH;
}

}  // namespace

void InstallExceptionLogger() {
    // 只做记录，不改变异常流向：真正崩溃时至少能在日志里留下一行。
    AddVectoredExceptionHandler(1, &ExceptionLogger);
}

void LogProcessInfo() {
    wchar_t exePath[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    LogTagW("process:", exePath);
    LogF("pid=%lu", static_cast<unsigned long>(GetCurrentProcessId()));

    const auto modules = GetLoadedModules();
    LogF("loaded modules: %lu", static_cast<unsigned long>(modules.size()));
    for (const auto& mod : modules) {
        const bool target = [&] {
            for (const auto& t : kTargetModules) {
                if (lstrcmpiW(t.c_str(), mod.name.c_str()) == 0) return true;
            }
            return false;
        }();
        LogF("  %-30s base=%p size=0x%08lX%s", WideToUtf8(mod.name.c_str()).c_str(), mod.base,
             static_cast<unsigned long>(mod.size), target ? "  <== 挂接目标" : "");
    }

    LogF("top-level windows of this process:");
    WindowSearchContext ctx{GetCurrentProcessId()};
    EnumWindows(&EnumWindowProc, reinterpret_cast<LPARAM>(&ctx));
}

namespace {

CRITICAL_SECTION g_patchLock;
std::set<HMODULE> g_visited;  // 已确认处理过的模块，避免重复打补丁
bool g_fileHooks = false;

bool IsTargetModule(const std::wstring& name) {
    for (const auto& t : kTargetModules) {
        if (lstrcmpiW(t.c_str(), name.c_str()) == 0) return true;
    }
    return false;
}

// 给单个模块补挂全部钩子。
// 返回本次新增的挂接数；若模块的 IAT 尚未解析完，则不标记为已处理，留待下一轮。
int TryPatchModule(HMODULE module, const std::wstring& name, bool assumeResolved = false) {
    EnterCriticalSection(&g_patchLock);
    const bool visited = g_visited.find(module) != g_visited.end();
    LeaveCriticalSection(&g_patchLock);
    if (visited) return 0;

    int patched = 0;
    bool notReady = false;

    auto apply = [&](const char* funcName, void* hook, void** real) {
        switch (PatchIatInModule(module, funcName, hook, real, assumeResolved)) {
            case IatPatchResult::Patched:
                ++patched;
                break;
            case IatPatchResult::NotReady:
                notReady = true;
                break;
            case IatPatchResult::NotFound:
                break;
        }
    };

    apply("RoGetActivationFactory", reinterpret_cast<void*>(&Hook_RoGetActivationFactory),
          reinterpret_cast<void**>(&Real_RoGetActivationFactory));
    apply("RoActivateInstance", reinterpret_cast<void*>(&Hook_RoActivateInstance),
          reinterpret_cast<void**>(&Real_RoActivateInstance));
    if (g_fileHooks) {
        apply("CreateFileW", reinterpret_cast<void*>(&Hook_CreateFileW),
              reinterpret_cast<void**>(&Real_CreateFileW));
    }

    if (patched > 0) {
        LogF("[watch] %s 补挂 %d 个导入项", WideToUtf8(name.c_str()).c_str(), patched);
    }
    if (!notReady) {
        EnterCriticalSection(&g_patchLock);
        g_visited.insert(module);
        LeaveCriticalSection(&g_patchLock);
    }
    return patched;
}

// PotPlayer 是分阶段加载模块的：注入得越早，MediaDB64.dll 之类越可能还没进来。
// 这个线程负责在它们加载之后把钩子补上。
DWORD WINAPI ModuleWatcherThread(LPVOID) {
    constexpr int kFastRounds = 600;   // 前 6 秒：10ms 一次，尽量别错过激活时机
    constexpr int kSlowRounds = 3000;  // 之后：200ms 一次，约 10 分钟

    for (int round = 0; round < kFastRounds + kSlowRounds; ++round) {
        for (const auto& name : kTargetModules) {
            HMODULE module = GetModuleHandleW(name.c_str());
            if (module) TryPatchModule(module, name);
        }
        Sleep(round < kFastRounds ? 10 : 200);
    }
    LogF("[watch] 模块监视结束");
    return 0;
}

// ---------------------------------------------------------------------------
// DLL 加载通知
//
// 轮询有 10ms 的固有延迟，而实测 MediaDB64.dll 加载后不到 10ms 就把 SMTC 对象
// 建好了，因此必须在模块加载完成的第一时间补挂，不能等下一轮轮询。
// 这里用 ntdll 的 LdrRegisterDllNotification，回调在模块初始化完成、且
// LoadLibrary 尚未返回给调用方之前触发。
//
// 回调运行在加载器锁内，所以：只做内存写入（判断名字 + 改 IAT），
// 不调用任何加载器相关 API（因此传入 assumeResolved = true 跳过探测），
// 也不写日志——需要记录的内容交给监视线程。
// ---------------------------------------------------------------------------

typedef struct _BridgeUnicodeString {
    USHORT Length;
    USHORT MaximumLength;
    PWSTR Buffer;
} BridgeUnicodeString;

typedef struct _BridgeDllLoadedData {
    ULONG Flags;
    const BridgeUnicodeString* FullDllName;
    const BridgeUnicodeString* BaseDllName;
    PVOID DllBase;
    ULONG SizeOfImage;
} BridgeDllLoadedData;

typedef union _BridgeDllNotificationData {
    BridgeDllLoadedData Loaded;
    BridgeDllLoadedData Unloaded;
} BridgeDllNotificationData;

constexpr ULONG kDllNotificationLoaded = 1;

typedef VOID(WINAPI* PFN_DllNotification)(ULONG reason,
                                          const BridgeDllNotificationData* data, PVOID context);
typedef LONG(WINAPI* PFN_LdrRegisterDllNotification)(ULONG flags, PFN_DllNotification callback,
                                                     PVOID context, PVOID* cookie);

bool MatchesTargetName(const BridgeUnicodeString* name) {
    if (!name || !name->Buffer || name->Length == 0) return false;
    const size_t chars = name->Length / sizeof(wchar_t);
    for (const auto& target : kTargetModules) {
        if (target.size() == chars && _wcsnicmp(target.c_str(), name->Buffer, chars) == 0) {
            return true;
        }
    }
    return false;
}

std::wstring ToWString(const BridgeUnicodeString* name) {
    if (!name || !name->Buffer) return std::wstring();
    return std::wstring(name->Buffer, name->Length / sizeof(wchar_t));
}

VOID WINAPI OnDllNotification(ULONG reason, const BridgeDllNotificationData* data, PVOID) {
    if (reason != kDllNotificationLoaded || !data) return;
    const auto& loaded = data->Loaded;
    if (!loaded.DllBase) return;
    if (!MatchesTargetName(loaded.BaseDllName)) return;

    // 加载器锁内：只改内存，不写日志、不进加载器
    const int patched =
        TryPatchModule(static_cast<HMODULE>(loaded.DllBase), ToWString(loaded.BaseDllName), true);
    (void)patched;
}

void RegisterDllNotifications() {
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) return;
    auto registerFn = reinterpret_cast<PFN_LdrRegisterDllNotification>(
        GetProcAddress(ntdll, "LdrRegisterDllNotification"));
    if (!registerFn) {
        LogF("[watch] LdrRegisterDllNotification 不可用，退化为轮询");
        return;
    }
    PVOID cookie = nullptr;
    const LONG status = registerFn(0, &OnDllNotification, nullptr, &cookie);
    LogF("[watch] LdrRegisterDllNotification status=0x%08lX", static_cast<unsigned long>(status));
}

}  // namespace

void InstallProbes() {
    InitializeCriticalSection(&g_patchLock);

    HMODULE combase = GetModuleHandleW(L"combase.dll");
    if (!combase) combase = LoadLibraryW(L"combase.dll");
    if (!combase) {
        LogF("[!] combase.dll not available");
        return;
    }

    Real_RoGetActivationFactory = reinterpret_cast<PFN_RoGetActivationFactory>(
        GetProcAddress(combase, "RoGetActivationFactory"));
    Real_RoActivateInstance =
        reinterpret_cast<PFN_RoActivateInstance>(GetProcAddress(combase, "RoActivateInstance"));
    LogF("combase resolved: RoGetActivationFactory=%p RoActivateInstance=%p",
         reinterpret_cast<void*>(Real_RoGetActivationFactory),
         reinterpret_cast<void*>(Real_RoActivateInstance));

    // 关键一步：直接在 combase 的导出表上改跳转。
    // 之后加载的模块（MediaDB64.dll 就是其中之一）在解析导入时拿到的就是我们的地址，
    // 不再存在「补丁比模块初始化慢」的窗口。
    void* exportOriginal = nullptr;
    if (PatchExportInModule(combase, "RoGetActivationFactory",
                            reinterpret_cast<void*>(&Hook_RoGetActivationFactory),
                            &exportOriginal)) {
        LogF("[probe] combase 导出表已改写: RoGetActivationFactory 原地址=%p", exportOriginal);
        Real_RoGetActivationFactory =
            reinterpret_cast<PFN_RoGetActivationFactory>(exportOriginal);
    } else {
        LogF("[probe] combase 导出表改写失败（RoGetActivationFactory），退回 IAT 补丁");
    }
    exportOriginal = nullptr;
    if (PatchExportInModule(combase, "RoActivateInstance",
                            reinterpret_cast<void*>(&Hook_RoActivateInstance),
                            &exportOriginal)) {
        LogF("[probe] combase 导出表已改写: RoActivateInstance 原地址=%p", exportOriginal);
        Real_RoActivateInstance = reinterpret_cast<PFN_RoActivateInstance>(exportOriginal);
    } else {
        LogF("[probe] combase 导出表改写失败（RoActivateInstance），退回 IAT 补丁");
    }

    g_fileHooks = FileHookEnabled();
    if (!g_fileHooks) {
        LogF("[probe] CreateFileW 挂接未启用（在 DLL 同目录放置 PotPlayerSmtcHook.hookfiles 可开启）");
    }

    // 必须在装文件探针之前就绪，否则最早那批路径上报会被丢掉
    SmtcWriter_Init();

    // 初次尝试：此时已加载的目标模块直接挂上
    int initial = 0;
    for (const auto& mod : GetLoadedModules()) {
        if (!IsTargetModule(mod.name)) continue;
        initial += TryPatchModule(mod.base, mod.name);
    }
    LogF("[probe] 初次挂接完成，命中 %d 个导入项", initial);

    // 之后加载的模块：优先靠加载通知即时补挂，轮询作为兜底
    RegisterDllNotifications();
    HANDLE watcher = CreateThread(nullptr, 0, ModuleWatcherThread, nullptr, 0, nullptr);
    if (watcher) CloseHandle(watcher);

    // 主动拿下 interop 工厂并挂钩 —— 这样就不必和 MediaDB64 抢那 10ms
    SmtcCapture_Start(reinterpret_cast<void*>(Real_RoGetActivationFactory));
}
