"""圖形介面: 開始擷取 -> 執行 ATE -> 停止並產生報告。"""
from __future__ import annotations

import os
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from typing import Optional

from .analyze import SYSTEM
from .session import Session, analyze_file


class App:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.session: Optional[Session] = None
        self.busy = False
        root.title("ATE Bench - SCPI 指令分析")
        root.geometry("900x560")
        root.minsize(720, 420)

        top = ttk.Frame(root, padding=10)
        top.pack(fill="x")
        self.btn_start = ttk.Button(top, text="▶ 開始擷取", command=self.start)
        self.btn_start.pack(side="left")
        self.btn_stop = ttk.Button(top, text="■ 停止並產生報告", command=self.stop, state="disabled")
        self.btn_stop.pack(side="left", padx=6)
        ttk.Button(top, text="分析既有紀錄檔…", command=self.open_existing).pack(side="right")
        self.normalize = tk.BooleanVar(value=True)
        ttk.Checkbutton(top, text="歸併 SCPI 長短格式", variable=self.normalize).pack(side="right", padx=10)

        mk = ttk.Frame(root, padding=(10, 0))
        mk.pack(fill="x")
        ttk.Label(mk, text="測項名稱:").pack(side="left")
        self.mark_var = tk.StringVar()
        self.mark_entry = ttk.Entry(mk, textvariable=self.mark_var, width=36, state="disabled")
        self.mark_entry.pack(side="left", padx=6)
        self.mark_entry.bind("<Return>", lambda e: self.mark())
        self.btn_mark = ttk.Button(mk, text="標記測項開始", command=self.mark, state="disabled")
        self.btn_mark.pack(side="left")
        self.mark_info = ttk.Label(mk, text="(選用) 在每個測項開始前標記，報告會依測項分別統計", foreground="#666")
        self.mark_info.pack(side="left", padx=10)

        self.status = tk.StringVar(value="按「開始擷取」後再啟動 ATE 程式進行測試。")
        ttk.Label(root, textvariable=self.status, padding=(10, 8)).pack(fill="x")

        stats = ttk.Frame(root, padding=(10, 0))
        stats.pack(fill="x")
        self.vars = {}
        for i, (key, label) in enumerate([("elapsed", "經過時間"), ("instruments", "儀器數"), ("commands", "SCPI 指令"),
                                          ("unique", "不重複指令"), ("writes", "寫入"), ("reads", "讀取"),
                                          ("errors", "錯誤")]):
            box = ttk.LabelFrame(stats, text=label, padding=(10, 2))
            box.grid(row=0, column=i, padx=3, sticky="ew")
            stats.columnconfigure(i, weight=1)
            v = tk.StringVar(value="-")
            ttk.Label(box, textvariable=v, font=("Segoe UI", 15, "bold")).pack()
            self.vars[key] = v

        cols = [("name", "儀器 (資源)", 300), ("commands", "指令數", 70), ("unique", "不重複", 70), ("writes", "寫入", 60),
                ("reads", "讀取", 60), ("errors", "錯誤", 50), ("idn", "*IDN?", 240)]
        frame = ttk.Frame(root, padding=10)
        frame.pack(fill="both", expand=True)
        self.tree = ttk.Treeview(frame, columns=[c[0] for c in cols], show="headings")
        for key, label, width in cols:
            self.tree.heading(key, text=label)
            self.tree.column(key, width=width, anchor="w" if key in ("name", "idn") else "e")
        sb = ttk.Scrollbar(frame, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=sb.set)
        self.tree.pack(side="left", fill="both", expand=True)
        sb.pack(side="right", fill="y")

        root.protocol("WM_DELETE_WINDOW", self.on_close)

    # ------------------------------------------------------------------ 動作
    def _thread(self, work, done) -> None:
        self.busy = True

        def runner() -> None:
            try:
                result, error = work(), None
            except Exception as e:  # 在主執行緒顯示
                result, error = None, e
            self.root.after(0, lambda: self._finish(done, result, error))
        threading.Thread(target=runner, daemon=True).start()

    def _finish(self, done, result, error) -> None:
        self.busy = False
        done(result, error)

    def start(self) -> None:
        if self.busy or self.session:
            return
        self.btn_start.config(state="disabled")
        self.status.set("正在啟動 NI I/O Trace…")
        try:
            session = Session("captures", normalize=self.normalize.get())
        except Exception as e:
            self._start_done(None, e)
            return

        def work():
            session.start()
            return session
        self._thread(work, self._start_done)

    def _start_done(self, session, error) -> None:
        if error:
            self.btn_start.config(state="normal")
            self.status.set("啟動失敗。")
            messagebox.showerror("ATE Bench", str(error))
            return
        self.session = session
        self.tree.delete(*self.tree.get_children())
        self.btn_stop.config(state="normal")
        self.btn_mark.config(state="normal")
        self.mark_entry.config(state="normal")
        self.mark_info.config(text="(選用) 在每個測項開始前標記，報告會依測項分別統計")
        self.status.set("擷取中 — 現在可以啟動 ATE 程式。紀錄檔: " + session.log_path)
        self.tick()

    def tick(self) -> None:
        s = self.session
        if not s or self.busy:
            return
        s.poll()
        self.refresh()
        self.root.after(1000, self.tick)

    def refresh(self) -> None:
        s = self.session
        if not s:
            return
        t = s.analyzer.live_totals()
        e = int(s.elapsed())
        self.vars["elapsed"].set("%d:%02d:%02d" % (e // 3600, e % 3600 // 60, e % 60))
        for k in ("instruments", "commands", "unique", "writes", "reads", "errors"):
            self.vars[k].set("{:,}".format(t[k]))
        for inst in s.analyzer.instruments.values():
            if inst.name == SYSTEM or not (inst.writes or inst.reads or inst.errors):
                continue
            values = (inst.name, inst.commands, len(inst.cmds), inst.writes, inst.reads, inst.errors, inst.idn)
            iid = "i%d" % inst.idx
            if self.tree.exists(iid):
                self.tree.item(iid, values=values)
            else:
                self.tree.insert("", "end", iid=iid, values=values)

    def mark(self) -> None:
        s = self.session
        label = self.mark_var.get().strip()
        if not s or not label:
            return
        s.mark(label)
        self.mark_var.set("")
        self.mark_info.config(text="目前測項: %s (共 %d 個標記)" % (label, len(s.analyzer.markers)))

    def stop(self) -> None:
        s = self.session
        if not s or self.busy:
            return
        self.btn_stop.config(state="disabled")
        self.btn_mark.config(state="disabled")
        self.mark_entry.config(state="disabled")
        self.status.set("停止擷取並產生報告…")
        self._thread(s.stop, self._stop_done)

    def _stop_done(self, path, error) -> None:
        self.refresh()
        self.session = None
        self.btn_start.config(state="normal")
        if error:
            self.status.set("產生報告失敗。")
            messagebox.showerror("ATE Bench", str(error))
            return
        self.status.set("完成。報告: " + path)
        os.startfile(path)

    def open_existing(self) -> None:
        if self.busy or self.session:
            messagebox.showinfo("ATE Bench", "請先停止目前的擷取。")
            return
        path = filedialog.askopenfilename(title="選擇 I/O Trace XML 紀錄檔", filetypes=[("I/O Trace XML", "*.xml")])
        if not path:
            return
        self.status.set("分析中: " + path)
        norm = self.normalize.get()

        def done(result, error) -> None:
            if error:
                self.status.set("分析失敗。")
                messagebox.showerror("ATE Bench", str(error))
            else:
                self.status.set("完成。報告: " + result)
                os.startfile(result)
        self._thread(lambda: analyze_file(path, normalize=norm), done)

    def on_close(self) -> None:
        if self.session:
            if not messagebox.askyesno("ATE Bench", "擷取仍在進行，要停止並結束嗎？(不會產生報告)"):
                return
            try:
                self.session.trace.stop()
            except Exception:
                pass
        self.root.destroy()


def run() -> None:
    root = tk.Tk()
    App(root)
    root.mainloop()
