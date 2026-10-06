// 替身 MediaDB64.dll —— 专门用来验证「模块后加载」时的补挂逻辑。
//
// 它唯一的作用是让导入表里出现 RoGetActivationFactory 这一项，
// 从而可以被 PotPlayerSmtcHook.dll 的 IAT 补丁命中。
// 编译产物必须命名为 MediaDB64.dll，并放在靶子同目录。

#include <windows.h>
#include <objbase.h>

extern "C" __declspec(dllimport) HRESULT WINAPI RoGetActivationFactory(void* classId,
                                                                      const IID& iid,
                                                                      void** factory);

extern "C" __declspec(dllexport) int BridgeTestProbe() {
    void* out = nullptr;
    // 参数故意传 null：这里只关心调用是否经过被替换的 IAT 槽
    return static_cast<int>(RoGetActivationFactory(nullptr, IID_IUnknown, &out));
}
