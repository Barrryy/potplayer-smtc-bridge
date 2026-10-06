// 最小验证靶子：只用来检验「导入表补丁」本身是否成立。
// 编译出来的 exe 越小越干净，越容易判断补丁的成败。
#include <windows.h>

#include <cstdio>

int main() {
    std::printf("pe-probe alive\n");
    std::fflush(stdout);
    Sleep(3000);
    return 0;
}
