"""命令列入口。

  python -m ate_bench                      開啟圖形介面
  python -m ate_bench capture              在主控台擷取 (Enter 停止；輸入文字後 Enter = 標記測項)
  python -m ate_bench capture --run "ATE.exe 參數"   啟動 ATE 程式並在它結束時自動停止
  python -m ate_bench analyze trace.xml    分析既有的 I/O Trace XML 紀錄檔
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys
import threading
import time


def _capture(args) -> int:
    from .iotrace import IOTraceError
    from .session import Session
    try:
        s = Session(args.out, normalize=not args.no_normalize)
        print("啟動 NI I/O Trace 擷取中…")
        s.start()
    except IOTraceError as e:
        print("錯誤:", e, file=sys.stderr)
        return 2
    print("擷取中，紀錄檔:", s.log_path)
    done = threading.Event()
    proc = None
    try:
        if args.run:
            proc = subprocess.Popen(args.run, shell=True)
            print("已啟動:", args.run)
        else:
            print("請開始執行 ATE 程式。輸入測項名稱後按 Enter 可加標記；直接按 Enter 停止。")

            def reader() -> None:
                for line in sys.stdin:
                    label = line.strip()
                    if not label:
                        break
                    s.mark(label)
                    print("  已標記測項: %s" % label)
                done.set()
            threading.Thread(target=reader, daemon=True).start()
        while not done.is_set():
            s.poll()
            t = s.analyzer.live_totals()
            print("\r  %6.0fs  儀器 %d  指令 %d (不重複 %d)  寫 %d / 讀 %d  錯誤 %d   "
                  % (s.elapsed(), t["instruments"], t["commands"], t["unique"], t["writes"], t["reads"], t["errors"]),
                  end="", flush=True)
            if proc is not None and proc.poll() is not None:
                break
            time.sleep(1.0)
    except KeyboardInterrupt:
        pass
    print("\n停止擷取，產生報告…")
    path = s.stop()
    print("報告:", path)
    if not args.no_open:
        os.startfile(path)
    return 0


def _analyze(args) -> int:
    from .session import analyze_file
    path = analyze_file(args.xml, args.out, normalize=not args.no_normalize)
    print("報告:", path)
    if not args.no_open:
        os.startfile(path)
    return 0


def main(argv=None) -> int:
    p = argparse.ArgumentParser(prog="ate_bench", description="擷取並分析 ATE 程式使用的 SCPI 指令")
    sub = p.add_subparsers(dest="cmd")
    c = sub.add_parser("capture", help="在主控台擷取")
    c.add_argument("-o", "--out", default="captures", help="輸出根資料夾 (預設 captures)")
    c.add_argument("--run", help="要啟動的 ATE 程式命令列；程式結束即停止擷取")
    a = sub.add_parser("analyze", help="分析既有的 I/O Trace XML 紀錄檔")
    a.add_argument("xml")
    a.add_argument("-o", "--out", help="輸出資料夾 (預設與 XML 同資料夾)")
    for q in (c, a):
        q.add_argument("--no-normalize", action="store_true", help="不要把 SCPI 長短格式歸併")
        q.add_argument("--no-open", action="store_true", help="完成後不要自動開啟報告")
    args = p.parse_args(argv)
    if args.cmd == "capture":
        return _capture(args)
    if args.cmd == "analyze":
        return _analyze(args)
    from .gui import run
    run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
