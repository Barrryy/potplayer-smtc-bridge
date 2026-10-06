#pragma once

void LogProcessInfo();
void InstallProbes();

// 崩溃时把异常位置落到日志里，便于定位
void InstallExceptionLogger();
