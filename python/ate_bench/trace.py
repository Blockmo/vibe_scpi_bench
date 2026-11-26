"""NI I/O Trace XML 紀錄檔解析。

紀錄檔在擷取期間是「尚未結束」的 XML，所以這裡用正規表示式逐筆取出完整的
<call>...</call> 區塊，可重複呼叫以增量讀取 (供即時統計使用)。
"""
from __future__ import annotations

import html
import re
from typing import Dict, Iterator, List, Optional

_CALL_RE = re.compile(rb"<call\b([^>]*)>(.*?)</call>", re.S)
_ATTR_RE = re.compile(r'([\w-]+)="([^"]*)"')
_STATUS_RE = re.compile(r"<status\b([^>]*?)(?:/>|>(.*?)</status>)", re.S)
_SECTION_RE = re.compile(r"<(input|output)\b([^>]*?)(?:/>|>(.*?)</\1>)", re.S)
_PARAM_RE = re.compile(r'<parameter name="([^"]*)"\s*(?:/>|>(.*?)</parameter>)', re.S)
_BUFFER_RE = re.compile(r"<buffer\b([^>]*)>(.*?)</buffer>", re.S)
_HEX_RE = re.compile(r"\b[0-9A-Fa-f]{2}\b")


def _tod(text: str) -> Optional[float]:
    """'23:40:18.1035' -> 當日秒數。"""
    try:
        h, m, s = text.split(":")
        return int(h) * 3600 + int(m) * 60 + float(s)
    except (ValueError, AttributeError):
        return None


class Call:
    __slots__ = ("api", "func", "pid", "tid", "ok", "status_code", "status_name", "status_text",
                 "t_in", "t_out", "time_text", "params_in", "params_out",
                 "buf_in", "buf_out", "buf_in_size", "buf_out_size")

    def __init__(self) -> None:
        self.api = self.func = ""
        self.pid = self.tid = 0
        self.ok = True
        self.status_code = self.status_name = self.status_text = ""
        self.t_in: Optional[float] = None
        self.t_out: Optional[float] = None
        self.time_text = ""
        self.params_in: Dict[str, str] = {}
        self.params_out: Dict[str, str] = {}
        self.buf_in: Optional[bytes] = None
        self.buf_out: Optional[bytes] = None
        self.buf_in_size = self.buf_out_size = 0

    @property
    def duration(self) -> float:
        if self.t_in is None or self.t_out is None:
            return 0.0
        d = self.t_out - self.t_in
        return d + 86400 if d < -43200 else max(d, 0.0)


def _parse_call(attr_text: str, body: str) -> Call:
    c = Call()
    attrs = dict(_ATTR_RE.findall(attr_text))
    c.api = attrs.get("type", "")
    c.func = html.unescape(attrs.get("description", ""))
    try:
        c.pid = int(attrs.get("process", "0"), 16)
        c.tid = int(attrs.get("thread", "0"), 16)
    except ValueError:
        pass
    m = _STATUS_RE.search(body)
    if m:
        st = dict(_ATTR_RE.findall(m.group(1)))
        c.ok = st.get("type", "success") != "failure"
        c.status_code = st.get("code", "")
        c.status_name = st.get("name", "")
        c.status_text = html.unescape((m.group(2) or "").strip())
    for sm in _SECTION_RE.finditer(body):
        which, sattr, inner = sm.group(1), dict(_ATTR_RE.findall(sm.group(2))), sm.group(3) or ""
        params = {html.unescape(k): html.unescape(v or "") for k, v in _PARAM_RE.findall(inner)}
        buf, size = None, 0
        bm = _BUFFER_RE.search(inner)
        if bm:
            battr = dict(_ATTR_RE.findall(bm.group(1)))
            buf = bytes(int(x, 16) for x in _HEX_RE.findall(bm.group(2)))
            size = int(battr.get("size", len(buf)) or 0)
        if which == "input":
            c.t_in, c.time_text = _tod(sattr.get("time", "")), sattr.get("time", "")
            c.params_in, c.buf_in, c.buf_in_size = params, buf, size
        else:
            c.t_out = _tod(sattr.get("time", ""))
            c.params_out, c.buf_out, c.buf_out_size = params, buf, size
    return c


class TraceReader:
    """增量讀取 I/O Trace XML 紀錄檔。"""

    def __init__(self, path: str) -> None:
        self.path = path
        self._offset = 0

    def read_new(self) -> List[Call]:
        try:
            with open(self.path, "rb") as f:
                f.seek(self._offset)
                data = f.read()
        except (OSError, IOError):
            return []
        calls: List[Call] = []
        end = 0
        for m in _CALL_RE.finditer(data):
            end = m.end()
            try:
                calls.append(_parse_call(m.group(1).decode("utf-8", "replace"), m.group(2).decode("utf-8", "replace")))
            except Exception:  # 單筆格式異常不應中斷整體分析
                continue
        self._offset += end
        return calls


def read_all(path: str) -> Iterator[Call]:
    return iter(TraceReader(path).read_new())
