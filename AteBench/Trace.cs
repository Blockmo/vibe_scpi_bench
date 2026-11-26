using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AteBench
{
    /// <summary>I/O Trace 紀錄的一筆 API 呼叫。</summary>
    public sealed class Call
    {
        public string Api = "", Func = "";
        public int Pid, Tid;
        public bool Ok = true;
        public string StatusCode = "", StatusName = "", StatusText = "";
        public double? TIn, TOut;           // 當日秒數
        public string TimeText = "";
        public Dictionary<string, string> ParamsIn = new Dictionary<string, string>();
        public Dictionary<string, string> ParamsOut = new Dictionary<string, string>();
        public List<string> ParamInOrder = new List<string>();
        public byte[] BufIn, BufOut;
        public int BufInSize, BufOutSize;

        public double Duration
        {
            get
            {
                if (TIn == null || TOut == null) return 0.0;
                double d = TOut.Value - TIn.Value;
                return d < -43200 ? d + 86400 : Math.Max(d, 0.0);
            }
        }

        public string In(string name)
        {
            string v;
            return ParamsIn.TryGetValue(name, out v) ? v : null;
        }

        public string Out(string name)
        {
            string v;
            return ParamsOut.TryGetValue(name, out v) ? v : null;
        }
    }

    /// <summary>
    /// 增量讀取 NI I/O Trace XML 紀錄檔。
    /// 紀錄檔在擷取期間是「尚未結束」的 XML，所以用正規表示式逐筆取出完整的 &lt;call&gt; 區塊。
    /// </summary>
    public sealed class TraceReader
    {
        static readonly Regex CallRe = new Regex(@"<call\b([^>]*)>(.*?)</call>", RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex AttrRe = new Regex("([\\w-]+)=\"([^\"]*)\"", RegexOptions.Compiled);
        static readonly Regex StatusRe = new Regex(@"<status\b([^>]*?)(?:/>|>(.*?)</status>)", RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex SectionRe = new Regex(@"<(input|output)\b([^>]*?)(?:/>|>(.*?)</\1>)", RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex ParamRe = new Regex("<parameter name=\"([^\"]*)\"\\s*(?:/>|>(.*?)</parameter>)", RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex BufferRe = new Regex(@"<buffer\b([^>]*)>(.*?)</buffer>", RegexOptions.Singleline | RegexOptions.Compiled);
        static readonly Regex HexRe = new Regex(@"\b[0-9A-Fa-f]{2}\b", RegexOptions.Compiled);
        static readonly byte[] EndTag = Encoding.ASCII.GetBytes("</call>");

        readonly string _path;
        long _offset;

        public TraceReader(string path)
        {
            _path = path;
        }

        public List<Call> ReadNew()
        {
            var calls = new List<Call>();
            byte[] data;
            try
            {
                using (var f = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (f.Length <= _offset) return calls;
                    f.Seek(_offset, SeekOrigin.Begin);
                    data = new byte[f.Length - _offset];
                    int got = 0;
                    while (got < data.Length)
                    {
                        int n = f.Read(data, got, data.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got < data.Length) Array.Resize(ref data, got);
                }
            }
            catch (IOException) { return calls; }
            catch (UnauthorizedAccessException) { return calls; }

            // 只處理到最後一個完整的 </call>，其餘留待下次
            int end = LastIndexOf(data, EndTag);
            if (end < 0) return calls;
            end += EndTag.Length;
            string text = Encoding.UTF8.GetString(data, 0, end);
            _offset += end;

            foreach (Match m in CallRe.Matches(text))
            {
                try { calls.Add(ParseCall(m.Groups[1].Value, m.Groups[2].Value)); }
                catch (Exception) { /* 單筆格式異常不應中斷整體分析 */ }
            }
            return calls;
        }

        static int LastIndexOf(byte[] data, byte[] pat)
        {
            for (int i = data.Length - pat.Length; i >= 0; i--)
            {
                int j = 0;
                while (j < pat.Length && data[i + j] == pat[j]) j++;
                if (j == pat.Length) return i;
            }
            return -1;
        }

        static Dictionary<string, string> Attrs(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (Match m in AttrRe.Matches(text)) d[m.Groups[1].Value] = m.Groups[2].Value;
            return d;
        }

        static string Get(Dictionary<string, string> d, string key, string def = "")
        {
            string v;
            return d.TryGetValue(key, out v) ? v : def;
        }

        static double? Tod(string text)
        {
            string[] p = text.Split(':');
            int h, mi;
            double s;
            if (p.Length != 3 || !int.TryParse(p[0], out h) || !int.TryParse(p[1], out mi) ||
                !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out s)) return null;
            return h * 3600 + mi * 60 + s;
        }

        static int Hex(string text)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
            long v;
            return long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? unchecked((int)v) : 0;
        }

        static Call ParseCall(string attrText, string body)
        {
            var c = new Call();
            var attrs = Attrs(attrText);
            c.Api = Get(attrs, "type");
            c.Func = WebUtility.HtmlDecode(Get(attrs, "description"));
            c.Pid = Hex(Get(attrs, "process", "0"));
            c.Tid = Hex(Get(attrs, "thread", "0"));

            Match sm = StatusRe.Match(body);
            if (sm.Success)
            {
                var st = Attrs(sm.Groups[1].Value);
                c.Ok = Get(st, "type", "success") != "failure";
                c.StatusCode = Get(st, "code");
                c.StatusName = Get(st, "name");
                c.StatusText = WebUtility.HtmlDecode(sm.Groups[2].Value.Trim());
            }

            foreach (Match sec in SectionRe.Matches(body))
            {
                bool isInput = sec.Groups[1].Value == "input";
                var sattr = Attrs(sec.Groups[2].Value);
                string inner = sec.Groups[3].Value;
                var pars = isInput ? c.ParamsIn : c.ParamsOut;
                foreach (Match pm in ParamRe.Matches(inner))
                {
                    string name = WebUtility.HtmlDecode(pm.Groups[1].Value);
                    if (isInput && !pars.ContainsKey(name)) c.ParamInOrder.Add(name);
                    pars[name] = WebUtility.HtmlDecode(pm.Groups[2].Value);
                }
                byte[] buf = null;
                int size = 0;
                Match bm = BufferRe.Match(inner);
                if (bm.Success)
                {
                    MatchCollection hex = HexRe.Matches(bm.Groups[2].Value);
                    buf = new byte[hex.Count];
                    for (int i = 0; i < hex.Count; i++) buf[i] = Convert.ToByte(hex[i].Value, 16);
                    if (!int.TryParse(Get(Attrs(bm.Groups[1].Value), "size"), out size)) size = buf.Length;
                }
                string time = Get(sattr, "time");
                if (isInput)
                {
                    c.TIn = Tod(time);
                    c.TimeText = time;
                    c.BufIn = buf;
                    c.BufInSize = size;
                }
                else
                {
                    c.TOut = Tod(time);
                    c.BufOut = buf;
                    c.BufOutSize = size;
                }
            }
            return c;
        }
    }
}
