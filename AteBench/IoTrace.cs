using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace AteBench
{
    public class IoTraceException : Exception
    {
        public IoTraceException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// 控制 NI I/O Trace 進行擷取。
    /// NI I/O Trace 會攔截本機所有程序對 NI-VISA / NI-488.2 的呼叫，所以不需要知道 ATE 程式
    /// 用哪個 GPIB 位址或哪種介面。這裡透過 NiSpyLog.dll 的 nispy_* 函式 (與 LabVIEW 的
    /// I/O Trace VI 相同的介面) 啟動 / 停止擷取並直接寫成 XML 紀錄檔。
    /// </summary>
    public sealed class IoTrace
    {
        const int LogXml = 3;             // 0=.nitrace 1=.txt 2=.csv 3=.xml
        const int WriteImmediate = 1;     // 每筆呼叫立即寫檔 (可即時統計)
        const int ErrNotReady = -303203;

        [DllImport("NiSpyLog.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nispy_StartSpying(int fileType, byte[] logPath, int writeMode);

        [DllImport("NiSpyLog.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nispy_StopSpying();

        [DllImport("NiSpyLog.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int nispy_CloseSpy();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        bool _launched;
        public bool Capturing { get; private set; }

        public static string FindExe()
        {
            string[] roots = {
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetEnvironmentVariable("ProgramFiles")
            };
            string[] rels = { @"National Instruments\NI IO Trace\NI IO Trace.exe", @"National Instruments\NI Spy\NI Spy.exe" };
            foreach (string rel in rels)
                foreach (string root in roots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string path = Path.Combine(root, rel);
                    if (File.Exists(path)) return path;
                }
            return null;
        }

        public static bool IsRunning()
        {
            Process[] ps = Process.GetProcessesByName("NI IO Trace");
            foreach (Process p in ps) p.Dispose();
            return ps.Length > 0;
        }

        /// <summary>PID -> 執行檔名稱 (程序已結束則回傳空字串)。</summary>
        public static string ProcessName(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                uint size = 1024;
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? Path.GetFileName(sb.ToString()) : "";
            }
            finally { CloseHandle(h); }
        }

        /// <summary>I/O Trace 預設每個緩衝區只記 64 bytes，長指令會被截斷；改成大緩衝區模式。</summary>
        static void EnsureFullBuffers()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\National Instruments\NI Spy\Options"))
                {
                    object cur = k.GetValue("Buffer Is Brief");
                    if (!(cur is int) || (int)cur != 0) k.SetValue("Buffer Is Brief", 0, RegistryValueKind.DWord);
                }
            }
            catch (Exception) { /* 設定失敗不影響擷取，只是長指令可能被截斷 */ }
        }

        public void Start(string logPath, double timeoutSeconds = 60)
        {
            logPath = Path.GetFullPath(logPath);
            if (!logPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                throw new IoTraceException("紀錄檔副檔名必須是 .xml");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            if (File.Exists(logPath)) File.Delete(logPath);

            if (!IsRunning())
            {
                string exe = FindExe();
                if (exe == null) throw new IoTraceException("找不到 NI IO Trace.exe，請先安裝 NI I/O Trace (隨 NI-VISA / NI-488.2 安裝)。");
                EnsureFullBuffers();
                var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized };
                using (Process.Start(psi)) { }
                _launched = true;
            }

            // nispy_StartSpying 的路徑參數是 ANSI 字串
            byte[] ansi = Encoding.Default.GetBytes(logPath + "\0");
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            int rc = ErrNotReady;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    rc = nispy_StartSpying(LogXml, ansi, WriteImmediate);
                    if (rc != ErrNotReady) break;      // I/O Trace 剛啟動時尚未就緒，重試
                    Thread.Sleep(500);
                }
            }
            catch (DllNotFoundException e)
            {
                throw new IoTraceException("找不到 NiSpyLog.dll，請確認已安裝 NI I/O Trace (隨 NI-VISA / NI-488.2 安裝)。", e);
            }
            catch (BadImageFormatException e)
            {
                throw new IoTraceException("NiSpyLog.dll 與本程式的位元數不符。", e);
            }
            if (rc != 0)
                throw new IoTraceException("無法啟動 NI I/O Trace 擷取 (錯誤碼 " + rc + ")。若 I/O Trace 已在擷取中，請先停止它再試。");
            Capturing = true;
        }

        public void Stop()
        {
            if (Capturing)
            {
                nispy_StopSpying();
                Capturing = false;
                Thread.Sleep(1000);     // 等 I/O Trace 把紀錄檔寫完
            }
            if (_launched)
            {
                nispy_CloseSpy();
                _launched = false;
            }
        }
    }
}
