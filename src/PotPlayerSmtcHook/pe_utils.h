#pragma once

#include <windows.h>

#include <string>
#include <vector>

struct ModuleInfo {
    std::wstring name;
    HMODULE base;
    size_t size;
    std::wstring path;
};

// 用 Toolhelp 快照枚举当前进程已加载模块（不依赖 PEB 结构布局）。
std::vector<ModuleInfo> GetLoadedModules();

// 在单个模块的导入表里查找名为 funcName 的导入项（不限来源 DLL），
// 命中则把 IAT 槽改成 newFunc，并通过 original 回传原地址。
//
// 实现做了严格的边界校验：导入目录 / thunk 数组 / 名称 RVA 越界一律跳过。
// OriginalFirstThunk 为 0 的描述符直接跳过——那种情况下 IAT 里存的是解析后的
// 函数地址而不是 RVA，按名称表解析会得到野指针。
enum class IatPatchResult {
    Patched,   // 成功替换
    NotFound,  // 该模块没有导入这个函数
    NotReady,  // 找到了，但 IAT 还没被加载器解析完 —— 稍后重试
};

// assumeResolved：调用方已确认模块加载完毕时置 true，可跳过「IAT 是否已解析」的探测。
// 该探测会调用 GetModuleHandleEx，而 DLL 加载通知回调运行在加载器锁内，
// 那条路径上必须避免再次进入加载器，因此需要这个开关。
IatPatchResult PatchIatInModule(HMODULE module, const char* funcName, void* newFunc,
                                void** original, bool assumeResolved = false);

// 在模块的导出表（EAT）上改跳转。
//
// 为什么需要它：IAT 补丁只能覆盖「此刻已经加载」的模块。实测 MediaDB64.dll
// 从加载到创建 SMTC 会话不足 3ms，等我们收到 DLL 加载通知时它的初始化早就跑完了。
// 改 EAT 则不同——之后任何模块在加载时解析导入、或运行时 GetProcAddress，
// 拿到的都是我们指定的地址，彻底绕开时序问题。
//
// 实现：EAT 里存的是 32 位 RVA，而我们的代码在别的模块里、距离可能超出 4GB，
// 因此先在目标模块附近分配一段可执行内存作为跳板（写入 `mov rax, <newFunc>; jmp rax`），
// 再把导出地址改写到该跳板。
bool PatchExportInModule(HMODULE module, const char* funcName, void* newFunc, void** original);

// 只对白名单模块打补丁。onlyThese 为空表示不过滤（不推荐）。
// 返回成功打补丁的模块数。
int PatchIatInModules(const std::vector<std::wstring>& onlyThese, const char* funcName,
                      void* newFunc, void** original);
