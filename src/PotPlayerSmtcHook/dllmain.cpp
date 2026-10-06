#include <windows.h>

#include "logger.h"
#include "probes.h"

namespace {

DWORD WINAPI InitThread(LPVOID) {
    LogInit();
    LogF("=== PotPlayerSmtcHook v0.1.2 (recon build) ===");
    LogProcessInfo();
    InstallProbes();
    LogF("=== recon ready; play something in PotPlayer ===");
    return 0;
}

}  // namespace

// 供「导入表补丁」引用的锚点符号。
//
// 把 PotPlayerMini64.exe 的导入表里加上 PotPlayerSmtcHook.dll 的这条导入之后，
// 加载器会在启动时自动把我们加载进来（并执行 DllMain）。
// 这个函数本身没有用途，只是让导入项有个名字可指向。
extern "C" __declspec(dllexport) int PotPlayerSmtcBridgeAttach() {
    return 1;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        // 异常记录器要在做任何事之前装好，之后才会留下排障线索。
        InstallExceptionLogger();
        // DllMain 里不做重活，其余全部丢到独立线程。
        HANDLE thread = CreateThread(nullptr, 0, InitThread, nullptr, 0, nullptr);
        if (thread) CloseHandle(thread);
    }
    return TRUE;
}
