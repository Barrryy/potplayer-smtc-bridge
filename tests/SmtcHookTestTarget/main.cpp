// 注入测试靶子。
//
// 构建后改名成 PotPlayerMini64.exe，这样才会命中 PotPlayerSmtcHook.dll 的模块白名单。
// 它做四件事：打开几个「媒体文件」、中途加载一个替身 MediaDB64.dll、
// 把 pid 打出来、然后挂着一会儿等注入生效。
// 用它可以在不碰真 PotPlayer 的前提下验证注入器、日志、IAT 挂接是否正常。

#include <windows.h>

#include <cstdio>

namespace {

void TouchMediaFiles() {
    const wchar_t* exts[] = {L".mp3", L".flac", L".mkv", L".txt"};
    for (const wchar_t* ext : exts) {
        wchar_t path[MAX_PATH] = {};
        wsprintfW(path, L"%s\\bridge-test%s", L"C:\\Windows\\Temp", ext);
        HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_ALWAYS,
                               FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    }
}

}  // namespace

int main() {
    std::printf("test target pid=%lu\n", GetCurrentProcessId());
    std::fflush(stdout);

    for (int i = 0; i < 20; ++i) {
        TouchMediaFiles();

        // 第 4 圈（约 2 秒后）再加载替身 MediaDB64.dll，
        // 用来验证注入模块的「后加载模块补挂」逻辑。
        if (i == 4) {
            HMODULE late = LoadLibraryW(L"MediaDB64.dll");
            if (late) {
                auto probe = reinterpret_cast<int (*)()>(GetProcAddress(late, "BridgeTestProbe"));
                const int result = probe ? probe() : -999;
                std::printf("late MediaDB64.dll loaded, probe=%d\n", result);
                std::fflush(stdout);
            } else {
                std::printf("late MediaDB64.dll load failed: %lu\n", GetLastError());
                std::fflush(stdout);
            }
        }
        Sleep(500);
    }
    std::printf("test target exiting\n");
    return 0;
}
