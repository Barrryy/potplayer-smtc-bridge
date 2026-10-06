#!/usr/bin/env python3
"""汇总 potplayer-smtc-bridge 的侦察日志。

用法:
    python summarize-log.py                    # 自动取 %TEMP% 下最新的日志
    python summarize-log.py <日志路径>

输出重点：
    * PotPlayer 请求过的 WinRT 激活（类名 + IID）—— 这是 v0.2 需要的常量来源
    * 是否出现 SystemMediaTransportControls 相关的激活
    * 媒体文件打开记录（当前播放文件的路径候选）
    * 进程内窗口列表
"""

import os
import re
import sys
from pathlib import Path

# 重定向到文件/管道时，Windows 上 Python 默认用本地代码页（中文系统是 cp936），
# 中文会变成乱码。这里统一成 UTF-8；直接连控制台时本来就是 UTF-8，属空操作。
try:
    if sys.stdout.encoding and sys.stdout.encoding.lower().replace("-", "") != "utf8":
        sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

LOG_DIR = Path(os.environ.get("TEMP", ".")) / "potplayer-smtc-bridge"

INTERESTING_CLASS = "SystemMediaTransportControls"

# 注意：MSVCRT 的 %p 输出不带 0x 前缀，所以两种写法都要兼容
RE_MODULE = re.compile(r"^\s*(\S+)\s+base=0x?([0-9A-Fa-f]+)\s+size=0x?([0-9A-Fa-f]+)")
RE_WINDOW = re.compile(r"hwnd=0x?([0-9A-Fa-f]+)")


def latest_log() -> Path | None:
    if not LOG_DIR.is_dir():
        return None
    logs = sorted(LOG_DIR.glob("hook-*.log"), key=lambda p: p.stat().st_mtime)
    return logs[-1] if logs else None


def main() -> int:
    if len(sys.argv) > 1:
        path = Path(sys.argv[1])
    else:
        path = latest_log()
        if path is None:
            print(f"没有找到日志目录: {LOG_DIR}")
            print(r"先运行 SmtcLoader.exe 注入 PotPlayer，再播放一个文件。")
            return 1

    text = path.read_text(encoding="utf-8", errors="replace")
    lines = text.splitlines()
    print(f"日志: {path}")
    print(f"行数: {len(lines)}")
    print()

    # --- 模块 ---
    modules = []
    for ln in lines:
        m = RE_MODULE.match(ln.split("] ", 1)[-1])
        if m:
            modules.append(m.group(1))
    print("== 模块中与 SMTC 相关的 ==")
    hit = [m for m in modules if "Media" in m or "PotPlayer" in m]
    if not hit:
        print("  （没有匹配到，检查日志是否为空行 —— 那说明是 v0.1 的格式化缺陷）")
    for m in hit:
        print(f"  {m}")
    print(f"  （共 {len(modules)} 个模块）")
    print()

    # --- WinRT 激活 ---
    activations = []
    current = {}
    for ln in lines:
        body = ln.split("] ", 1)[-1] if "] " in ln else ln
        if body.startswith("--- Ro"):
            if current:
                activations.append(current)
            current = {"kind": body.strip("- ").strip()}
        elif "class" in body and '"' in body and current:
            current["class"] = body.split('"')[1]
        elif body.strip().startswith("iid") and current:
            current["iid"] = body.split()[-1]
        elif body.strip().startswith("hr=") and current:
            current["hr"] = body.split()[0]
    if current:
        activations.append(current)

    print(f"== WinRT 激活调用（{len(activations)} 次）==")
    seen = {}
    for act in activations:
        key = (act.get("kind"), act.get("class"), act.get("iid"))
        seen[key] = seen.get(key, 0) + 1
    for (kind, cls, iid), count in sorted(seen.items(), key=lambda x: -x[1]):
        flag = "  <== SMTC" if cls and INTERESTING_CLASS in cls else ""
        print(f"  x{count:<3} {kind:<26} {cls or '?':<52} {iid or ''}{flag}")
    print()

    # --- 媒体文件打开 ---
    opens = [ln.split("[open] ", 1)[1] for ln in lines if "[open]" in ln]
    print(f"== 媒体文件打开记录（{len(opens)} 条）==")
    for p in opens[-15:]:
        print(f"  {p}")
    print()

    # --- 窗口 ---
    windows = [ln.split("] ", 1)[-1].strip() for ln in lines if RE_WINDOW.search(ln)]
    print(f"== 进程内窗口（{len(windows)} 个）==")
    for w in windows:
        print(f"  {w}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
