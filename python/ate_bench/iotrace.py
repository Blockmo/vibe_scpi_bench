"""控制 NI I/O Trace 進行擷取。

NI I/O Trace 會攔截本機所有程序對 NI-VISA / NI-488.2 的呼叫，所以不需要知道
ATE 程式用哪個 GPIB 位址或哪種介面。這裡透過 NiSpyLog.dll 的 nispy_* 函式
(與 LabVIEW 的 I/O Trace VI 相同的介面) 啟動 / 停止擷取並直接寫成 XML 紀錄檔。
"""
from __future__ import annotations

import ctypes
import os
import subprocess
import time
from typing import Optional

LOG_XML = 3            # 0=.nitrace 1=.txt 2=.csv 3=.xml
WRITE_IMMEDIATE = 1    # 每筆呼叫立即寫檔 (可即時統計)
ERR_NOT_READY = -303203

_EXE_CANDIDATES = [
    r"%ProgramFiles(x86)%\National Instruments\NI IO Trace\NI IO Trace.exe",
    r"%ProgramFiles%\National Instruments\NI IO Trace\NI IO Trace.exe",
    r"%ProgramFiles(x86)%\National Instruments\NI Spy\NI Spy.exe",
]
_NO_WINDOW = 0x08000000


class IOTraceError(RuntimeError):
    pass


def find_exe() -> Optional[str]:
    for cand in _EXE_CANDIDATES:
        path = os.path.expandvars(cand)
        if os.path.isfile(path):
            return path
    return None


def is_running() -> bool:
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq NI IO Trace.exe", "/NH"],
                             capture_output=True, text=True, creationflags=_NO_WINDOW).stdout
    except OSError:
        return False
    return "NI IO Trace" in out


def process_name(pid: int) -> str:
    """PID -> 執行檔名稱 (程序已結束則回傳空字串)。"""
    k32 = ctypes.windll.kernel32
    k32.OpenProcess.restype = ctypes.c_void_p
    h = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return ""
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = ctypes.c_uint32(1024)
        if k32.QueryFullProcessImageNameW(ctypes.c_void_p(h), 0, buf, ctypes.byref(size)):
            return os.path.basename(buf.value)
        return ""
    finally:
        k32.CloseHandle(ctypes.c_void_p(h))


def _ensure_full_buffers() -> None:
    """I/O Trace 預設每個緩衝區只記 64 bytes，長指令會被截斷；改成大緩衝區模式。"""
    try:
        import winreg
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\National Instruments\NI Spy\Options") as k:
            try:
                cur = winreg.QueryValueEx(k, "Buffer Is Brief")[0]
            except OSError:
                cur = 1
            if cur != 0:
                winreg.SetValueEx(k, "Buffer Is Brief", 0, winreg.REG_DWORD, 0)
    except OSError:
        pass


class IOTrace:
    def __init__(self) -> None:
        try:
            self._dll = ctypes.CDLL("NiSpyLog.dll")
        except OSError as e:
            raise IOTraceError("找不到 NiSpyLog.dll，請確認已安裝 NI I/O Trace (隨 NI-VISA / NI-488.2 安裝)。") from e
        self._dll.nispy_StartSpying.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int]
        self._dll.nispy_StartSpying.restype = ctypes.c_int
        self._dll.nispy_StopSpying.restype = ctypes.c_int
        self._dll.nispy_CloseSpy.restype = ctypes.c_int
        self._launched = False
        self.capturing = False

    def start(self, log_path: str, timeout: float = 60.0) -> None:
        log_path = os.path.abspath(log_path)
        if not log_path.lower().endswith(".xml"):
            raise IOTraceError("紀錄檔副檔名必須是 .xml")
        os.makedirs(os.path.dirname(log_path), exist_ok=True)
        if os.path.exists(log_path):
            os.unlink(log_path)
        if not is_running():
            exe = find_exe()
            if not exe:
                raise IOTraceError("找不到 NI IO Trace.exe，請先安裝 NI I/O Trace。")
            _ensure_full_buffers()
            si = subprocess.STARTUPINFO()
            si.dwFlags = subprocess.STARTF_USESHOWWINDOW
            si.wShowWindow = 7  # 最小化且不搶焦點
            subprocess.Popen([exe], startupinfo=si)
            self._launched = True
        deadline = time.time() + timeout
        rc = ERR_NOT_READY
        while time.time() < deadline:
            rc = self._dll.nispy_StartSpying(LOG_XML, log_path.encode("mbcs"), WRITE_IMMEDIATE)
            if rc != ERR_NOT_READY:
                break
            time.sleep(0.5)
        if rc != 0:
            raise IOTraceError("無法啟動 NI I/O Trace 擷取 (錯誤碼 %d)。若 I/O Trace 已在擷取中，請先停止它再試。" % rc)
        self.capturing = True

    def stop(self) -> None:
        if self.capturing:
            self._dll.nispy_StopSpying()
            self.capturing = False
            time.sleep(1.0)  # 等 I/O Trace 把紀錄檔寫完
        if self._launched:
            self._dll.nispy_CloseSpy()
            self._launched = False
