#include "console.h"

#include <windows.h>

#include <cstdarg>
#include <cstdio>
#include <string>

void PrintW(const wchar_t* fmt, ...) {
    wchar_t buf[4096] = {};
    va_list args;
    va_start(args, fmt);
    vswprintf(buf, 4095, fmt, args);
    va_end(args);

    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    if (out == INVALID_HANDLE_VALUE || out == nullptr) return;

    DWORD mode = 0;
    if (GetConsoleMode(out, &mode)) {
        DWORD written = 0;
        WriteConsoleW(out, buf, static_cast<DWORD>(wcslen(buf)), &written, nullptr);
        return;
    }

    const int need = WideCharToMultiByte(CP_UTF8, 0, buf, -1, nullptr, 0, nullptr, nullptr);
    if (need <= 1) return;
    std::string text(static_cast<size_t>(need), '\0');
    WideCharToMultiByte(CP_UTF8, 0, buf, -1, &text[0], need, nullptr, nullptr);
    text.resize(static_cast<size_t>(need - 1));
    DWORD written = 0;
    WriteFile(out, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);
}
