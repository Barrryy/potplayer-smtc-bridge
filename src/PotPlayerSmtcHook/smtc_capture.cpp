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

HRESULT WINAPI Hook_GetForWindow(void* self, HWND hwnd, REFIID riid, void** ppv) {
    g_getForWindowSeen = true;
    LogF("--- GetForWindow ---");
    LogF("  hwnd=%p", hwnd);
    LogGuid("  riid", riid);

    const HRESULT hr = Real_GetForWindow(self, hwnd, riid, ppv);
    LogF("  hr=0x%08lX out=%p", static_cast<unsigned long>(hr),
         (ppv && SUCCEEDED(hr)) ? *ppv : nullptr);

    if (SUCCEEDED(hr) && ppv && *ppv) ProbeSmtcObject(*ppv);
    return hr;
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
        LogTagW("[capture] vtable[6] 不在 MediaControl.dll，放弃挂接以免误挂:", owner.c_str());
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

// 兜底：如果等了 8 秒都没等到 GetForWindow，就用 PotPlayer 主窗口自己去要。
// GetForWindow 的语义是「同一个窗口返回同一个会话对象」，所以只要窗口选对，
// 拿到的就是 MediaDB 正在用的那一个。
DWORD WINAPI FallbackThread(LPVOID) {
    // 等主窗口出现就够；拖太久会让自动注入后的首屏信息迟迟不更新
    Sleep(3000);
    if (g_getForWindowSeen) {
        LogF("[fallback] GetForWindow 已被调用，无需兜底");
        return 0;
    }
    if (!g_firstFactory) return 0;

    HWND main = nullptr;
    EnumWindows(&FindMainWindowProc, reinterpret_cast<LPARAM>(&main));
    if (!main) {
        LogF("[fallback] 没找到可用窗口");
        return 0;
    }

    wchar_t cls[256] = {};
    GetClassNameW(main, cls, 256);
    LogF("[fallback] 没等到 GetForWindow 调用，改用主窗口自取: hwnd=%p class=%s", main,
         WideToUtf8(cls).c_str());

    void* unknown = nullptr;
    const HRESULT hr = Real_GetForWindow(g_firstFactory, main, kIidIUnknown, &unknown);
    LogF("[fallback] GetForWindow(IID_IUnknown) hr=0x%08lX out=%p",
         static_cast<unsigned long>(hr), unknown);
    if (FAILED(hr) || !unknown) return 0;

    typedef HRESULT(WINAPI* PFN_QueryInterface)(void*, REFIID, void**);
    void* inspectable = nullptr;
    const HRESULT hr2 = reinterpret_cast<PFN_QueryInterface>(VtableOf(unknown)[0])(
        unknown, kIidIInspectable, &inspectable);
    LogF("[fallback] QI(IInspectable) hr=0x%08lX out=%p", static_cast<unsigned long>(hr2),
         inspectable);
    if (SUCCEEDED(hr2) && inspectable) {
        LogIids("[fallback/smtc]", inspectable);
        ProbeSmtcObject(unknown);
        SmtcWriter_Attach(unknown);
    }
    return 0;
}

}  // namespace

void SmtcCapture_OnActivation(void* hstringClassId, void* factory) {
    if (!factory) return;
    const wchar_t* className = HStringRaw(hstringClassId);
    if (!className) return;
    if (!wcsstr(className, L"SystemMediaTransportControls")) return;

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

    // 兜底线程：等 8 秒，如果 MediaDB 始终不调 GetForWindow，就自己来
    HANDLE fallback = CreateThread(nullptr, 0, FallbackThread, nullptr, 0, nullptr);
    if (fallback) CloseHandle(fallback);
}
