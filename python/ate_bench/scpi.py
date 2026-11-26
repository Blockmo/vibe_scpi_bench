"""SCPI 訊息拆解與正規化。

一次 write 可能含多個指令 (以 ';' 或換行分隔)，且後續指令可沿用前一個指令的路徑
(例如 "SOUR:VOLT 1;CURR 2" 的第二個指令實際上是 SOUR:CURR)。
"""
from __future__ import annotations

import re
from typing import List, NamedTuple

_HEADER_RE = re.compile(r"^[*:]?[A-Za-z_][A-Za-z0-9_:.\[\]]*\??$")
_NODE_RE = re.compile(r"^([A-Za-z_]+)(\d*)$")
_BLOCK_RE = re.compile(r"#([1-9])(\d+)")
_PRINTABLE = set(range(0x20, 0x7F)) | {0x09, 0x0A, 0x0D}


class Command(NamedTuple):
    key: str        # 統計用的鍵 (正規化後的完整路徑，查詢含 '?')
    raw: str        # 原始寫法 (單一指令，不含參數)
    args: str       # 參數字串
    is_query: bool
    kind: str       # 'scpi' | 'common' | 'raw' | 'binary'


def is_mostly_text(data: bytes) -> bool:
    if not data:
        return True
    bad = sum(1 for b in data if b not in _PRINTABLE)
    return bad <= max(1, len(data) // 20)


def decode(data: bytes) -> str:
    return data.decode("latin-1")


def shorten_block(text: str, limit: int = 120) -> str:
    """把 IEEE 488.2 區塊資料 (#<n><len><data>) 與過長的內容縮短，方便顯示。"""
    m = _BLOCK_RE.search(text)
    if m:
        ndig = int(m.group(1))
        digits = m.group(2)[:ndig]
        if len(digits) == ndig:
            text = text[: m.start()] + "#<block %s bytes>" % int(digits)
    text = "".join(ch if 0x20 <= ord(ch) < 0x7F else "." for ch in text)
    if len(text) > limit:
        text = text[: limit - 3] + "..."
    return text


def _split_units(line: str) -> List[str]:
    """以 ';' 分割，但略過引號內的分號；遇到區塊資料就不再分割。"""
    units, cur, quote = [], [], ""
    i, n = 0, len(line)
    while i < n:
        ch = line[i]
        if quote:
            cur.append(ch)
            if ch == quote:
                quote = ""
        elif ch in "'\"":
            quote = ch
            cur.append(ch)
        elif ch == "#" and i + 1 < n and line[i + 1] in "123456789":
            cur.append(line[i:])
            break
        elif ch == ";":
            units.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
        i += 1
    units.append("".join(cur))
    return units


def short_form(node: str) -> str:
    """SCPI 助記詞的短格式: VOLTage -> VOLT, POWer -> POW, FREQuency -> FREQ。"""
    m = _NODE_RE.match(node)
    if not m:
        return node.upper()
    word, suffix = m.group(1).upper(), m.group(2)
    if len(word) > 4:
        word = word[:3] if word[3] in "AEIOU" else word[:4]
    return word + suffix


def normalize_header(header: str, normalize: bool = True) -> str:
    query = header.endswith("?")
    body = header[:-1] if query else header
    if body.startswith("*"):
        key = body.upper()
    else:
        nodes = [p for p in body.replace("[", "").replace("]", "").split(":") if p]
        key = ":".join(short_form(p) if normalize else p.upper() for p in nodes)
    return key + ("?" if query else "")


def parse_message(data: bytes, normalize: bool = True) -> List[Command]:
    """把一次 write 的資料拆成個別指令。"""
    if not data:
        return []
    block = _BLOCK_RE.search(decode(data[:4096]))
    if not is_mostly_text(data) and not block:
        return [Command("<binary>", "<binary>", "%d bytes" % len(data), False, "binary")]

    text = decode(data)
    if block:  # 區塊資料之後都是二進位，不可再用換行分割
        lines = [text]
    else:
        lines = re.split(r"[\r\n]+", text)

    out: List[Command] = []
    for line in lines:
        line = line.strip("\x00 \t\r\n")
        if not line:
            continue
        prefix = ""  # 前一個指令的父路徑
        first = True
        for unit in _split_units(line):
            unit = unit.strip()
            if not unit:
                continue
            parts = unit.split(None, 1)
            header = parts[0]
            args = parts[1].strip() if len(parts) > 1 else ""
            if not _HEADER_RE.match(header):
                # 非 SCPI 語法 (舊式儀器指令等)，原樣保留
                out.append(Command(shorten_block(unit, 60), unit[:60], "", unit.rstrip().endswith("?"), "raw"))
                first = False
                continue
            if header.startswith("*"):
                full, kind = header, "common"
            else:
                kind = "scpi"
                if header.startswith(":") or first or not prefix:
                    full = header.lstrip(":")
                else:
                    full = prefix + ":" + header
                prefix = full.rsplit(":", 1)[0] if ":" in full else ""
            first = False
            out.append(Command(normalize_header(full, normalize), full, shorten_block(args), header.endswith("?"), kind))
    return out
