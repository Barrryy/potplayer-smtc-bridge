#include "pe_utils.h"

#include <tlhelp32.h>

#include <cstring>

#include "logger.h"

namespace {

std::vector<void*> g_stubs;  // 已分配的跳板地址，LooksResolved 需要认得它们

bool NameMatches(const std::vector<std::wstring>& list, const std::wstring& name) {
    if (list.empty()) return true;
    for (const auto& item : list) {
        if (lstrcmpiW(item.c_str(), name.c_str()) == 0) return true;
    }
    return false;
}

// IAT 里的值应当指向某个已加载模块。若还不是（例如模块刚被映射、
// 加载器尚未完成导入解析），说明现在打补丁会被随后的解析覆盖掉，必须等下一轮。
bool LooksResolved(void* address) {
    if (!address) return false;
    for (void* stub : g_stubs) {
        if (address == stub) return true;
    }
    HMODULE owner = nullptr;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                  GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                              reinterpret_cast<LPCWSTR>(address), &owner) != 0 &&
           owner != nullptr;
}

}  // namespace

std::vector<ModuleInfo> GetLoadedModules() {
    std::vector<ModuleInfo> mods;
    HANDLE snap =
        CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snap == INVALID_HANDLE_VALUE) return mods;

    MODULEENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (Module32FirstW(snap, &entry)) {
        do {
            // 过滤掉 Toolhelp 偶发返回的无效条目（数据文件、已被卸载的残留等）
            if (entry.hModule == nullptr || entry.modBaseSize == 0) continue;

            ModuleInfo info;
            info.name = entry.szModule;
            info.path = entry.szExePath;
            info.base = entry.hModule;
            info.size = entry.modBaseSize;
            mods.push_back(std::move(info));
        } while (Module32NextW(snap, &entry));
    }
    CloseHandle(snap);
    return mods;
}

IatPatchResult PatchIatInModule(HMODULE module, const char* funcName, void* newFunc,
                                void** original, bool assumeResolved) {
    if (!module || !funcName || !newFunc) return IatPatchResult::NotFound;

    auto base = reinterpret_cast<BYTE*>(module);
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return IatPatchResult::NotFound;
    if (dos->e_lfanew <= 0 || dos->e_lfanew > 0x1000) return IatPatchResult::NotFound;

    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return IatPatchResult::NotFound;

    const DWORD imageSize = nt->OptionalHeader.SizeOfImage;
    if (imageSize == 0) return IatPatchResult::NotFound;

    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (dir.VirtualAddress == 0 || dir.Size < sizeof(IMAGE_IMPORT_DESCRIPTOR))
        return IatPatchResult::NotFound;
    if (dir.VirtualAddress >= imageSize) return IatPatchResult::NotFound;

    auto import = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress);

    for (int desc = 0; desc < 1024 && import->Name != 0; ++desc, ++import) {
        // OriginalFirstThunk == 0 时 FirstThunk 里是解析后的地址而不是 RVA，
        // 按名称表遍历会拿到野指针，必须跳过。
        if (import->OriginalFirstThunk == 0 || import->FirstThunk == 0) continue;
        if (import->OriginalFirstThunk >= imageSize) continue;
        if (import->FirstThunk >= imageSize) continue;

        auto nameThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + import->OriginalFirstThunk);
        auto iatThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + import->FirstThunk);

        for (int i = 0; i < 8192 && nameThunk->u1.AddressOfData != 0; ++i, ++nameThunk, ++iatThunk) {
            if (IMAGE_SNAP_BY_ORDINAL(nameThunk->u1.Ordinal)) continue;

            const ULONGLONG nameRva = nameThunk->u1.AddressOfData;
            if (nameRva < sizeof(IMAGE_IMPORT_BY_NAME) || nameRva + 2 >= imageSize) continue;

            auto byName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + nameRva);
            if (!byName->Name) continue;
            if (std::strcmp(reinterpret_cast<const char*>(byName->Name), funcName) != 0) continue;

            if (!assumeResolved &&
                !LooksResolved(reinterpret_cast<void*>(iatThunk->u1.Function))) {
                return IatPatchResult::NotReady;
            }

            DWORD oldProtect = 0;
            if (!VirtualProtect(&iatThunk->u1.Function, sizeof(void*), PAGE_READWRITE,
                                &oldProtect)) {
                return IatPatchResult::NotFound;
            }
            if (original && *original == nullptr) {
                *original = reinterpret_cast<void*>(iatThunk->u1.Function);
            }
            iatThunk->u1.Function = reinterpret_cast<ULONG_PTR>(newFunc);
            VirtualProtect(&iatThunk->u1.Function, sizeof(void*), oldProtect, &oldProtect);
            return IatPatchResult::Patched;
        }
    }
    return IatPatchResult::NotFound;
}

int PatchIatInModules(const std::vector<std::wstring>& onlyThese, const char* funcName,
                      void* newFunc, void** original) {
    int patched = 0;
    for (const auto& mod : GetLoadedModules()) {
        if (!NameMatches(onlyThese, mod.name)) continue;
        if (PatchIatInModule(mod.base, funcName, newFunc, original) == IatPatchResult::Patched) {
            ++patched;
        }
    }
    return patched;
}

namespace {

// 在 nearBase 之后 4GB 范围内找一块空闲内存并提交。
void* AllocateExecutableNear(void* nearBase, size_t size) {
    SYSTEM_INFO si{};
    GetSystemInfo(&si);
    const ULONGLONG granularity = si.dwAllocationGranularity ? si.dwAllocationGranularity : 0x10000;

    BYTE* base = static_cast<BYTE*>(nearBase);
    BYTE* cursor = base;
    const ULONGLONG limit = 0x70000000ULL;  // 保持在 4GB 内即可，留出余量

    for (ULONGLONG offset = 0; offset < limit;) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (VirtualQuery(cursor, &mbi, sizeof(mbi)) == 0) {
            LogF("[eat] 扫描在 %p 处停止（VirtualQuery 失败）", cursor);
            break;
        }

        if (mbi.State == MEM_FREE && mbi.RegionSize >= size) {
            // MEM_FREE 区段的起始地址只保证 4KB 对齐，而 VirtualAlloc 指定基址时
            // 要求按分配粒度（通常 64KB）对齐，否则返回 ERROR_INVALID_ADDRESS。
            const ULONGLONG regionStart = reinterpret_cast<ULONGLONG>(mbi.BaseAddress);
            const ULONGLONG regionEnd = regionStart + mbi.RegionSize;
            ULONGLONG want = (regionStart + granularity - 1) & ~(granularity - 1);
            if (want + size > regionEnd) {
                offset += mbi.RegionSize;
                cursor = base + offset;
                continue;
            }
            void* p = VirtualAlloc(reinterpret_cast<void*>(want), size,
                                   MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (p) return p;
            LogF("[eat]   在 %p 分配失败: %lu", mbi.BaseAddress, GetLastError());
        }

        const ULONGLONG regionSize = mbi.RegionSize ? mbi.RegionSize : granularity;
        offset += regionSize;
        cursor = base + offset;
    }
    return nullptr;
}

}  // namespace

bool PatchExportInModule(HMODULE module, const char* funcName, void* newFunc, void** original) {
    if (!module || !funcName || !newFunc) return false;

    auto base = reinterpret_cast<BYTE*>(module);
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    if (dos->e_lfanew <= 0 || dos->e_lfanew > 0x1000) return false;

    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return false;

    const DWORD imageSize = nt->OptionalHeader.SizeOfImage;
    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (dir.VirtualAddress == 0 || dir.VirtualAddress >= imageSize) {
        LogF("[eat] %s: 没有导出目录", funcName);
        return false;
    }

    auto exports = reinterpret_cast<IMAGE_EXPORT_DIRECTORY*>(base + dir.VirtualAddress);
    auto names = reinterpret_cast<DWORD*>(base + exports->AddressOfNames);
    auto ordinals = reinterpret_cast<WORD*>(base + exports->AddressOfNameOrdinals);
    auto functions = reinterpret_cast<DWORD*>(base + exports->AddressOfFunctions);

    LogF("[eat] 模块 %p 导出目录: names=%lu funcs=%lu", base,
         static_cast<unsigned long>(exports->NumberOfNames),
         static_cast<unsigned long>(exports->NumberOfFunctions));

    for (DWORD i = 0; i < exports->NumberOfNames; ++i) {
        if (names[i] >= imageSize) continue;
        if (std::strcmp(reinterpret_cast<const char*>(base + names[i]), funcName) != 0) continue;

        const WORD ordinal = ordinals[i];
        if (ordinal >= exports->NumberOfFunctions) return false;

        DWORD* slot = &functions[ordinal];
        LogF("[eat] 找到 %s: ordinal=%u slotRva=0x%08lX", funcName, ordinal,
             static_cast<unsigned long>(*slot));
        // 转发器：RVA 落在导出目录内部，这种不能直接改
        if (*slot >= dir.VirtualAddress && *slot < dir.VirtualAddress + dir.Size) {
            LogF("[eat] %s 是转发器，跳过", funcName);
            return false;
        }
        if (*slot >= imageSize) {
            LogF("[eat] %s 的 RVA 越界", funcName);
            return false;
        }

        void* real = base + *slot;

        // 跳板：mov rax, <newFunc>; jmp rax
        BYTE* stub = static_cast<BYTE*>(AllocateExecutableNear(base, 64));
        if (!stub) {
            LogF("[eat] %s: 附近 4GB 内找不到可分配内存", funcName);
            return false;
        }
        LogF("[eat] %s 跳板分配于 %p (距模块 0x%llX)", funcName, stub,
             static_cast<unsigned long long>(stub - base));
        stub[0] = 0x48;
        stub[1] = 0xB8;
        std::memcpy(stub + 2, &newFunc, sizeof(void*));
        stub[10] = 0xFF;
        stub[11] = 0xE0;
        FlushInstructionCache(GetCurrentProcess(), stub, 12);

        const ULONGLONG rva = static_cast<ULONGLONG>(stub - base);
        if (rva > 0xFFFFFFFFULL) {
            VirtualFree(stub, 0, MEM_RELEASE);
            return false;
        }

        DWORD oldProtect = 0;
        if (!VirtualProtect(slot, sizeof(DWORD), PAGE_READWRITE, &oldProtect)) {
            VirtualFree(stub, 0, MEM_RELEASE);
            return false;
        }
        if (original && *original == nullptr) *original = real;
        *slot = static_cast<DWORD>(rva);
        VirtualProtect(slot, sizeof(DWORD), oldProtect, &oldProtect);

        g_stubs.push_back(stub);
        return true;
    }
    return false;
}
