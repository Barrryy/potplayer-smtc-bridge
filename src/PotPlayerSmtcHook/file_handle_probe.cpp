#include "file_handle_probe.h"

#include <windows.h>

#include <cwchar>
#include <set>
#include <string>
#include <vector>

#include "logger.h"
#include "smtc_writer.h"

extern "C" DWORD WINAPI GetFinalPathNameByHandleW(HANDLE file, LPWSTR path, DWORD chars,
                                                   DWORD flags);

namespace {

constexpr ULONG kSystemExtendedHandleInformation = 64;
constexpr LONG kStatusInfoLengthMismatch = static_cast<LONG>(0xC0000004);
constexpr LONG kStatusBufferTooSmall = static_cast<LONG>(0xC0000023);

typedef LONG(NTAPI* PFN_NtQuerySystemInformation)(ULONG, PVOID, ULONG, PULONG);

#pragma pack(push, 8)
struct SystemHandleEntry {
    void* Object;
    ULONG_PTR UniqueProcessId;
    HANDLE HandleValue;
    ULONG GrantedAccess;
    USHORT CreatorBackTraceIndex;
    USHORT ObjectTypeIndex;
    ULONG HandleAttributes;
    ULONG Reserved;
};

struct SystemHandleInformation {
    ULONG_PTR NumberOfHandles;
    ULONG_PTR Reserved;
};
#pragma pack(pop)

static_assert(sizeof(SystemHandleEntry) == 40, "unexpected x64 handle entry layout");

PFN_NtQuerySystemInformation g_querySystemInformation = nullptr;
HMODULE g_selfModule = nullptr;
DWORD g_processId = 0;
LONG g_fileTypeIndex = -1;
std::wstring g_lastChosenPath;
std::set<std::wstring> g_previousPaths;

const wchar_t* kMediaExts[] = {
    L".mp3", L".flac", L".wav",  L".m4a", L".aac", L".ogg",  L".opus", L".wma",
    L".ape", L".wv",   L".tta",  L".mp4", L".mkv", L".avi",  L".mov",  L".wmv",
    L".flv", L".ts",   L".m2ts", L".webm", L".mpg", L".mpeg", L".rmvb", L".rm",
};

bool IsMediaPath(const std::wstring& path) {
    const size_t slash = path.find_last_of(L"\\/");
    const size_t dot = path.find_last_of(L'.');
    if (dot == std::wstring::npos || dot < slash) return false;
    for (const wchar_t* ext : kMediaExts) {
        if (_wcsicmp(path.c_str() + dot, ext) == 0) return true;
    }
    return false;
}

std::wstring Lower(std::wstring text) {
    for (auto& ch : text) ch = static_cast<wchar_t>(towlower(ch));
    return text;
}

std::wstring FileNameOf(const std::wstring& path) {
    const size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(slash + 1);
}

std::wstring StemOf(const std::wstring& path) {
    std::wstring name = FileNameOf(path);
    const size_t dot = name.find_last_of(L'.');
    if (dot != std::wstring::npos && dot > 0) name.resize(dot);
    return name;
}

std::wstring NormalizeDosPath(const std::wstring& path) {
    if (path.rfind(L"\\\\?\\UNC\\", 0) == 0) return L"\\\\" + path.substr(8);
    if (path.rfind(L"\\\\?\\", 0) == 0) return path.substr(4);
    return path;
}

bool QueryHandles(std::vector<SystemHandleEntry>* result) {
    if (!result || !g_querySystemInformation) return false;

    ULONG length = 1 << 20;
    for (int attempt = 0; attempt < 8; ++attempt) {
        std::vector<BYTE> buffer(length);
        ULONG returned = 0;
        const LONG status =
            g_querySystemInformation(kSystemExtendedHandleInformation, buffer.data(), length,
                                     &returned);
        if (status == kStatusInfoLengthMismatch || status == kStatusBufferTooSmall) {
            length = returned > length ? returned + 65536 : length * 2;
            continue;
        }
        if (status < 0) return false;

        const auto* info = reinterpret_cast<const SystemHandleInformation*>(buffer.data());
        const size_t maxEntries = (length - sizeof(SystemHandleInformation)) /
                                  sizeof(SystemHandleEntry);
        if (info->NumberOfHandles > maxEntries) return false;

        const auto* entries = reinterpret_cast<const SystemHandleEntry*>(
            buffer.data() + sizeof(SystemHandleInformation));
        result->clear();
        result->reserve(static_cast<size_t>(info->NumberOfHandles));
        for (ULONG_PTR i = 0; i < info->NumberOfHandles; ++i) {
            if (entries[i].UniqueProcessId == g_processId) result->push_back(entries[i]);
        }
        return true;
    }
    return false;
}

bool InitializeFileTypeIndex() {
    if (g_fileTypeIndex >= 0) return true;

    wchar_t modulePath[MAX_PATH] = {};
    if (!GetModuleFileNameW(g_selfModule, modulePath, MAX_PATH)) return false;

    HANDLE probe = CreateFileW(modulePath, FILE_READ_ATTRIBUTES,
                               FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                               OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (probe == INVALID_HANDLE_VALUE) return false;

    std::vector<SystemHandleEntry> handles;
    const bool ok = QueryHandles(&handles);
    if (ok) {
        for (const auto& entry : handles) {
            if (entry.HandleValue == probe) {
                const LONG index = static_cast<LONG>(entry.ObjectTypeIndex);
                InterlockedCompareExchange(&g_fileTypeIndex, index, -1);
                break;
            }
        }
    }
    CloseHandle(probe);
    return g_fileTypeIndex >= 0;
}

std::wstring PathFromHandle(HANDLE handle) {
    DWORD capacity = 512;
    for (int attempt = 0; attempt < 4; ++attempt) {
        std::vector<wchar_t> buffer(capacity);
        const DWORD length = GetFinalPathNameByHandleW(
            handle, buffer.data(), capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (length == 0) return std::wstring();
        if (length < capacity) {
            return NormalizeDosPath(std::wstring(buffer.data(), length));
        }
        capacity = length + 1;
    }
    return std::wstring();
}

BOOL CALLBACK FindPlayerWindow(HWND hwnd, LPARAM param) {
    auto* found = reinterpret_cast<HWND*>(param);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != g_processId || !IsWindowVisible(hwnd)) return TRUE;

    wchar_t cls[256] = {};
    GetClassNameW(hwnd, cls, 256);
    if (wcsstr(cls, L"PotPlayer") == nullptr) return TRUE;

    *found = hwnd;
    return FALSE;
}

std::wstring MainWindowTitle() {
    HWND window = nullptr;
    EnumWindows(&FindPlayerWindow, reinterpret_cast<LPARAM>(&window));
    if (!window) return std::wstring();

    wchar_t title[1024] = {};
    GetWindowTextW(window, title, 1024);
    return title;
}

bool TitleMatches(const std::wstring& title, const std::wstring& path) {
    if (title.empty()) return false;
    const std::wstring lowerTitle = Lower(title);
    return lowerTitle.find(Lower(FileNameOf(path))) != std::wstring::npos ||
           lowerTitle.find(Lower(StemOf(path))) != std::wstring::npos;
}

std::vector<std::wstring> MediaPathsFromHandles() {
    std::vector<SystemHandleEntry> handles;
    if (!QueryHandles(&handles)) return {};

    std::vector<std::wstring> paths;
    std::set<std::wstring> seen;
    for (const auto& entry : handles) {
        if (static_cast<LONG>(entry.ObjectTypeIndex) != g_fileTypeIndex) continue;

        HANDLE duplicated = nullptr;
        if (!DuplicateHandle(GetCurrentProcess(), entry.HandleValue, GetCurrentProcess(),
                             &duplicated, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
            continue;
        }
        const std::wstring path = PathFromHandle(duplicated);
        CloseHandle(duplicated);
        if (path.empty() || !IsMediaPath(path)) continue;
        const std::wstring key = Lower(path);
        if (seen.insert(key).second) paths.push_back(path);
    }
    return paths;
}

void ScanOnce() {
    if (!InitializeFileTypeIndex()) return;

    const std::vector<std::wstring> paths = MediaPathsFromHandles();
    std::set<std::wstring> current;
    std::vector<std::wstring> newlyOpened;
    const std::wstring title = MainWindowTitle();

    for (const auto& path : paths) {
        const std::wstring key = Lower(path);
        current.insert(key);
        if (g_previousPaths.find(key) == g_previousPaths.end()) newlyOpened.push_back(path);
    }

    std::wstring chosen;
    for (const auto& path : paths) {
        if (TitleMatches(title, path)) {
            chosen = path;
            break;
        }
    }
    if (chosen.empty() && newlyOpened.size() == 1) chosen = newlyOpened.front();
    if (chosen.empty() && !g_lastChosenPath.empty() &&
        current.find(Lower(g_lastChosenPath)) != current.end()) {
        chosen = g_lastChosenPath;
    }
    if (chosen.empty() && paths.size() == 1) chosen = paths.front();
    if (chosen.empty() && !newlyOpened.empty()) chosen = newlyOpened.front();

    if (!chosen.empty()) {
        if (Lower(chosen) != Lower(g_lastChosenPath)) {
            LogTagW("[handle] current media:", chosen.c_str());
            SmtcWriter_SetCurrentFile(chosen.c_str());
            SmtcWriter_RefreshMetadata();
        }
        g_lastChosenPath = chosen;
    }
    g_previousPaths.swap(current);
}

DWORD WINAPI ProbeThread(LPVOID) {
    for (;;) {
        ScanOnce();
        Sleep(250);
    }
    return 0;
}

}  // namespace

void FileHandleProbe_Start() {
    if (g_querySystemInformation) return;

    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) return;
    g_querySystemInformation = reinterpret_cast<PFN_NtQuerySystemInformation>(
        GetProcAddress(ntdll, "NtQuerySystemInformation"));
    if (!g_querySystemInformation) {
        LogF("[handle] NtQuerySystemInformation unavailable");
        return;
    }

    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                           GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&FileHandleProbe_Start), &g_selfModule);
    g_processId = GetCurrentProcessId();

    HANDLE thread = CreateThread(nullptr, 0, ProbeThread, nullptr, 0, nullptr);
    if (thread) CloseHandle(thread);
}
