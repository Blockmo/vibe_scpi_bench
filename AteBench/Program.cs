using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AteBench
{
    /// <summary>
    /// 進入點。
    ///   AteBench.exe                                  開啟圖形介面
    ///   AteBench.exe capture [-o 資料夾]              主控台擷取 (按 Enter 停止)
    ///   AteBench.exe capture --run "ATE.exe 參數"     啟動 ATE 程式並在它結束時自動停止
    ///   AteBench.exe analyze trace.xml [-o 資料夾]    分析既有的 I/O Trace XML 紀錄檔
    ///   AteBench.exe selftest                         SCPI 拆解自我測試
    /// 選項: --no-normalize 不歸併 SCPI 長短格式；--no-open 完成後不自動開啟報告
    /// </summary>
    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] static extern bool AllocConsole();
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);

        static bool HasHandle(int std)
        {
            IntPtr h = GetStdHandle(std);
            return h != IntPtr.Zero && h != new IntPtr(-1);
        }

        /// <summary>本程式是視窗程式，命令列模式需自行接上主控台。</summary>
        static void EnsureConsole(bool needInput)
        {
            if (needInput && !HasHandle(-10)) { AllocConsole(); return; }   // 需要鍵盤輸入: 開獨立主控台視窗
            if (!HasHandle(-11) && !AttachConsole(-1)) AllocConsole();
        }

        sealed class Options
        {
            public string Command, Out, Run, Xml;
            public bool NoNormalize, NoOpen;
        }

        static Options Parse(string[] args)
        {
            var o = new Options { Command = args[0].ToLowerInvariant() };
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "-o" || a == "--out") && i + 1 < args.Length) o.Out = args[++i];
                else if (a == "--run" && i + 1 < args.Length) o.Run = args[++i];
                else if (a == "--no-normalize") o.NoNormalize = true;
                else if (a == "--no-open") o.NoOpen = true;
                else if (!a.StartsWith("-") && o.Xml == null) o.Xml = a;
                else throw new ArgumentException("無法辨識的參數: " + a);
            }
            return o;
        }

        const string Usage =
            "用法:\r\n" +
            "  AteBench.exe                                開啟圖形介面\r\n" +
            "  AteBench.exe capture [-o 資料夾]            主控台擷取 (按 Enter 停止)\r\n" +
            "  AteBench.exe capture --run \"ATE.exe 參數\"   啟動 ATE 程式，程式結束時自動停止並產生報告\r\n" +
            "  AteBench.exe analyze trace.xml [-o 資料夾]  分析既有的 I/O Trace XML 紀錄檔\r\n" +
            "選項: --no-normalize 不歸併 SCPI 長短格式；--no-open 完成後不自動開啟報告";

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                var app = new System.Windows.Application();
                ThemeManager.Initialize(app);
                app.Run(new MainWindow());
                return 0;
            }

            Options o;
            try { o = Parse(args); }
            catch (ArgumentException e)
            {
                EnsureConsole(false);
                Console.Error.WriteLine(e.Message + "\r\n" + Usage);
                return 1;
            }
            try
            {
                switch (o.Command)
                {
                    case "capture":
                        EnsureConsole(o.Run == null);
                        return Capture(o);
                    case "analyze":
                        EnsureConsole(false);
                        if (o.Xml == null) { Console.Error.WriteLine(Usage); return 1; }
                        string path = Session.AnalyzeFile(o.Xml, o.Out, !o.NoNormalize);
                        Console.WriteLine("報告: " + path);
                        if (!o.NoOpen) Process.Start(path);
                        return 0;
                    case "selftest":
                        EnsureConsole(false);
                        return SelfTest();
                    default:
                        EnsureConsole(false);
                        Console.WriteLine(Usage);
                        return o.Command == "-h" || o.Command == "--help" || o.Command == "/?" ? 0 : 1;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("錯誤: " + e.Message);
                return 2;
            }
        }

        static int Capture(Options o)
        {
            var s = new Session(o.Out, !o.NoNormalize);
            Console.WriteLine("啟動 NI I/O Trace 擷取中…");
            s.Start();
            Console.WriteLine("擷取中，紀錄檔: " + s.LogPath);

            var done = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (sender, e) => { e.Cancel = true; done.Set(); };
            Process proc = null;
            if (o.Run != null)
            {
                proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + o.Run) { UseShellExecute = false });
                Console.WriteLine("已啟動: " + o.Run);
            }
            else
            {
                Console.WriteLine("請開始執行 ATE 程式。測試結束後按 Enter 停止擷取。");
                var reader = new Thread(() =>
                {
                    Console.ReadLine();
                    done.Set();
                }) { IsBackground = true };
                reader.Start();
            }

            string last = null;
            while (!done.IsSet)
            {
                s.Poll();
                LiveTotals t = s.Analyzer.Live();
                string line = string.Format("  儀器 {0}  指令 {1} (不重複 {2})  寫 {3} / 讀 {4}  錯誤 {5}",
                    t.Instruments, t.Commands, t.Unique, t.Writes, t.Reads, t.Errors);
                if (line != last)
                {
                    Console.WriteLine(string.Format("{0,6:F0}s", s.Elapsed) + line);
                    last = line;
                }
                if (proc != null && proc.HasExited) break;
                done.Wait(1000);
            }

            Console.WriteLine("停止擷取，產生報告…");
            string report = s.Stop();
            Console.WriteLine("報告: " + report);
            if (!o.NoOpen) Process.Start(report);
            return 0;
        }

        // ------------------------------------------------------------------ 自我測試
        static int SelfTest()
        {
            int failed = 0, total = 0;
            Action<string, string, string> eq = (name, actual, expected) =>
            {
                total++;
                if (actual == expected) return;
                failed++;
                Console.WriteLine("FAIL " + name + ": 得到 [" + actual + "]，預期 [" + expected + "]");
            };
            Func<byte[], bool, string> keys = (data, norm) => string.Join(",", Scpi.ParseMessage(data, norm).Select(c => c.Key));
            Func<string, byte[]> b = s => Encoding.GetEncoding(28591).GetBytes(s);

            var forms = new Dictionary<string, string> {
                { "VOLTage", "VOLT" }, { "POWer", "POW" }, { "FREQuency", "FREQ" }, { "SWEep", "SWE" },
                { "TIME", "TIME" }, { "OUTPut2", "OUTP2" }, { "dc", "DC" } };
            foreach (var f in forms) eq("short_form " + f.Key, Scpi.ShortForm(f.Key), f.Value);

            eq("idn", keys(b("*IDN?\n"), true), "*IDN?");
            eq("long form", keys(b("CONFigure:VOLTage:DC 10,0.001\n"), true), "CONF:VOLT:DC");
            eq("leading colon", keys(b(":meas:volt?\r\n"), true), "MEAS:VOLT?");
            eq("compound", keys(b("SOUR:VOLT 3.3;CURR 0.5;:OUTP ON\n"), true), "SOUR:VOLT,SOUR:CURR,OUTP");
            eq("compound+common", keys(b("SENS:VOLT:DC:NPLC 10;RANG 10;*OPC;RES 1\n"), true),
               "SENS:VOLT:DC:NPLC,SENS:VOLT:DC:RANG,*OPC,SENS:VOLT:DC:RES");
            eq("multiline", keys(b("VOLT 1\nCURR 2\n"), true), "VOLT,CURR");
            eq("no normalize", keys(b("VOLTage 1\n"), false), "VOLTAGE");
            eq("non-scpi", keys(b("F1R2T4\n"), true), "F1R2T4");

            var q = Scpi.ParseMessage(b("MEAS:VOLT? CH1\n"))[0];
            eq("query flag", q.IsQuery.ToString(), "True");
            eq("query args", q.Args, "CH1");

            var quoted = Scpi.ParseMessage(b("DISP:TEXT 'a;b';:OUTP ON\n"));
            eq("quoted keys", string.Join(",", quoted.Select(c => c.Key)), "DISP:TEXT,OUTP");
            eq("quoted args", quoted[0].Args, "'a;b'");

            var blockData = new List<byte>(b("DATA:ARB #216"));
            for (int i = 0; i < 16; i++) blockData.Add((byte)i);
            blockData.Add((byte)'\n');
            var block = Scpi.ParseMessage(blockData.ToArray());
            eq("block count", block.Count.ToString(), "1");
            eq("block key", block[0].Key, "DATA:ARB");
            eq("block args", block[0].Args, "#<block 16 bytes>");

            var bin = new byte[30];
            for (int i = 0; i < 30; i++) bin[i] = (byte)i;
            eq("binary", Scpi.ParseMessage(bin)[0].Kind, "binary");

            Console.WriteLine(failed == 0 ? "OK: " + total + " 項測試全部通過" : failed + " / " + total + " 項測試失敗");
            return failed == 0 ? 0 : 1;
        }
    }
}
