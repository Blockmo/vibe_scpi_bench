using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace AteBench
{
    /// <summary>輸出報告: report.html (互動式)、commands.csv、instruments.csv、timeline.csv、report.xlsx。</summary>
    public static class Report
    {
        const string BeforeFirstMarker = "(第一個標記之前)";

        static string Csv(object v)
        {
            string s = v == null ? "" : v is bool ? ((bool)v ? "1" : "0") : Convert.ToString(v, CultureInfo.InvariantCulture);
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        static void WriteCsv(string path, IList<string> header, IEnumerable<object[]> rows)
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))   // BOM 讓 Excel 正確辨識中文
            {
                w.Write(string.Join(",", header.Select(h => Csv(h))) + "\r\n");
                foreach (object[] r in rows) w.Write(string.Join(",", r.Select(Csv)) + "\r\n");
            }
        }

        static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        static double R(double v, int digits)
        {
            return Math.Round(v, digits);
        }

        /// <summary>組出內嵌到 report.html 的資料 (欄位名稱與報告範本的 JavaScript 對應)。</summary>
        public static Dictionary<string, object> BuildData(Analyzer a, string source, string captured, int maxTimeline)
        {
            double first = a.FirstAbs ?? 0.0;
            var real = a.Instruments.Where(i => i.Name != Analyzer.SystemName).ToList();

            var commands = new List<object>();
            var others = new List<object>();
            foreach (Instrument inst in a.Instruments)
            {
                foreach (CmdStat st in inst.CmdList)
                {
                    commands.Add(Obj(
                        "inst", inst.Idx, "key", st.Key, "kind", st.Kind, "query", st.IsQuery, "count", st.Count,
                        "nargs", st.ArgOrder.Count(x => x.Length > 0),
                        "args", Analyzer.TopArgs(st, 6).Select(p => new object[] { p.Key, p.Value }).ToArray(),
                        "resp", st.Resp.ToArray(), "raw", st.Raw.Take(4).ToArray(),
                        "total_ms", R(st.Total * 1000, 2),
                        "avg_ms", st.Count > 0 ? R(st.Total * 1000 / st.Count, 3) : 0.0,
                        "max_ms", R(st.Max * 1000, 2), "errors", st.Errors, "first", st.FirstSeq));
                }
                foreach (OtherStat o in inst.OtherList)
                    others.Add(Obj("inst", inst.Idx, "func", o.Func, "count", o.Count, "errors", o.Errors,
                                   "total_ms", R(o.Time * 1000, 2)));
            }

            // 測項 (標記) 區段
            var segments = new List<object>();
            if (a.Markers.Count > 0)
            {
                int nseg = a.Markers.Count + 1;
                var start = new double?[nseg];
                var end = new double?[nseg];
                var writes = new int[nseg];
                var reads = new int[nseg];
                var ncmd = new int[nseg];
                var errs = new int[nseg];
                var insts = new HashSet<int>[nseg];
                var cmdOrder = new List<KeyValuePair<int, string>>[nseg];
                var cmdCount = new Dictionary<string, int>[nseg];
                for (int i = 0; i < nseg; i++)
                {
                    insts[i] = new HashSet<int>();
                    cmdOrder[i] = new List<KeyValuePair<int, string>>();
                    cmdCount[i] = new Dictionary<string, int>();
                }
                foreach (TraceEvent ev in a.Events)
                {
                    int s = a.SegmentOf(ev.AbsT);
                    if (start[s] == null) start[s] = ev.T;
                    end[s] = ev.T;
                    if (ev.Dir == "W") { writes[s]++; insts[s].Add(ev.Inst); }
                    else if (ev.Dir == "R") { reads[s]++; insts[s].Add(ev.Inst); }
                    if (!ev.Ok) errs[s]++;
                    if (ev.Cmds == null) continue;
                    foreach (string k in ev.Cmds)
                    {
                        string ck = ev.Inst + "\n" + k;
                        int n;
                        if (cmdCount[s].TryGetValue(ck, out n)) cmdCount[s][ck] = n + 1;
                        else
                        {
                            cmdCount[s][ck] = 1;
                            cmdOrder[s].Add(new KeyValuePair<int, string>(ev.Inst, k));
                        }
                        ncmd[s]++;
                    }
                }
                for (int i = 0; i < nseg; i++)
                {
                    if (i == 0 && start[0] == null) continue;
                    int si = i;
                    segments.Add(Obj(
                        "idx", i, "label", i == 0 ? BeforeFirstMarker : a.Markers[i - 1].Value,
                        "start", start[i], "end", end[i], "writes", writes[i], "reads", reads[i],
                        "commands", ncmd[i], "unique", cmdOrder[i].Count, "errors", errs[i], "instruments", insts[i].Count,
                        "cmds", cmdOrder[i].Select(p => new object[] { p.Key, p.Value, cmdCount[si][p.Key + "\n" + p.Value] })
                                           .OrderByDescending(x => (int)x[2]).ToArray()));
                }
            }

            bool hasMarkers = a.Markers.Count > 0;
            var timeline = a.Events.Take(maxTimeline).Select(e => new object[] {
                e.Seq, e.T, e.Time, e.Pid, e.Inst, e.Func, e.Dir, e.Text, e.Bytes, e.DurMs, e.Ok ? 1 : 0, e.Status,
                hasMarkers ? a.SegmentOf(e.AbsT) : 0 }).ToArray();

            return Obj(
                "meta", Obj(
                    "source", source, "captured", captured, "start_text", a.FirstText,
                    "duration_s", a.FirstAbs != null ? R(a.LastAbs - first, 3) : 0.0,
                    "normalize", a.Normalize, "truncated_buffers", a.TruncatedBuffers,
                    "processes", a.PidOrder.OrderByDescending(p => a.PidCalls[p]).Select(p => Obj(
                        "pid", p, "name", a.PidNames.ContainsKey(p) ? a.PidNames[p] : "", "calls", a.PidCalls[p])).ToArray(),
                    "markers", a.Markers.Select(m => m.Value).ToArray()),
                "totals", Obj(
                    "api_calls", a.NCalls,
                    "writes", real.Sum(i => i.Writes), "reads", real.Sum(i => i.Reads),
                    "commands", real.Sum(i => i.Commands), "unique", real.Sum(i => i.Cmds.Count),
                    "unique_global", real.SelectMany(i => i.Cmds.Keys).Distinct().Count(),
                    "instruments", real.Count(i => i.Writes > 0 || i.Reads > 0),
                    "errors", a.Errors.Count,
                    "bytes_out", real.Sum(i => i.BytesOut), "bytes_in", real.Sum(i => i.BytesIn),
                    "io_ms", R(real.Sum(i => i.IoTime) * 1000, 1)),
                "instruments", a.Instruments.Select(i => Obj(
                    "id", i.Idx, "name", i.Name, "api", i.Api, "idn", i.Idn, "writes", i.Writes, "reads", i.Reads,
                    "commands", i.Commands, "unique", i.Cmds.Count, "bytes_out", i.BytesOut, "bytes_in", i.BytesIn,
                    "io_ms", R(i.IoTime * 1000, 1), "errors", i.Errors,
                    "other_calls", i.OtherList.Sum(o => o.Count))).ToArray(),
                "commands", commands, "others", others,
                "errors", a.Errors.Take(5000).Select(e => Obj(
                    "seq", e.Seq, "t", e.T, "time", e.Time, "inst", e.Inst, "func", e.Func, "text", e.Text,
                    "status", e.Status, "desc", e.Desc, "iberr", e.Iberr)).ToArray(),
                "segments", segments, "timeline", timeline, "timeline_total", a.Events.Count);
        }

        public static string Generate(Analyzer a, string outDir, string source = "", string captured = "", int maxTimeline = 50000)
        {
            Directory.CreateDirectory(outDir);
            var names = a.Instruments.ToDictionary(i => i.Idx, i => i.Name);

            string[] instHead = { "儀器", "*IDN?", "介面", "指令數", "不重複指令", "寫入", "讀取", "送出bytes", "接收bytes", "I/O時間ms", "其他API", "錯誤" };
            var instRows = a.Instruments.Select(i => new object[] {
                i.Name, i.Idn, i.Api, i.Commands, i.Cmds.Count, i.Writes, i.Reads, i.BytesOut, i.BytesIn,
                R(i.IoTime * 1000, 1), i.OtherList.Sum(o => o.Count), i.Errors }).ToList();

            string[] cmdHead = { "儀器", "指令", "類型", "次數", "參數種類數", "參數範例", "回應範例", "實際寫法", "總時間ms", "平均ms", "最大ms", "錯誤" };
            var cmdRows = new List<object[]>();
            foreach (Instrument inst in a.Instruments)
                foreach (CmdStat st in inst.CmdList.OrderByDescending(c => c.Count))
                    cmdRows.Add(new object[] {
                        inst.Name, st.Key, st.IsQuery ? "查詢" : "設定", st.Count, st.ArgOrder.Count(x => x.Length > 0),
                        string.Join(" | ", Analyzer.TopArgs(st, 6).Select(p => p.Key + " (x" + p.Value + ")")),
                        string.Join(" | ", st.Resp), string.Join(" | ", st.Raw.Take(4)),
                        R(st.Total * 1000, 2), st.Count > 0 ? R(st.Total * 1000 / st.Count, 3) : 0.0, R(st.Max * 1000, 2), st.Errors });

            string[] tlHead = { "#", "時間s", "時刻", "PID", "儀器", "函式", "方向", "內容", "bytes", "耗時ms", "成功", "狀態" };
            IEnumerable<object[]> tlRows = a.Events.Select(e => new object[] {
                e.Seq, e.T, e.Time, e.Pid, names[e.Inst], e.Func, e.Dir, e.Text, e.Bytes, e.DurMs, e.Ok ? 1 : 0, e.Status });

            WriteCsv(Path.Combine(outDir, "instruments.csv"), instHead, instRows);
            WriteCsv(Path.Combine(outDir, "commands.csv"), cmdHead, cmdRows);
            WriteCsv(Path.Combine(outDir, "timeline.csv"), tlHead, tlRows);

            try   // Excel 為附加輸出，失敗 (例如檔案被 Excel 開啟中) 不影響報告
            {
                Xlsx.Write(Path.Combine(outDir, "report.xlsx"), new[] {
                    new Xlsx.Sheet { Name = "儀器", Header = instHead, Rows = instRows },
                    new Xlsx.Sheet { Name = "指令", Header = cmdHead, Rows = cmdRows },
                    new Xlsx.Sheet { Name = "時間軸", Header = tlHead, Rows = tlRows.Take(1000000) } });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            string template;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("report_template.html"))
            using (var r = new StreamReader(s, Encoding.UTF8))
                template = r.ReadToEnd();

            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
            string payload = json.Serialize(BuildData(a, source, captured, maxTimeline)).Replace("</", "<\\/");
            int at = template.IndexOf("/*DATA*/", StringComparison.Ordinal);
            string html = template.Substring(0, at) + payload + template.Substring(at + "/*DATA*/".Length);
            // 報告預設採用 ATE Bench 目前的主題；使用者在報告裡切換後由瀏覽器記住
            html = html.Replace("/*THEME*/\"\"", ThemeManager.Preferred == AppTheme.Light ? "\"light\"" : "\"dark\"");
            string htmlPath = Path.Combine(outDir, "report.html");
            File.WriteAllText(htmlPath, html, new UTF8Encoding(false));
            return htmlPath;
        }
    }
}
