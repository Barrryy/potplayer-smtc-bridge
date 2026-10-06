#pragma once

// 宽字符控制台输出。
// MinGW 的 wprintf 在默认 "C" locale 下会静默丢弃所有非 ASCII 字符，
// 所以这里直接走 WriteConsoleW；被重定向到文件/管道时退化为 UTF-8 字节流。
//
// 注意：MSVCRT 语义下，宽字符格式串里的 %s 期望 char*，
// 传 wchar_t* 必须用 %ls，否则只会打出第一个字节（v0.1 的路径就被截成了 "C"）。
void PrintW(const wchar_t* fmt, ...);
