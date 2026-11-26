"""把 I/O Trace 的 API 呼叫彙整成「每台儀器用了哪些 SCPI 指令」。"""
from __future__ import annotations

import re
from collections import Counter, OrderedDict
from typing import Dict, List, Optional, Tuple

from . import scpi
from .trace import Call

VISA_WRITE = {"viWrite", "viWriteAsync", "Formatted viWrite", "viWriteFromFile"}
VISA_READ = {"viRead", "viReadAsync", "Formatted viRead", "viReadToFile"}
GPIB_WRITE = {"ibwrt", "ibwrta", "ibwrtf", "Send", "SendList", "SendDataBytes"}
GPIB_READ = {"ibrd", "ibrda", "ibrdf", "Receive", "RcvRespMsg"}
# 這些呼叫只是把資料放進格式化緩衝區，真正的匯流排 I/O 會另外記成 "Formatted viWrite/viRead"
WRAPPERS = {"viBufWrite", "viBufRead", "viPrintf", "viVPrintf", "viSPrintf", "viVSPrintf",
            "viScanf", "viVScanf", "viSScanf", "viVSScanf", "viQueryf", "viVQueryf",
            "ibcmd", "ibcmda", "SendCmds"}

_VI_RE = re.compile(r"^(.*) \((0x[0-9A-Fa-f]+)\)$")
_INT_RE = re.compile(r"-?(?:0x[0-9A-Fa-f]+|\d+)")
SYSTEM = "(無特定儀器)"


def _int(text: Optional[str], default: int = 0) -> int:
    m = _INT_RE.search(text or "")
    if not m:
        return default
    try:
        return int(m.group(0), 0)
    except ValueError:
        return default


def _gpib_name(board: int, pad: int, sad: int = 0) -> str:
    return "GPIB%d::%d%s::INSTR" % (board, pad, "::%d" % sad if sad else "")


class CmdStat:
    __slots__ = ("key", "kind", "is_query", "count", "args", "raw", "resp", "total", "max", "errors", "first_seq")

    def __init__(self, key: str, kind: str, is_query: bool, seq: int) -> None:
        self.key, self.kind, self.is_query = key, kind, is_query
        self.count = 0
        self.args: Counter = Counter()
        self.raw: "OrderedDict[str, None]" = OrderedDict()
        self.resp: "OrderedDict[str, None]" = OrderedDict()
        self.total = 0.0
        self.max = 0.0
        self.errors = 0
        self.first_seq = seq


class Instrument:
    def __init__(self, idx: int, name: str, api: str) -> None:
        self.idx, self.name, self.api = idx, name, api
        self.idn = ""
        self.writes = self.reads = self.commands = 0
        self.bytes_out = self.bytes_in = 0
        self.io_time = 0.0
        self.errors = 0
        self.cmds: Dict[str, CmdStat] = {}
        self.others: Dict[str, List[float]] = {}  # func -> [count, errors, time]
        self.pending: List[Tuple[CmdStat, int]] = []  # 等待回應的查詢


class Analyzer:
    def __init__(self, normalize: bool = True, max_text: int = 300) -> None:
        self.normalize = normalize
        self.max_text = max_text
        self.instruments: Dict[str, Instrument] = {}
        self.events: List[list] = []
        self.event_cmds: Dict[int, List[str]] = {}
        self.errors: List[dict] = []
        self.markers: List[Tuple[float, str]] = []
        self.pid_names: Dict[int, str] = {}
        self.pid_calls: Counter = Counter()
        self.n_calls = 0
        self.truncated_buffers = 0
        self.first_abs: Optional[float] = None
        self.last_abs = 0.0
        self.first_text = ""
        self.start_tod: Optional[float] = None  # 擷取開始的當日秒數 (標記時間的基準日)
        self._last_tod: Optional[float] = None
        self._day = 0
        self._visa_sessions: Dict[Tuple[int, int], str] = {}
        self._gpib_devs: Dict[Tuple[int, int], str] = {}
        self._gpib_addr: Dict[Tuple[int, str], dict] = {}

    # ------------------------------------------------------------------ 儀器辨識
    def _inst(self, name: str, api: str) -> Instrument:
        inst = self.instruments.get(name)
        if inst is None:
            inst = self.instruments[name] = Instrument(len(self.instruments), name, api)
        return inst

    def _resolve_visa(self, c: Call) -> Optional[str]:
        p = c.params_in
        if c.func == "viOpen":
            name = p.get("rsrcName", "")
            sess = _int(c.params_out.get("*vi"))
            if name and sess and c.ok:
                self._visa_sessions[(c.pid, sess)] = name
            return name or None
        vi = p.get("vi")
        if vi is None:
            return None
        m = _VI_RE.match(vi)
        if m:
            name, sess = m.group(1).strip(), _int(m.group(2))
            if name:
                self._visa_sessions.setdefault((c.pid, sess), name)
                return name
        sess = _int(vi)
        return self._visa_sessions.get((c.pid, sess))

    def _resolve_gpib(self, c: Call) -> Optional[str]:
        p = c.params_in
        if c.func == "ibdev":
            name = _gpib_name(_int(p.get("ud")), _int(p.get("pad")), _int(p.get("sad")) & 0x1F)
            self._gpib_devs[(c.pid, _int(c.params_out.get("ud"), -1))] = name
            return name
        if "BrdIndx" in p:
            if "address" in p:
                addr = _int(p.get("address"))
                return _gpib_name(_int(p.get("BrdIndx")), addr & 0xFF, (addr >> 8) & 0x1F)
            return "GPIB%d (多重位址)" % _int(p.get("BrdIndx")) if c.func in GPIB_WRITE | GPIB_READ else None
        if "ud" not in p:
            return None
        ud = _int(p.get("ud"), -1)
        known = self._gpib_devs.get((c.pid, ud))
        if known:
            return known
        label = p.get("Brd/Dev Name", "") or "ud%d" % ud
        if re.match(r"(?i)^gpib\d+$", label):
            # 介面卡層級 I/O: 位址由先前的 ibcmd 指定
            state = self._gpib_addr.setdefault((c.pid, label), {"listen": [], "talk": None})
            board = _int(label[4:])
            if c.func in ("ibcmd", "ibcmda") and c.buf_in:
                for b in c.buf_in:
                    if b == 0x3F:
                        state["listen"] = []
                    elif 0x20 <= b <= 0x3E:
                        state["listen"].append(b - 0x20)
                    elif b == 0x5F:
                        state["talk"] = None
                    elif 0x40 <= b <= 0x5E:
                        state["talk"] = b - 0x40
                return None
            if c.func in GPIB_WRITE and state["listen"]:
                return _gpib_name(board, state["listen"][0])
            if c.func in GPIB_READ and state["talk"] is not None:
                return _gpib_name(board, state["talk"])
            return None
        return "NI-488.2 %s" % label

    # ------------------------------------------------------------------ 主流程
    def add_marker(self, abs_seconds: float, label: str) -> None:
        self.markers.append((abs_seconds, label))
        self.markers.sort()

    def feed(self, c: Call) -> None:
        self.n_calls += 1
        seq = self.n_calls
        self.pid_calls[c.pid] += 1
        tod = c.t_in if c.t_in is not None else (self._last_tod or 0.0)
        if self._last_tod is None:
            # 擷取在午夜前開始、第一筆呼叫在午夜後才出現
            if self.start_tod is not None and tod < self.start_tod - 43200:
                self._day = 1
        elif tod < self._last_tod - 43200:
            self._day += 1
        self._last_tod = tod
        abs_t = tod + 86400 * self._day
        if self.first_abs is None:
            self.first_abs, self.first_text = abs_t, c.time_text
        self.last_abs = max(self.last_abs, abs_t + c.duration)

        gpib = "488" in c.api or c.func.startswith("ib")
        name = self._resolve_gpib(c) if gpib else self._resolve_visa(c)
        api = c.api or ("NI-488.2" if gpib else "VISA")
        inst = self._inst(name or SYSTEM, api)
        if api not in inst.api.split(" + "):
            inst.api += " + " + api
        dur = c.duration

        direction = ""
        if c.func not in WRAPPERS:
            if c.func in VISA_WRITE or c.func in GPIB_WRITE:
                direction = "W"
            elif c.func in VISA_READ or c.func in GPIB_READ:
                direction = "R"
            elif c.buf_in is not None and re.search(r"(?i)write|wrt|send", c.func):
                direction = "W"
            elif c.buf_out is not None and re.search(r"(?i)read|receive|ibrd", c.func):
                direction = "R"

        text, nbytes = "", 0
        if direction == "W":
            data = c.buf_in or b""
            nbytes = c.buf_in_size or len(data)
            if nbytes > len(data):
                self.truncated_buffers += 1
            inst.writes += 1
            inst.bytes_out += nbytes
            inst.io_time += dur
            cmds = scpi.parse_message(data, self.normalize)
            inst.pending = []
            keys = []
            share = dur / len(cmds) if cmds else 0.0
            for cmd in cmds:
                st = inst.cmds.get(cmd.key)
                if st is None:
                    st = inst.cmds[cmd.key] = CmdStat(cmd.key, cmd.kind, cmd.is_query, seq)
                st.count += 1
                st.total += share
                st.max = max(st.max, share)
                st.raw.setdefault(cmd.raw)
                if len(st.args) < 500 or cmd.args in st.args:
                    st.args[cmd.args] += 1
                if not c.ok:
                    st.errors += 1
                if cmd.is_query:
                    inst.pending.append((st, seq))
                keys.append(cmd.key)
            inst.commands += len(cmds)
            if keys:
                self.event_cmds[seq] = keys
            text = scpi.shorten_block(scpi.decode(data).rstrip("\r\n\x00"), self.max_text) if scpi.is_mostly_text(data) \
                else "<binary %d bytes>" % nbytes
        elif direction == "R":
            data = c.buf_out or b""
            nbytes = c.buf_out_size or len(data)
            if nbytes > len(data):
                self.truncated_buffers += 1
            inst.reads += 1
            inst.bytes_in += nbytes
            inst.io_time += dur
            text = scpi.shorten_block(scpi.decode(data).rstrip("\r\n\x00"), self.max_text) if data else ""
            if inst.pending:
                n = len(inst.pending)
                pieces = text.split(";") if n > 1 and text.count(";") == n - 1 else None
                for i, (st, _) in enumerate(inst.pending):
                    st.total += dur / n
                    st.max = max(st.max, dur / n)
                    if not c.ok:
                        st.errors += 1
                    resp = pieces[i] if pieces else text
                    if resp and len(st.resp) < 3:
                        st.resp.setdefault(resp[:80])
                    if st.key == "*IDN?" and resp and c.ok:
                        inst.idn = resp[:120]
                if c.ok or not data:
                    inst.pending = []
        else:
            o = inst.others.setdefault(c.func, [0, 0, 0.0])
            o[0] += 1
            o[1] += 0 if c.ok else 1
            o[2] += dur
            vals = [v for k, v in c.params_in.items() if not k.startswith("&") and k not in ("vi", "ud", "Brd/Dev Name")]
            text = scpi.shorten_block(", ".join(vals), 160)

        if not c.ok:
            inst.errors += 1
            self.errors.append({"seq": seq, "t": round(abs_t - self.first_abs, 4), "time": c.time_text,
                                "inst": inst.idx, "func": c.func, "text": text,
                                "status": c.status_name or c.status_code, "desc": c.status_text,
                                "iberr": c.params_out.get("iberr", "")})

        self.events.append([seq, round(abs_t - self.first_abs, 4), c.time_text, c.pid, inst.idx, c.func,
                            direction, text, nbytes, round(dur * 1000, 2), 1 if c.ok else 0,
                            c.status_name, abs_t])

    # ------------------------------------------------------------------ 即時統計
    def live_totals(self) -> dict:
        insts = [i for i in self.instruments.values() if i.name != SYSTEM]
        return {
            "calls": self.n_calls,
            "writes": sum(i.writes for i in insts),
            "reads": sum(i.reads for i in insts),
            "commands": sum(i.commands for i in insts),
            "unique": sum(len(i.cmds) for i in insts),
            "instruments": len([i for i in insts if i.writes or i.reads]),
            "errors": len(self.errors),
        }

    # ------------------------------------------------------------------ 結果
    def result(self, source: str = "", max_timeline: int = 50000) -> dict:
        first = self.first_abs or 0.0
        insts = sorted(self.instruments.values(), key=lambda i: i.idx)

        def seg_of(abs_t: float) -> int:
            n = 0
            for t, _ in self.markers:
                if t <= abs_t:
                    n += 1
                else:
                    break
            return n

        commands, others = [], []
        for inst in insts:
            for st in inst.cmds.values():
                top = st.args.most_common(6)
                commands.append({
                    "inst": inst.idx, "key": st.key, "kind": st.kind, "query": st.is_query, "count": st.count,
                    "nargs": len([a for a in st.args if a]),
                    "args": [[a, n] for a, n in top if a],
                    "resp": list(st.resp), "raw": list(st.raw)[:4],
                    "total_ms": round(st.total * 1000, 2),
                    "avg_ms": round(st.total * 1000 / st.count, 3) if st.count else 0,
                    "max_ms": round(st.max * 1000, 2), "errors": st.errors, "first": st.first_seq,
                })
            for func, (n, err, t) in inst.others.items():
                others.append({"inst": inst.idx, "func": func, "count": int(n), "errors": int(err),
                               "total_ms": round(t * 1000, 2)})

        # 測項 (標記) 區段
        segments = []
        if self.markers:
            labels = ["(第一個標記之前)"] + [m[1] for m in self.markers]
            seg_data = [{"label": l, "start": None, "end": None, "writes": 0, "reads": 0, "commands": 0,
                         "errors": 0, "cmds": Counter(), "insts": set()} for l in labels]
            for ev in self.events:
                s = seg_data[seg_of(ev[12])]
                if s["start"] is None:
                    s["start"] = ev[1]
                s["end"] = ev[1]
                if ev[6] == "W":
                    s["writes"] += 1
                    s["insts"].add(ev[4])
                elif ev[6] == "R":
                    s["reads"] += 1
                    s["insts"].add(ev[4])
                if not ev[10]:
                    s["errors"] += 1
                for k in self.event_cmds.get(ev[0], ()):
                    s["cmds"][(ev[4], k)] += 1
                    s["commands"] += 1
            for i, s in enumerate(seg_data):
                if i == 0 and s["start"] is None:
                    continue
                segments.append({
                    "idx": i, "label": s["label"], "start": s["start"], "end": s["end"],
                    "writes": s["writes"], "reads": s["reads"], "commands": s["commands"],
                    "unique": len(s["cmds"]), "errors": s["errors"], "instruments": len(s["insts"]),
                    "cmds": [[k[0], k[1], n] for k, n in s["cmds"].most_common()],
                })

        real = [i for i in insts if i.name != SYSTEM]
        timeline = [ev[:12] + [seg_of(ev[12]) if self.markers else 0] for ev in self.events[:max_timeline]]
        return {
            "meta": {
                "source": source, "start_text": self.first_text,
                "duration_s": round(self.last_abs - first, 3) if self.first_abs is not None else 0,
                "normalize": self.normalize, "truncated_buffers": self.truncated_buffers,
                "processes": [{"pid": pid, "name": self.pid_names.get(pid, ""), "calls": n}
                              for pid, n in self.pid_calls.most_common()],
                "markers": [m[1] for m in self.markers],
            },
            "totals": {
                "api_calls": self.n_calls,
                "writes": sum(i.writes for i in real), "reads": sum(i.reads for i in real),
                "commands": sum(i.commands for i in real),
                "unique": sum(len(i.cmds) for i in real),
                "unique_global": len({k for i in real for k in i.cmds}),
                "instruments": len([i for i in real if i.writes or i.reads]),
                "errors": len(self.errors),
                "bytes_out": sum(i.bytes_out for i in real), "bytes_in": sum(i.bytes_in for i in real),
                "io_ms": round(sum(i.io_time for i in real) * 1000, 1),
            },
            "instruments": [{
                "id": i.idx, "name": i.name, "api": i.api, "idn": i.idn, "writes": i.writes, "reads": i.reads,
                "commands": i.commands, "unique": len(i.cmds), "bytes_out": i.bytes_out, "bytes_in": i.bytes_in,
                "io_ms": round(i.io_time * 1000, 1), "errors": i.errors,
                "other_calls": int(sum(o[0] for o in i.others.values())),
            } for i in insts],
            "commands": commands, "others": others, "errors": self.errors[:5000],
            "segments": segments, "timeline": timeline,
            "timeline_total": len(self.events),
        }
