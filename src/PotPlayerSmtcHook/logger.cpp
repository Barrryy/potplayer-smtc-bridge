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

void LogF(const char* fmt, ...) {
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
