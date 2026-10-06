#pragma once

#include <windows.h>

#include <string>

// 文件日志（线程安全）。
// 路径：%TEMP%\potplayer-smtc-bridge\hook-<pid>-<时间戳>.log
//
// 重要约定：本模块所有格式化一律走【窄字符】。
//
// MinGW 在 -std=c++17 下按 MSVCRT 语义解析格式串，而 MSVCRT 的宽字符格式化函数里
// %s 期望的是 char* 而不是 wchar_t*。混用会把宽字符串当作窄字符串解引用：
// 轻则整行输出为空，重则读到野指针直接崩在 msvcrt.dll（v0.1 就是这么崩的）。
// 因此：
//   * LogF 只接受窄字符（%s = char*）
//   * 宽字符串先经 WideToUtf8 转换，再交给 LogW / LogTagW

void LogInit();
void LogClose();

void LogF(const char* fmt, ...);
void LogW(const wchar_t* wide);
void LogTagW(const char* tag, const wchar_t* wide);

void LogGuid(const char* tag, const GUID& guid);
void LogHString(const char* tag, void* hstring);
void LogAddressModule(const char* tag, const void* address);

// 取出 HSTRING 里的原始宽字符串；失败返回 nullptr。仅用于比较类名。
const wchar_t* HStringRaw(void* hstring);

std::string WideToUtf8(const wchar_t* wide);
std::wstring LogFilePath();
