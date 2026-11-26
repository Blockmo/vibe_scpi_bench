"""測試用假儀器: 在本機開兩個 TCP socket (5025, 5026)，對任何查詢回應固定值。

用法: python tests/fake_instruments.py [存活秒數]
"""
import socket
import sys
import threading
import time


def serve(port: int) -> None:
    srv = socket.socket()
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", port))
    srv.listen(5)

    def handle(conn: socket.socket) -> None:
        buf = b""
        while True:
            try:
                data = conn.recv(4096)
            except OSError:
                break
            if not data:
                break
            buf += data
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                text = line.decode(errors="replace").strip()
                queries = [u for u in text.split(";") if "?" in u]
                if not queries:
                    continue
                if "NOREPLY" in text.upper():
                    continue  # 模擬逾時
                parts = ["FAKE,MODEL%d,SN001,1.0" % port if q.strip().upper().startswith("*IDN") else "+1.234560E+00"
                         for q in queries]
                conn.sendall((";".join(parts) + "\n").encode())

    while True:
        conn, _ = srv.accept()
        threading.Thread(target=handle, args=(conn,), daemon=True).start()


if __name__ == "__main__":
    for p in (5025, 5026):
        threading.Thread(target=serve, args=(p,), daemon=True).start()
    time.sleep(float(sys.argv[1]) if len(sys.argv) > 1 else 3600)
