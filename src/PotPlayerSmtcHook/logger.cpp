#include "logger.h"

#include <objbase.h>

#include <cstdarg>
#include <cstdio>
#include <string>

namespace {

CRITICAL_SECTION g_cs;
HANDLE g_file = INVALID_HANDLE_VALUE;
bool g_inited = false;
std::wstring g_path;

typedef PCWSTR(WINAPI* PFN_WindowsGetStringRawBuffer)(void* hstring, UINT32* length);
PFN_WindowsGetStringRawBuffer g_getStringRawBuffer = nullptr;

void WriteBytes(const char* data, int len) {
    if (g_file == INVALID_HANDLE_VALUE || len <= 0) return;
    DWORD written = 0;
    WriteFile(g_file, data, static_cast<DWORD>(len), &written, nullptr);
}

void EnsureStringHelper() {
    if (g_getStringRawBuffer) return;
    HMODULE combase = GetModuleHandleW(L"combase.dll");
    if (!combase) combase = LoadLibraryW(L"combase.dll");
    if (combase) {
        g_getStringRawBuffer = reinterpret_cast<PFN_WindowsGetStringRawBuffer>(
            GetProcAddress(combase, "WindowsGetStringRawBuffer"));
    }
}

// 所有输出最终都汇聚到这里：拼时间戳（纯数字格式）+ 已转好的 UTF-8 正文。
void WriteLine(const char* utf8Body) {
    if (!g_inited || g_file == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME st{};
    GetLocalTime(&st);
    char line[2400] = {};
    const int len = snprintf(line, sizeof(line) - 1, "[%02u:%02u:%02u.%03u][T%05lu] %s\r\n",
                             st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                             static_cast<unsigned long>(GetCurrentThreadId()),
                             utf8Body ? utf8Body : "");
    if (len <= 0) return;

    EnterCriticalSection(&g_cs);
    WriteBytes(line, len);
    FlushFileBuffers(g_file);
    LeaveCriticalSection(&g_cs);
}

}  // namespace

std::string WideToUtf8(const wchar_t* wide) {
    if (!wide || !*wide) return std::string();
    const int need = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    if (need <= 1) return std::string();
    // need 含结尾 NUL，因此要按 need 分配、写入后再截断。
    std::string buffer(static_cast<size_t>(need), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, &buffer[0], need, nullptr, nullptr);
    buffer.resize(static_cast<size_t>(need - 1));
    return buffer;
}

void LogInit() {
    if (g_inited) return;
    InitializeCriticalSection(&g_cs);

    wchar_t tempDir[MAX_PATH] = {};
    const DWORD n = GetTempPathW(MAX_PATH, tempDir);
    std::wstring dir(tempDir, n);
    dir += L"potplayer-smtc-bridge";
    CreateDirectoryW(dir.c_str(), nullptr);

    SYSTEMTIME st{};
    GetLocalTime(&st);
    wchar_t name[160] = {};
    wsprintfW(name, L"\\hook-%u-%04u%02u%02u-%02u%02u%02u.log", GetCurrentProcessId(), st.wYear,
              st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    g_path = dir + name;

    g_file = CreateFileW(g_path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                         nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    g_inited = true;
    EnsureStringHelper();
}

void LogClose() {
    if (g_file != INVALID_HANDLE_VALUE) {
        CloseHandle(g_file);
        g_file = INVALID_HANDLE_VALUE;
    }
}

// 日志统一输出英文：按「格式串」整条映射，%s/%lu/%p 等占位符原样保留。
// 表里没有的原样输出 —— 漏翻最多保持中文，不会写坏日志。
static const char* TranslateLog(const char* fmt) {
    struct Entry { const char* zh; const char* en; };
    static const Entry kTable[] = {
        {"[capture] combase.dll 不可用", "[capture] combase.dll unavailable"},
        {"[capture] VirtualProtect 失败: %lu", "[capture] VirtualProtect failed: %lu"},
        {"[capture] vtable[6] 已经是本模块的钩子，跳过以免递归",
         "[capture] vtable[6] is already this module's hook, skipping to avoid recursion"},
        {"[capture] WindowsCreateString 失败: 0x%08lX", "[capture] WindowsCreateString failed: 0x%08lX"},
        {"[capture] 主动激活 interop 工厂: hr=0x%08lX factory=%p",
         "[capture] activating interop factory: hr=0x%08lX factory=%p"},
        {"[capture] 工厂 %p 的 vtable %p 已挂过，跳过",
         "[capture] factory %p vtable %p already hooked, skipping"},
        {"[capture] 工厂 vtable 布局：", "[capture] factory vtable layout:"},
        {"[capture] 已挂钩（第 %d 个），等待 GetForWindow 调用",
         "[capture] hooked (#%d), waiting for GetForWindow"},
        {"[capture] 新工厂 %p, vtable=%p, vtable[6]=%p",
         "[capture] new factory %p, vtable=%p, vtable[6]=%p"},
        {"[capture] 缺少 RoGetActivationFactory / WindowsCreateString",
         "[capture] missing RoGetActivationFactory / WindowsCreateString"},
        {"[eat]   在 %p 分配失败: %lu", "[eat]   allocation at %p failed: %lu"},
        {"[eat] %s 是转发器，跳过", "[eat] %s is a forwarder, skipping"},
        {"[eat] %s 的 RVA 越界", "[eat] %s: RVA out of range"},
        {"[eat] %s 跳板分配于 %p (距模块 0x%llX)", "[eat] %s trampoline at %p (0x%llX from module)"},
        {"[eat] %s: 没有导出目录", "[eat] %s: no export directory"},
        {"[eat] %s: 附近 4GB 内找不到可分配内存", "[eat] %s: no allocatable memory within 4GB"},
        {"[eat] 扫描在 %p 处停止（VirtualQuery 失败）", "[eat] scan stopped at %p (VirtualQuery failed)"},
        {"[eat] 找到 %s: ordinal=%u slotRva=0x%08lX", "[eat] found %s: ordinal=%u slotRva=0x%08lX"},
        {"[eat] 模块 %p 导出目录: names=%lu funcs=%lu",
         "[eat] module %p export directory: names=%lu funcs=%lu"},
        {"[fallback] GetForWindow 已被调用，无需兜底",
         "[fallback] GetForWindow already called, no fallback needed"},
        {"[fallback] 没找到可用窗口", "[fallback] no usable window found"},
        {"[fallback] 没等到 GetForWindow 调用，改用主窗口自取: hwnd=%p class=%s",
         "[fallback] GetForWindow never came; using the main window instead: hwnd=%p class=%s"},
        {"[probe] combase 导出表已改写: RoActivateInstance 原地址=%p",
         "[probe] combase export table patched: RoActivateInstance was %p"},
        {"[probe] combase 导出表已改写: RoGetActivationFactory 原地址=%p",
         "[probe] combase export table patched: RoGetActivationFactory was %p"},
        {"[probe] combase 导出表改写失败（RoActivateInstance），退回 IAT 补丁",
         "[probe] patching the combase export table failed (RoActivateInstance); falling back to IAT"},
        {"[probe] combase 导出表改写失败（RoGetActivationFactory），退回 IAT 补丁",
         "[probe] patching the combase export table failed (RoGetActivationFactory); falling back to IAT"},
        {"[probe] CreateFileW 挂接未启用（在 DLL 同目录放置 PotPlayerSmtcHook.hookfiles 可开启）",
         "[probe] CreateFileW hook disabled (drop PotPlayerSmtcHook.hookfiles next to the DLL to enable)"},
        {"[probe] 初次挂接完成，命中 %d 个导入项",
         "[probe] initial hooking done, %d import entries patched"},
        {"[spy] vtable[%d] 被调用！self=%p", "[spy] vtable[%d] called! self=%p"},
        {"[watch] %s 补挂 %d 个导入项", "[watch] %s: late-hooked %d import entries"},
        {"[watch] LdrRegisterDllNotification 不可用，退化为轮询",
         "[watch] LdrRegisterDllNotification unavailable, falling back to polling"},
        {"[watch] 模块监视结束", "[watch] module watch finished"},
        {"[writer] get_DisplayUpdater 失败 hr=0x%08lX",
         "[writer] get_DisplayUpdater failed hr=0x%08lX"},
        {"[writer] get_Genres 失败，跳过流派", "[writer] get_Genres failed, skipping genres"},
        {"[writer] get_MusicProperties 失败 hr=0x%08lX",
         "[writer] get_MusicProperties failed hr=0x%08lX"},
        {"[writer] 就绪：WindowsCreateString=%p PSGetPropertyKeyFromName=%p",
         "[writer] ready: WindowsCreateString=%p PSGetPropertyKeyFromName=%p"},
        {"[writer] 已挂钩 DisplayUpdater::Update (vtable[%d])",
         "[writer] hooked DisplayUpdater::Update (vtable[%d])"},
        {"[writer] 拿不到 IMusicDisplayProperties，放弃",
         "[writer] IMusicDisplayProperties unavailable, giving up"},
        {"[writer] 配置: tag_first=%d pattern=%s", "[writer] config: tag_first=%d pattern=%s"},
        {"[writer] 配置已重载: tag_first=%d pattern=%s",
         "[writer] config reloaded: tag_first=%d pattern=%s"},
        {"[writer] 首次写入完成", "[writer] first write done"},
    };
    for (const Entry& e : kTable) {
        if (strcmp(fmt, e.zh) == 0) return e.en;
    }
    return fmt;
}

void LogF(const char* fmt, ...) {
    fmt = TranslateLog(fmt);
    if (!g_inited) return;
    char body[2048] = {};
    va_list args;
    va_start(args, fmt);
    vsnprintf(body, sizeof(body) - 1, fmt, args);
    va_end(args);
    WriteLine(body);
}

void LogW(const wchar_t* wide) {
    if (!g_inited) return;
    const std::string utf8 = WideToUtf8(wide);
    WriteLine(utf8.c_str());
}

void LogTagW(const char* tag, const wchar_t* wide) {
    if (!g_inited) return;
    const std::string utf8 = WideToUtf8(wide);
    char body[2400] = {};
    snprintf(body, sizeof(body) - 1, "%s %s", tag ? tag : "", utf8.c_str());
    WriteLine(body);
}

void LogGuid(const char* tag, const GUID& guid) {
    wchar_t buf[64] = {};
    if (StringFromGUID2(guid, buf, 64) > 0) LogTagW(tag, buf);
}

void LogHString(const char* tag, void* hstring) {
    if (!hstring) {
        LogF("%s <null>", tag);
        return;
    }
    PCWSTR raw = HStringRaw(hstring);
    if (!raw) {
        LogF("%s <cannot read HSTRING>", tag);
        return;
    }
    LogTagW(tag, raw);
}

const wchar_t* HStringRaw(void* hstring) {
    if (!hstring) return nullptr;
    EnsureStringHelper();
    if (!g_getStringRawBuffer) return nullptr;
    return g_getStringRawBuffer(hstring, nullptr);
}

void LogAddressModule(const char* tag, const void* address) {
    if (!address) return;
    HMODULE module = nullptr;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(address), &module)) {
        wchar_t path[MAX_PATH] = {};
        GetModuleFileNameW(module, path, MAX_PATH);
        char body[900] = {};
        snprintf(body, sizeof(body) - 1, "%s %p in %s", tag ? tag : "", address,
                 WideToUtf8(path).c_str());
        WriteLine(body);
    } else {
        LogF("%s %p (module unknown)", tag ? tag : "", address);
    }
}

std::wstring LogFilePath() { return g_path; }
