#include "smtc_capture.h"

#include <windows.h>
#include <objbase.h>

#include <string>

#include "logger.h"
#include "smtc_writer.h"

namespace {

// IInspectable 占 6 个槽位，接口自身第 N 个方法的下标 = 6 + N。
constexpr int kSlotGetIids = 3;              // IInspectable::GetIids
constexpr int kSlotGetForWindow = 6;         // ISystemMediaTransportControlsInterop 唯一方法
constexpr int kSlotGetDisplayUpdater = 8;    // ISystemMediaTransportControls::get_DisplayUpdater
constexpr int kSlotGetMusicProperties = 12;  // DisplayUpdater::get_MusicProperties

// 用于验证「下标 6 到底是不是 GetForWindow」：
// 把工厂 vtable 前 16 个槽连同所属模块打出来，并在相邻几个槽上装探针。
constexpr int kProbeSlots[] = {7, 8, 9};
constexpr int kProbeCount = 3;
typedef HRESULT(WINAPI* PFN_Slot4)(void* self, void* a1, void* a2, void* a3);
PFN_Slot4 g_slotProbeReal[kProbeCount] = {};
bool g_slotProbeFired[kProbeCount] = {};
std::wstring g_lastHookedVtableOwner;
bool g_getForWindowSeen = false;
void* g_firstFactory = nullptr;

// 拦下来的 SMTC 会话对象（已 AddRef）。Hook_GetForWindow 只负责把它记在这儿，
// 真正的挂载交给 AttachThread —— 宿主线程里一行都不能等。
void* g_capturedSmtc = nullptr;
// 从主窗口自己向工厂要来的会话对象（已 AddRef），作为拦不到时的兜底
void* g_mainWindowSmtc = nullptr;
bool g_noWindowLogged = false;
// MediaDB 是否激活过 SMTC。只有激活过，会话对象才存在，
// 这时拿主窗口去调 GetForWindow 才是安全的（见 AttachThread 注释）。
bool g_activationSeen = false;

// 已知的经典 COM IID，用于兜底探测
const GUID kIidIUnknown = {0x00000000, 0x0000, 0x0000,
                           {0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46}};
const GUID kIidIInspectable = {0xAF86E2E0, 0xB12D, 0x4C6A,
                               {0x9C, 0x5A, 0xD7, 0xAA, 0x65, 0x10, 0x1E, 0x90}};

std::wstring ModuleNameOf(void* address) {
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(address), &module) ||
        !module) {
        return L"(未知)";
    }
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(module, path, MAX_PATH);
    const std::wstring full(path);
    const size_t pos = full.find_last_of(L"\\/");
    return pos == std::wstring::npos ? full : full.substr(pos + 1);
}

void LogVtableLayout(void** vtable) {
    LogF("[capture] 工厂 vtable 布局：");
    for (int i = 0; i < 16; ++i) {
        LogF("    [%2d] %p  %s", i, vtable[i],
             WideToUtf8(ModuleNameOf(vtable[i]).c_str()).c_str());
    }
}

void ProbeSlot(int index, void* self, void* a1, void* a2, void* a3) {
    (void)a1;
    (void)a2;
    (void)a3;
    if (!g_slotProbeFired[index]) {
        g_slotProbeFired[index] = true;
        LogF("[spy] vtable[%d] 被调用！self=%p", kProbeSlots[index], self);
    }
}

HRESULT WINAPI SpySlot7(void* s, void* a1, void* a2, void* a3) {
    ProbeSlot(0, s, a1, a2, a3);
    return g_slotProbeReal[0](s, a1, a2, a3);
}
HRESULT WINAPI SpySlot8(void* s, void* a1, void* a2, void* a3) {
    ProbeSlot(1, s, a1, a2, a3);
    return g_slotProbeReal[1](s, a1, a2, a3);
}
HRESULT WINAPI SpySlot9(void* s, void* a1, void* a2, void* a3) {
    ProbeSlot(2, s, a1, a2, a3);
    return g_slotProbeReal[2](s, a1, a2, a3);
}

void* const kSlotProbeHooks[kProbeCount] = {
    reinterpret_cast<void*>(&SpySlot7),
    reinterpret_cast<void*>(&SpySlot8),
    reinterpret_cast<void*>(&SpySlot9),
};

// 每次 RoGetActivationFactory 都可能拿到不同的工厂实例。
// 实测两次激活的工厂指针并不相同，所以不能只挂第一个。
// 但 vtable 有可能共享，因此按 vtable 去重；若某个 vtable 已经挂过，
// 绝不能把 Real_GetForWindow 再指向自己的钩子（会无限递归）。
constexpr int kMaxHookedVtables = 16;
void** g_hookedVtables[kMaxHookedVtables] = {};
int g_hookedCount = 0;

typedef HRESULT(WINAPI* PFN_GetForWindow)(void* self, HWND hwnd, REFIID riid, void** ppv);
typedef HRESULT(WINAPI* PFN_GetIids)(void* self, ULONG* count, IID** iids);
typedef HRESULT(WINAPI* PFN_GetObject)(void* self, void** out);
typedef ULONG(WINAPI* PFN_AddRef)(void* self);
typedef ULONG(WINAPI* PFN_Release)(void* self);
typedef HRESULT(WINAPI* PFN_QueryInterface)(void* self, REFIID iid, void** out);
typedef HRESULT(WINAPI* PFN_RoGetActivationFactory)(void* classId, REFIID iid, void** factory);
typedef HRESULT(WINAPI* PFN_WindowsCreateString)(LPCWSTR source, UINT32 length, void** hstring);
typedef HRESULT(WINAPI* PFN_RoInitialize)(int initType);

// {DDB0472D-C911-4A1F-86D9-DC3D71A95F5A} ISystemMediaTransportControlsInterop
//
// 这个值的来源有两条独立证据：
//   1. 它就是该接口在 Windows SDK 里的 IID；
//   2. 在 MediaDB64.dll 中能搜到完全相同的 16 字节序列（偏移 0x3869B0），
//      说明 PotPlayer 自己就是用它去激活 SMTC 的。
const GUID kIidSystemMediaTransportControlsInterop = {
    0xDDB0472D, 0xC911, 0x4A1F, {0x86, 0xD9, 0xDC, 0x3D, 0x71, 0xA9, 0x5F, 0x5A}};

PFN_GetForWindow Real_GetForWindow = nullptr;

void** VtableOf(void* object) { return *reinterpret_cast<void***>(object); }

// 只有当地址确实落在 Windows.Media.MediaControl.dll 里，才认定 vtable[6] 是 GetForWindow。
// 这样即使布局判断有误，也不会把无关函数换成我们的钩子。
bool BelongsToMediaControl(void* address, std::wstring* moduleName) {
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(address), &module)) {
        return false;
    }
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(module, path, MAX_PATH);
    const std::wstring full(path);
    const size_t pos = full.find_last_of(L"\\/");
    const std::wstring name = pos == std::wstring::npos ? full : full.substr(pos + 1);
    if (moduleName) *moduleName = name;
    return lstrcmpiW(name.c_str(), L"Windows.Media.MediaControl.dll") == 0;
}

void LogIids(const char* tag, void* inspectable) {
    if (!inspectable) return;
    auto vtable = VtableOf(inspectable);
    auto getIids = reinterpret_cast<PFN_GetIids>(vtable[kSlotGetIids]);

    ULONG count = 0;
    IID* iids = nullptr;
    const HRESULT hr = getIids(inspectable, &count, &iids);
    LogF("%s GetIids hr=0x%08lX count=%lu", tag, static_cast<unsigned long>(hr),
         static_cast<unsigned long>(count));
    if (SUCCEEDED(hr) && iids) {
        for (ULONG i = 0; i < count; ++i) LogGuid("      iid", iids[i]);
        CoTaskMemFree(iids);
    }
}

// 捕获到 SMTC 对象后，顺着 vtable 把它下面两层对象也翻出来并列出 IID
void ProbeSmtcObject(void* smtc) {
    if (!smtc) return;
    LogIids("[smtc]", smtc);

    auto vtable = VtableOf(smtc);
    void* updater = nullptr;
    const HRESULT hr =
        reinterpret_cast<PFN_GetObject>(vtable[kSlotGetDisplayUpdater])(smtc, &updater);
    LogF("  get_DisplayUpdater hr=0x%08lX ptr=%p", static_cast<unsigned long>(hr), updater);
    if (FAILED(hr) || !updater) return;

    LogIids("[updater]", updater);

    auto updaterVtable = VtableOf(updater);
    void* music = nullptr;
    const HRESULT hr2 =
        reinterpret_cast<PFN_GetObject>(updaterVtable[kSlotGetMusicProperties])(updater, &music);
    LogF("  get_MusicProperties hr=0x%08lX ptr=%p", static_cast<unsigned long>(hr2), music);
    if (FAILED(hr2) || !music) return;

    LogIids("[musicprops]", music);
}

void StoreCapturedSmtc(void* smtc);

HRESULT WINAPI Hook_GetForWindow(void* self, HWND hwnd, REFIID riid, void** ppv) {
    g_getForWindowSeen = true;
    LogF("--- GetForWindow ---");
    LogF("  hwnd=%p", hwnd);
    LogGuid("  riid", riid);

    const HRESULT hr = Real_GetForWindow(self, hwnd, riid, ppv);
    LogF("  hr=0x%08lX out=%p", static_cast<unsigned long>(hr),
         (ppv && SUCCEEDED(hr)) ? *ppv : nullptr);

    if (SUCCEEDED(hr) && ppv && *ppv) {
        ProbeSmtcObject(*ppv);
        // 只记下来，不在这里挂载：此刻 DisplayUpdater 往往还没就绪
        // （实测进程起来约 1 秒时 get_MusicProperties 返回 0x80070032），
        // 而这是宿主线程，绝不能 Sleep 等它。重试交给 AttachThread。
        StoreCapturedSmtc(*ppv);
    }
    return hr;
}

// 记下拦到的会话对象。只留第一个：后面几次 GetForWindow 返回的通常是同一个会话。
void StoreCapturedSmtc(void* smtc) {
    if (!smtc) return;
    auto* vtable = VtableOf(smtc);
    reinterpret_cast<PFN_AddRef>(vtable[1])(smtc);
    if (InterlockedCompareExchangePointer(&g_capturedSmtc, smtc, nullptr) != nullptr) {
        // 已经有人先存了，把多持的这一份引用还回去
        reinterpret_cast<PFN_Release>(vtable[2])(smtc);
    }
}

// 在工厂的 GetForWindow 上挂钩。两条路径（主动激活 / 被动捕获）共用。
bool AttachFactoryHook(void* factory) {
    if (!factory) return false;

    auto vtable = VtableOf(factory);
    void* slot = vtable[kSlotGetForWindow];

    // 先持有引用，避免工厂被提前释放
    reinterpret_cast<PFN_AddRef>(vtable[1])(factory);

    for (int i = 0; i < g_hookedCount; ++i) {
        if (g_hookedVtables[i] == vtable) {
            LogF("[capture] 工厂 %p 的 vtable %p 已挂过，跳过", factory, vtable);
            return true;
        }
    }
    if (slot == reinterpret_cast<void*>(&Hook_GetForWindow)) {
        LogF("[capture] vtable[6] 已经是本模块的钩子，跳过以免递归");
        return true;
    }

    LogF("[capture] 新工厂 %p, vtable=%p, vtable[6]=%p", factory, vtable, slot);

    std::wstring owner;
    if (!BelongsToMediaControl(slot, &owner)) {
        LogTagW("[capture] vtable[6] is not in MediaControl.dll, skipping to avoid a wrong hook:",
                owner.c_str());
        return false;
    }

    DWORD oldProtect = 0;
    if (!VirtualProtect(&vtable[kSlotGetForWindow], sizeof(void*), PAGE_READWRITE, &oldProtect)) {
        LogF("[capture] VirtualProtect 失败: %lu", GetLastError());
        return false;
    }
    Real_GetForWindow = reinterpret_cast<PFN_GetForWindow>(vtable[kSlotGetForWindow]);
    vtable[kSlotGetForWindow] = reinterpret_cast<void*>(&Hook_GetForWindow);
    VirtualProtect(&vtable[kSlotGetForWindow], sizeof(void*), oldProtect, &oldProtect);

    // 顺带在相邻槽位装探针，用来确认「下标 6 到底是不是 GetForWindow」
    LogVtableLayout(vtable);
    // 不再在相邻槽位装探针：那些槽位的真实函数签名未知，用通用四参签名转发
    // 一旦参数更多就会破坏调用约定，直接把宿主搞崩。

    if (!g_firstFactory) g_firstFactory = factory;

    if (g_hookedCount < kMaxHookedVtables) {
        g_hookedVtables[g_hookedCount++] = vtable;
    }
    LogF("[capture] 已挂钩（第 %d 个），等待 GetForWindow 调用", g_hookedCount);
    return true;
}

BOOL CALLBACK FindMainWindowProc(HWND hwnd, LPARAM param) {
    auto* best = reinterpret_cast<HWND*>(param);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != GetCurrentProcessId()) return TRUE;
    if (!IsWindowVisible(hwnd)) return TRUE;
    // 有 owner 的多半是辅助/浮动窗口，拿它去要会话容易出问题
    if (GetWindow(hwnd, GW_OWNER) != nullptr) return TRUE;

    wchar_t cls[256] = {};
    GetClassNameW(hwnd, cls, 256);

    // 只看真正的应用主窗口。
    // #32770 是对话框、tooltips_class32 / IME 之类是辅助窗口——
    // 拿它们的句柄去调 GetForWindow 会让 MediaControl 内部访问违例（实测崩过）。
    static const wchar_t* kSkipClasses[] = {L"#32770", L"tooltips_class32", L"IME",
                                            L"MSCTFIME UI", L"ComboLBox", L"PotShadowWnd"};
    for (const wchar_t* skip : kSkipClasses) {
        if (lstrcmpiW(cls, skip) == 0) return TRUE;
    }

    if (wcsstr(cls, L"PotPlayer") != nullptr) {
        *best = hwnd;
        return FALSE;  // 找到最像主窗口的那个，停止枚举
    }
    return TRUE;
}

// 兜底：MediaDB 迟迟不调用 GetForWindow（或者钩子没赶上）时，
// 用 PotPlayer 主窗口自己去要会话对象。GetForWindow 的语义是
// 「同一个窗口返回同一个会话对象」，所以只要窗口选对，拿到的就是 MediaDB 在用的那一个。
void* SmtcFromMainWindow() {
    if (g_mainWindowSmtc) return g_mainWindowSmtc;
    if (!g_firstFactory) return nullptr;

    HWND main = nullptr;
    EnumWindows(&FindMainWindowProc, reinterpret_cast<LPARAM>(&main));
    if (!main) {
        if (!g_noWindowLogged) {
            LogF("[fallback] no usable window yet (still looking)");
            g_noWindowLogged = true;
        }
        return nullptr;
    }

    wchar_t cls[256] = {};
    GetClassNameW(main, cls, 256);
    LogF("[fallback] asking the main window directly: hwnd=%p class=%s", main,
         WideToUtf8(cls).c_str());

    void* unknown = nullptr;
    const HRESULT hr = Real_GetForWindow(g_firstFactory, main, kIidIUnknown, &unknown);
    LogF("[fallback] GetForWindow(IID_IUnknown) hr=0x%08lX out=%p",
         static_cast<unsigned long>(hr), unknown);
    if (FAILED(hr) || !unknown) return nullptr;

    void* inspectable = nullptr;
    const HRESULT hr2 = reinterpret_cast<PFN_QueryInterface>(VtableOf(unknown)[0])(
        unknown, kIidIInspectable, &inspectable);
    LogF("[fallback] QI(IInspectable) hr=0x%08lX out=%p", static_cast<unsigned long>(hr2),
         inspectable);
    if (FAILED(hr2) || !inspectable) return nullptr;

    LogIids("[fallback/smtc]", inspectable);
    ProbeSmtcObject(unknown);
    g_mainWindowSmtc = unknown;
    return unknown;
}

// 挂载元数据写入器。
//
// 第一个关键点：对象「刚建好」的时候 DisplayUpdater 还没就绪 —— 实测进程起来约 1 秒时
// get_MusicProperties 返回 0x80070032(E_NOT_SUPPORTED)，同一个对象等到 3 秒再问就是
// S_OK。早注入（IFEO 的常规结果）永远撞上这个未就绪窗口，所以这里必须重试。
//
// 第二个关键点：主窗口兜底只能走一次，而且必须先确认会话对象已经存在
// （MediaDB 激活过 SMTC）。实测：会话还不存在时拿窗口去调 GetForWindow 会让
// MediaControl 内部访问违例（0xC0000005），反复重试就是反复崩宿主进程。
//
// 本线程是注入时自己开的，Sleep 安全；宿主线程里一律不等待。
DWORD WINAPI AttachThread(LPVOID) {
    constexpr int kRetryIntervalMs = 500;
    constexpr int kRetryCount = 120;               // 最多 60 秒
    constexpr int kMainWindowFallbackDelayMs = 10000;

    bool triedMainWindow = false;
    for (int attempt = 0; attempt < kRetryCount; ++attempt) {
        void* smtc = InterlockedCompareExchangePointer(&g_capturedSmtc, nullptr, nullptr);
        bool fromMainWindow = false;

        if (!smtc && !triedMainWindow && attempt * kRetryIntervalMs >= kMainWindowFallbackDelayMs) {
            triedMainWindow = true;   // 只试一次
            if (g_activationSeen && !g_getForWindowSeen) {
                smtc = SmtcFromMainWindow();
                fromMainWindow = true;
            } else if (!g_activationSeen) {
                LogF("[attach] no SMTC activation seen yet - not probing the main window");
            }
        }

        if (smtc && SmtcWriter_Attach(smtc)) {
            LogF("[attach] metadata writer attached (attempt %d, %s)", attempt + 1,
                 fromMainWindow ? "main window" : "captured session");
            return 0;
        }
        Sleep(kRetryIntervalMs);
    }

    LogF("[attach] gave up: no SMTC session object within 60 s (activation seen=%d)",
         g_activationSeen ? 1 : 0);
    return 0;
}

}  // namespace

void SmtcCapture_OnActivation(void* hstringClassId, void* factory) {
    if (!factory) return;
    const wchar_t* className = HStringRaw(hstringClassId);
    if (!className) return;
    if (!wcsstr(className, L"SystemMediaTransportControls")) return;

    g_activationSeen = true;
    LogTagW("[capture] 命中 SMTC 激活:", className);
    AttachFactoryHook(factory);
}

void SmtcCapture_Start(void* roGetActivationFactoryAddress) {
    HMODULE combase = GetModuleHandleW(L"combase.dll");
    if (!combase) combase = LoadLibraryW(L"combase.dll");
    if (!combase) {
        LogF("[capture] combase.dll 不可用");
        return;
    }

    auto roGetActivationFactory =
        reinterpret_cast<PFN_RoGetActivationFactory>(roGetActivationFactoryAddress);
    auto windowsCreateString = reinterpret_cast<PFN_WindowsCreateString>(
        GetProcAddress(combase, "WindowsCreateString"));
    if (!roGetActivationFactory || !windowsCreateString) {
        LogF("[capture] 缺少 RoGetActivationFactory / WindowsCreateString");
        return;
    }

    // 当前线程是注入时才创建的，做一次初始化避免激活被拒
    auto roInitialize =
        reinterpret_cast<PFN_RoInitialize>(GetProcAddress(combase, "RoInitialize"));
    if (roInitialize) roInitialize(1 /* RO_INIT_MULTITHREADED */);

    const wchar_t* className = L"Windows.Media.SystemMediaTransportControls";
    void* classId = nullptr;
    const HRESULT createHr =
        windowsCreateString(className, static_cast<UINT32>(wcslen(className)), &classId);
    if (FAILED(createHr) || !classId) {
        LogF("[capture] WindowsCreateString 失败: 0x%08lX", static_cast<unsigned long>(createHr));
        return;
    }

    void* factory = nullptr;
    const HRESULT hr =
        roGetActivationFactory(classId, kIidSystemMediaTransportControlsInterop, &factory);
    LogF("[capture] 主动激活 interop 工厂: hr=0x%08lX factory=%p",
         static_cast<unsigned long>(hr), factory);
    if (FAILED(hr) || !factory) return;

    AttachFactoryHook(factory);

    // 挂载线程：拿到会话对象后用重试的方式挂上元数据写入器。
    // 不能只在 GetForWindow 里直接挂 —— 那时对象还没就绪，挂不上就永远没元数据了。
    HANDLE attach = CreateThread(nullptr, 0, AttachThread, nullptr, 0, nullptr);
    if (attach) CloseHandle(attach);
}
