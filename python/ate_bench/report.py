"""輸出報告: report.html (互動式)、commands.csv、instruments.csv、timeline.csv、report.xlsx。"""
from __future__ import annotations

import csv
import json
import os
from typing import List

from .analyze import Analyzer

_TEMPLATE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "report_template.html")


def _write_csv(path: str, header: List[str], rows: List[list]) -> None:
    with open(path, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f)
        w.writerow(header)
        w.writerows(rows)


def generate(analyzer: Analyzer, out_dir: str, source: str = "", captured: str = "",
             max_timeline: int = 50000) -> str:
    os.makedirs(out_dir, exist_ok=True)
    data = analyzer.result(source=source, max_timeline=max_timeline)
    data["meta"]["captured"] = captured
    names = {i["id"]: i["name"] for i in data["instruments"]}

    inst_head = ["儀器", "*IDN?", "介面", "指令數", "不重複指令", "寫入", "讀取", "送出bytes", "接收bytes", "I/O時間ms", "其他API", "錯誤"]
    inst_rows = [[i["name"], i["idn"], i["api"], i["commands"], i["unique"], i["writes"], i["reads"], i["bytes_out"],
                  i["bytes_in"], i["io_ms"], i["other_calls"], i["errors"]] for i in data["instruments"]]
    cmd_head = ["儀器", "指令", "類型", "次數", "參數種類數", "參數範例", "回應範例", "實際寫法", "總時間ms", "平均ms", "最大ms", "錯誤"]
    cmd_rows = [[names[c["inst"]], c["key"], "查詢" if c["query"] else "設定", c["count"], c["nargs"],
                 " | ".join("%s (x%d)" % (a, n) for a, n in c["args"]), " | ".join(c["resp"]), " | ".join(c["raw"]),
                 c["total_ms"], c["avg_ms"], c["max_ms"], c["errors"]]
                for c in sorted(data["commands"], key=lambda c: (c["inst"], -c["count"]))]
    tl_head = ["#", "時間s", "時刻", "PID", "儀器", "函式", "方向", "內容", "bytes", "耗時ms", "成功", "狀態"]
    tl_rows = [[e[0], e[1], e[2], e[3], names.get(e[4], ""), e[5], e[6], e[7], e[8], e[9], e[10], e[11]]
               for e in analyzer.events]

    _write_csv(os.path.join(out_dir, "instruments.csv"), inst_head, inst_rows)
    _write_csv(os.path.join(out_dir, "commands.csv"), cmd_head, cmd_rows)
    _write_csv(os.path.join(out_dir, "timeline.csv"), tl_head, tl_rows)

    try:  # Excel 為選用輸出
        from openpyxl import Workbook
        wb = Workbook(write_only=True)
        for title, head, rows in (("儀器", inst_head, inst_rows), ("指令", cmd_head, cmd_rows),
                                  ("時間軸", tl_head, tl_rows[:200000])):
            ws = wb.create_sheet(title)
            ws.append(head)
            for r in rows:
                ws.append([("".join(ch for ch in v if ch >= " " or ch == "\t") if isinstance(v, str) else v) for v in r])
        wb.save(os.path.join(out_dir, "report.xlsx"))
    except Exception:
        pass

    with open(_TEMPLATE, encoding="utf-8") as f:
        tpl = f.read()
    payload = json.dumps(data, ensure_ascii=False, separators=(",", ":")).replace("</", "<\\/")
    html_path = os.path.join(out_dir, "report.html")
    with open(html_path, "w", encoding="utf-8") as f:
        f.write(tpl.replace("/*DATA*/", payload, 1))
    return html_path
