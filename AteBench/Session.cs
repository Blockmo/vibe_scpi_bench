using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace AteBench
{
    /// <summary>一次擷取工作階段: 啟動擷取 -> 即時統計 -> 停止 -> 產生報告。GUI 與 CLI 共用。</summary>
    public sealed class Session
    {
        public const string SessionFile = "session.json";

        public readonly Analyzer Analyzer;
        public readonly IoTrace Trace = new IoTrace();
        public DateTime? StartTime { get; private set; }
        public string OutDir { get; private set; }
        public string LogPath { get; private set; }

        readonly string _baseDir;
        TraceReader _reader;

        public Session(string baseDir = null, bool normalize = true)
        {
            _baseDir = baseDir ?? DefaultBaseDir();
            Analyzer = new Analyzer(normalize);
        }

        /// <summary>預設輸出位置: 執行檔旁的 captures；若該處不可寫入則改用「文件\ATE Bench\captures」。</summary>
        public static string DefaultBaseDir()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures");
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write_test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch (Exception)
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ATE Bench", "captures");
            }
        }

        static double Tod(DateTime t)
        {
            return t.TimeOfDay.TotalSeconds;
        }

        public void Start()
        {
            OutDir = Path.GetFullPath(Path.Combine(_baseDir, DateTime.Now.ToString("yyyyMMdd_HHmmss")));
            LogPath = Path.Combine(OutDir, "trace.xml");
            Trace.Start(LogPath);
            StartTime = DateTime.Now;
            Analyzer.StartTod = Tod(StartTime.Value);
            _reader = new TraceReader(LogPath);
        }

        /// <summary>讀入新的呼叫紀錄，回傳本次新增筆數。</summary>
        public int Poll()
        {
            if (_reader == null) return 0;
            List<Call> calls = _reader.ReadNew();
            foreach (Call c in calls)
            {
                if (!Analyzer.PidNames.ContainsKey(c.Pid)) Analyzer.PidNames[c.Pid] = IoTrace.ProcessName(c.Pid);
                Analyzer.Feed(c);
            }
            return calls.Count;
        }

        public double Elapsed
        {
            get { return StartTime == null ? 0 : (DateTime.Now - StartTime.Value).TotalSeconds; }
        }

        /// <summary>停止擷取並產生報告，回傳 report.html 路徑。</summary>
        public string Stop()
        {
            Trace.Stop();
            Poll();
            string captured = StartTime != null ? StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "";
            var meta = new Dictionary<string, object> {
                { "captured", captured },
                { "start_tod", Analyzer.StartTod },
                { "markers", Analyzer.Markers.Select(m => new object[] { m.Key, m.Value }).ToArray() },
                { "pid_names", Analyzer.PidNames.ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => (object)p.Value) }
            };
            File.WriteAllText(Path.Combine(OutDir, SessionFile), new JavaScriptSerializer().Serialize(meta), new UTF8Encoding(false));
            return Report.Generate(Analyzer, OutDir, LogPath, captured);
        }

        /// <summary>分析既有的 I/O Trace XML 紀錄檔 (本工具擷取的，或從 NI I/O Trace 手動記錄的)。</summary>
        public static string AnalyzeFile(string xmlPath, string outDir = null, bool normalize = true)
        {
            Analyzer unused;
            return AnalyzeFile(xmlPath, outDir, normalize, out unused);
        }

        /// <summary>同上，並回傳分析結果供介面顯示。</summary>
        public static string AnalyzeFile(string xmlPath, string outDir, bool normalize, out Analyzer analyzer)
        {
            xmlPath = Path.GetFullPath(xmlPath);
            if (!File.Exists(xmlPath)) throw new FileNotFoundException("找不到紀錄檔: " + xmlPath);
            outDir = outDir ?? Path.GetDirectoryName(xmlPath);
            var a = analyzer = new Analyzer(normalize);
            string captured = "";

            string metaPath = Path.Combine(Path.GetDirectoryName(xmlPath), SessionFile);
            if (File.Exists(metaPath))
            {
                try
                {
                    var meta = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(metaPath, Encoding.UTF8));
                    object v;
                    if (meta.TryGetValue("captured", out v) && v != null) captured = v.ToString();
                    if (meta.TryGetValue("start_tod", out v) && v != null) a.StartTod = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                    if (meta.TryGetValue("pid_names", out v) && v is IDictionary<string, object>)
                        foreach (var p in (IDictionary<string, object>)v)
                            a.PidNames[int.Parse(p.Key, CultureInfo.InvariantCulture)] = Convert.ToString(p.Value);
                    if (meta.TryGetValue("markers", out v) && v is IEnumerable)
                        foreach (object m in (IEnumerable)v)
                        {
                            var pair = ((IEnumerable)m).Cast<object>().ToArray();
                            a.AddMarker(Convert.ToDouble(pair[0], CultureInfo.InvariantCulture), Convert.ToString(pair[1]));
                        }
                }
                catch (Exception) { /* session.json 損毀時仍可分析，只是沒有測項標記與程序名稱 */ }
            }

            foreach (Call c in new TraceReader(xmlPath).ReadNew()) a.Feed(c);
            return Report.Generate(a, outDir, xmlPath, captured);
        }
    }
}
