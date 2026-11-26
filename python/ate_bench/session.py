"""一次擷取工作階段: 啟動擷取 -> 即時統計 -> 停止 -> 產生報告。GUI 與 CLI 共用。"""
from __future__ import annotations

import datetime as dt
import json
import os
from typing import Optional

from . import report
from .analyze import Analyzer
from .iotrace import IOTrace, process_name
from .trace import TraceReader

SESSION_FILE = "session.json"


def _tod(t: dt.datetime) -> float:
    return t.hour * 3600 + t.minute * 60 + t.second + t.microsecond / 1e6


class Session:
    def __init__(self, base_dir: str = "captures", normalize: bool = True) -> None:
        self.base_dir = base_dir
        self.analyzer = Analyzer(normalize=normalize)
        self.trace = IOTrace()
        self.start_time: Optional[dt.datetime] = None
        self.out_dir = ""
        self.log_path = ""
        self._reader: Optional[TraceReader] = None

    def start(self) -> None:
        now = dt.datetime.now()
        self.out_dir = os.path.abspath(os.path.join(self.base_dir, now.strftime("%Y%m%d_%H%M%S")))
        self.log_path = os.path.join(self.out_dir, "trace.xml")
        self.trace.start(self.log_path)
        self.start_time = dt.datetime.now()
        self.analyzer.start_tod = _tod(self.start_time)
        self._reader = TraceReader(self.log_path)

    def poll(self) -> int:
        """讀入新的呼叫紀錄，回傳本次新增筆數。"""
        if not self._reader:
            return 0
        calls = self._reader.read_new()
        for c in calls:
            if c.pid not in self.analyzer.pid_names:
                self.analyzer.pid_names[c.pid] = process_name(c.pid)
            self.analyzer.feed(c)
        return len(calls)

    def mark(self, label: str) -> None:
        """標記一個測項的開始；之後的指令都歸在這個測項，直到下一個標記。"""
        assert self.start_time is not None
        now = dt.datetime.now()
        midnight = dt.datetime.combine(self.start_time.date(), dt.time())
        self.analyzer.add_marker((now - midnight).total_seconds(), label)

    def elapsed(self) -> float:
        return (dt.datetime.now() - self.start_time).total_seconds() if self.start_time else 0.0

    def stop(self) -> str:
        """停止擷取並產生報告，回傳 report.html 路徑。"""
        self.trace.stop()
        self.poll()
        a = self.analyzer
        captured = self.start_time.strftime("%Y-%m-%d %H:%M:%S") if self.start_time else ""
        with open(os.path.join(self.out_dir, SESSION_FILE), "w", encoding="utf-8") as f:
            json.dump({"captured": captured, "start_tod": a.start_tod, "markers": a.markers,
                       "pid_names": {str(k): v for k, v in a.pid_names.items()}}, f, ensure_ascii=False, indent=1)
        return report.generate(a, self.out_dir, source=self.log_path, captured=captured)


def analyze_file(xml_path: str, out_dir: Optional[str] = None, normalize: bool = True) -> str:
    """分析既有的 I/O Trace XML 紀錄檔 (本工具擷取的，或從 NI I/O Trace 手動記錄的)。"""
    xml_path = os.path.abspath(xml_path)
    out_dir = out_dir or os.path.dirname(xml_path)
    a = Analyzer(normalize=normalize)
    captured = ""
    meta_path = os.path.join(os.path.dirname(xml_path), SESSION_FILE)
    if os.path.isfile(meta_path):
        with open(meta_path, encoding="utf-8") as f:
            meta = json.load(f)
        captured = meta.get("captured", "")
        a.start_tod = meta.get("start_tod")
        a.pid_names = {int(k): v for k, v in meta.get("pid_names", {}).items()}
        for t, label in meta.get("markers", []):
            a.add_marker(t, label)
    for c in TraceReader(xml_path).read_new():
        a.feed(c)
    return report.generate(a, out_dir, source=xml_path, captured=captured)
