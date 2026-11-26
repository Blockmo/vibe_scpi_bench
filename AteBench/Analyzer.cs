using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AteBench
{
    public sealed class CmdStat
    {
        public string Key, Kind;
        public bool IsQuery;
        public int Count, Errors, FirstSeq;
        public double Total, Max;
        public List<string> ArgOrder = new List<string>();                 // 參數出現順序
        public Dictionary<string, int> Args = new Dictionary<string, int>();
        public List<string> Raw = new List<string>();
        public List<string> Resp = new List<string>();
    }

    public sealed class OtherStat
    {
        public string Func;
        public int Count, Errors;
        public double Time;
    }

    public sealed class Instrument
    {
        public int Idx;
        public string Name, Api, Idn = "";
        public int Writes, Reads, Commands, Errors;
        public long BytesOut, BytesIn;
        public double IoTime;
        public List<CmdStat> CmdList = new List<CmdStat>();
        public Dictionary<string, CmdStat> Cmds = new Dictionary<string, CmdStat>();
        public List<OtherStat> OtherList = new List<OtherStat>();
        public Dictionary<string, OtherStat> Others = new Dictionary<string, OtherStat>();
        public List<CmdStat> Pending = new List<CmdStat>();                // 等待回應的查詢
    }

    public sealed class TraceEvent
    {
        public int Seq, Pid, Inst, Bytes;
        public double T, DurMs, AbsT;
        public string Time, Func, Dir, Text, Status;
        public bool Ok;
        public List<string> Cmds;
    }

    public sealed class ErrorEvent
    {
        public int Seq, Inst;
        public double T;
        public string Time, Func, Text, Status, Desc, Iberr;
    }

    public sealed class LiveTotals
    {
        public int Calls, Writes, Reads, Commands, Unique, Instruments, Errors;
    }

    /// <summary>把 I/O Trace 的 API 呼叫彙整成「每台儀器用了哪些 SCPI 指令」。</summary>
    public sealed class Analyzer
    {
        public const string SystemName = "(無特定儀器)";

        static readonly HashSet<string> VisaWrite = new HashSet<string> { "viWrite", "viWriteAsync", "Formatted viWrite", "viWriteFromFile" };
        static readonly HashSet<string> VisaRead = new HashSet<string> { "viRead", "viReadAsync", "Formatted viRead", "viReadToFile" };
        static readonly HashSet<string> GpibWrite = new HashSet<string> { "ibwrt", "ibwrta", "ibwrtf", "Send", "SendList", "SendDataBytes" };
        static readonly HashSet<string> GpibRead = new HashSet<string> { "ibrd", "ibrda", "ibrdf", "Receive", "RcvRespMsg" };
        // 這些呼叫只是把資料放進格式化緩衝區，真正的匯流排 I/O 會另外記成 "Formatted viWrite/viRead"
        static readonly HashSet<string> Wrappers = new HashSet<string> {
            "viBufWrite", "viBufRead", "viPrintf", "viVPrintf", "viSPrintf", "viVSPrintf",
            "viScanf", "viVScanf", "viSScanf", "viVSScanf", "viQueryf", "viVQueryf", "ibcmd", "ibcmda", "SendCmds" };
        static readonly HashSet<string> SkipParams = new HashSet<string> { "vi", "ud", "Brd/Dev Name" };

        static readonly Regex ViRe = new Regex(@"^(.*) \((0x[0-9A-Fa-f]+)\)$", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex IntRe = new Regex(@"-?(?:0x[0-9A-Fa-f]+|\d+)", RegexOptions.Compiled);
        static readonly Regex BoardRe = new Regex(@"^gpib\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex WriteNameRe = new Regex("write|wrt|send", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex ReadNameRe = new Regex("read|receive|ibrd", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public readonly bool Normalize;
        public readonly int MaxText;
        public readonly List<Instrument> Instruments = new List<Instrument>();
        public readonly List<TraceEvent> Events = new List<TraceEvent>();
        public readonly List<ErrorEvent> Errors = new List<ErrorEvent>();
        public readonly List<KeyValuePair<double, string>> Markers = new List<KeyValuePair<double, string>>();
        public readonly Dictionary<int, string> PidNames = new Dictionary<int, string>();
        public readonly Dictionary<int, int> PidCalls = new Dictionary<int, int>();
        public readonly List<int> PidOrder = new List<int>();
        public int NCalls, TruncatedBuffers;
        public double? FirstAbs;
        public double LastAbs;
        public string FirstText = "";
        public double? StartTod;            // 擷取開始的當日秒數 (標記時間的基準日)

        readonly Dictionary<string, Instrument> _byName = new Dictionary<string, Instrument>();
        readonly Dictionary<long, string> _visaSessions = new Dictionary<long, string>();
        readonly Dictionary<long, string> _gpibDevs = new Dictionary<long, string>();
        readonly Dictionary<string, BoardState> _gpibAddr = new Dictionary<string, BoardState>();
        double? _lastTod;
        int _day;

        sealed class BoardState
        {
            public List<int> Listen = new List<int>();
            public int? Talk;
        }

        public Analyzer(bool normalize = true, int maxText = 300)
        {
            Normalize = normalize;
            MaxText = maxText;
        }

        // ------------------------------------------------------------------ 工具
        static int Int(string text, int def = 0)
        {
            if (text == null) return def;
            Match m = IntRe.Match(text);
            if (!m.Success) return def;
            string s = m.Value;
            bool neg = s.StartsWith("-");
            if (neg) s = s.Substring(1);
            long v;
            bool ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)
                : long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
            if (!ok) return def;
            return unchecked((int)(neg ? -v : v));
        }

        static long Key(int pid, int id)
        {
            return ((long)pid << 32) | (uint)id;
        }

        static string GpibName(int board, int pad, int sad = 0)
        {
            return "GPIB" + board + "::" + pad + (sad != 0 ? "::" + sad : "") + "::INSTR";
        }

        // ------------------------------------------------------------------ 儀器辨識
        Instrument Inst(string name, string api)
        {
            Instrument inst;
            if (!_byName.TryGetValue(name, out inst))
            {
                inst = new Instrument { Idx = Instruments.Count, Name = name, Api = api };
                _byName[name] = inst;
                Instruments.Add(inst);
            }
            return inst;
        }

        string ResolveVisa(Call c)
        {
            if (c.Func == "viOpen")
            {
                string name = c.In("rsrcName") ?? "";
                int sess = Int(c.Out("*vi"));
                if (name.Length > 0 && sess != 0 && c.Ok) _visaSessions[Key(c.Pid, sess)] = name;
                return name.Length > 0 ? name : null;
            }
            string vi = c.In("vi");
            if (vi == null) return null;
            Match m = ViRe.Match(vi);
            if (m.Success)
            {
                string name = m.Groups[1].Value.Trim();
                if (name.Length > 0)
                {
                    long k = Key(c.Pid, Int(m.Groups[2].Value));
                    if (!_visaSessions.ContainsKey(k)) _visaSessions[k] = name;
                    return name;
                }
            }
            string known;
            return _visaSessions.TryGetValue(Key(c.Pid, Int(vi)), out known) ? known : null;
        }

        string ResolveGpib(Call c)
        {
            if (c.Func == "ibdev")
            {
                string name = GpibName(Int(c.In("ud")), Int(c.In("pad")), Int(c.In("sad")) & 0x1F);
                _gpibDevs[Key(c.Pid, Int(c.Out("ud"), -1))] = name;
                return name;
            }
            if (c.ParamsIn.ContainsKey("BrdIndx"))
            {
                int board = Int(c.In("BrdIndx"));
                if (c.ParamsIn.ContainsKey("address"))
                {
                    int addr = Int(c.In("address"));
                    return GpibName(board, addr & 0xFF, (addr >> 8) & 0x1F);
                }
                return GpibWrite.Contains(c.Func) || GpibRead.Contains(c.Func) ? "GPIB" + board + " (多重位址)" : null;
            }
            if (!c.ParamsIn.ContainsKey("ud")) return null;
            int ud = Int(c.In("ud"), -1);
            string known;
            if (_gpibDevs.TryGetValue(Key(c.Pid, ud), out known)) return known;
            string label = c.In("Brd/Dev Name");
            if (string.IsNullOrEmpty(label)) label = "ud" + ud;
            if (BoardRe.IsMatch(label))
            {
                // 介面卡層級 I/O: 位址由先前的 ibcmd 指定
                string sk = c.Pid + "|" + label;
                BoardState state;
                if (!_gpibAddr.TryGetValue(sk, out state)) _gpibAddr[sk] = state = new BoardState();
                int board = Int(label.Substring(4));
                if ((c.Func == "ibcmd" || c.Func == "ibcmda") && c.BufIn != null && c.BufIn.Length > 0)
                {
                    foreach (byte b in c.BufIn)
                    {
                        if (b == 0x3F) state.Listen.Clear();
                        else if (b >= 0x20 && b <= 0x3E) state.Listen.Add(b - 0x20);
                        else if (b == 0x5F) state.Talk = null;
                        else if (b >= 0x40 && b <= 0x5E) state.Talk = b - 0x40;
                    }
                    return null;
                }
                if (GpibWrite.Contains(c.Func) && state.Listen.Count > 0) return GpibName(board, state.Listen[0]);
                if (GpibRead.Contains(c.Func) && state.Talk != null) return GpibName(board, state.Talk.Value);
                return null;
            }
            return "NI-488.2 " + label;
        }

        // ------------------------------------------------------------------ 主流程
        /// <summary>標記時間以「擷取開始當日午夜起算的秒數」表示。</summary>
        public void AddMarker(double absSeconds, string label)
        {
            Markers.Add(new KeyValuePair<double, string>(absSeconds, label));
            var sorted = Markers.OrderBy(m => m.Key).ToList();   // 穩定排序
            Markers.Clear();
            Markers.AddRange(sorted);
        }

        static string TrimEnd(string s)
        {
            return s.TrimEnd('\r', '\n', '\0');
        }

        public void Feed(Call c)
        {
            NCalls++;
            int seq = NCalls;
            if (!PidCalls.ContainsKey(c.Pid))
            {
                PidCalls[c.Pid] = 0;
                PidOrder.Add(c.Pid);
            }
            PidCalls[c.Pid]++;

            double tod = c.TIn ?? (_lastTod ?? 0.0);
            if (_lastTod == null)
            {
                // 擷取在午夜前開始、第一筆呼叫在午夜後才出現
                if (StartTod != null && tod < StartTod.Value - 43200) _day = 1;
            }
            else if (tod < _lastTod.Value - 43200) _day++;
            _lastTod = tod;
            double absT = tod + 86400.0 * _day;
            if (FirstAbs == null)
            {
                FirstAbs = absT;
                FirstText = c.TimeText;
            }
            double dur = c.Duration;
            LastAbs = Math.Max(LastAbs, absT + dur);

            bool gpib = c.Api.Contains("488") || c.Func.StartsWith("ib");
            string name = gpib ? ResolveGpib(c) : ResolveVisa(c);
            string api = c.Api.Length > 0 ? c.Api : (gpib ? "NI-488.2" : "VISA");
            Instrument inst = Inst(name ?? SystemName, api);
            if (!inst.Api.Split(new[] { " + " }, StringSplitOptions.None).Contains(api)) inst.Api += " + " + api;

            string direction = "";
            if (!Wrappers.Contains(c.Func))
            {
                if (VisaWrite.Contains(c.Func) || GpibWrite.Contains(c.Func)) direction = "W";
                else if (VisaRead.Contains(c.Func) || GpibRead.Contains(c.Func)) direction = "R";
                else if (c.BufIn != null && WriteNameRe.IsMatch(c.Func)) direction = "W";
                else if (c.BufOut != null && ReadNameRe.IsMatch(c.Func)) direction = "R";
            }

            string text = "";
            int nbytes = 0;
            List<string> keys = null;
            if (direction == "W")
            {
                byte[] data = c.BufIn ?? new byte[0];
                nbytes = c.BufInSize != 0 ? c.BufInSize : data.Length;
                if (nbytes > data.Length) TruncatedBuffers++;
                inst.Writes++;
                inst.BytesOut += nbytes;
                inst.IoTime += dur;
                List<ScpiCommand> cmds = Scpi.ParseMessage(data, Normalize);
                inst.Pending = new List<CmdStat>();
                double share = cmds.Count > 0 ? dur / cmds.Count : 0.0;
                foreach (ScpiCommand cmd in cmds)
                {
                    CmdStat st;
                    if (!inst.Cmds.TryGetValue(cmd.Key, out st))
                    {
                        st = new CmdStat { Key = cmd.Key, Kind = cmd.Kind, IsQuery = cmd.IsQuery, FirstSeq = seq };
                        inst.Cmds[cmd.Key] = st;
                        inst.CmdList.Add(st);
                    }
                    st.Count++;
                    st.Total += share;
                    st.Max = Math.Max(st.Max, share);
                    if (!st.Raw.Contains(cmd.Raw)) st.Raw.Add(cmd.Raw);
                    int n;
                    if (st.Args.TryGetValue(cmd.Args, out n)) st.Args[cmd.Args] = n + 1;
                    else if (st.Args.Count < 500)
                    {
                        st.Args[cmd.Args] = 1;
                        st.ArgOrder.Add(cmd.Args);
                    }
                    if (!c.Ok) st.Errors++;
                    if (cmd.IsQuery) inst.Pending.Add(st);
                    if (keys == null) keys = new List<string>();
                    keys.Add(cmd.Key);
                }
                inst.Commands += cmds.Count;
                text = Scpi.IsMostlyText(data)
                    ? Scpi.ShortenBlock(TrimEnd(Scpi.Decode(data)), MaxText)
                    : "<binary " + nbytes + " bytes>";
            }
            else if (direction == "R")
            {
                byte[] data = c.BufOut ?? new byte[0];
                nbytes = c.BufOutSize != 0 ? c.BufOutSize : data.Length;
                if (nbytes > data.Length) TruncatedBuffers++;
                inst.Reads++;
                inst.BytesIn += nbytes;
                inst.IoTime += dur;
                text = data.Length > 0 ? Scpi.ShortenBlock(TrimEnd(Scpi.Decode(data)), MaxText) : "";
                if (inst.Pending.Count > 0)
                {
                    int n = inst.Pending.Count;
                    string[] pieces = null;
                    if (n > 1)
                    {
                        string[] sp = text.Split(';');
                        if (sp.Length == n) pieces = sp;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        CmdStat st = inst.Pending[i];
                        st.Total += dur / n;
                        st.Max = Math.Max(st.Max, dur / n);
                        if (!c.Ok) st.Errors++;
                        string resp = pieces != null ? pieces[i] : text;
                        if (resp.Length > 0 && st.Resp.Count < 3)
                        {
                            string r = resp.Length > 80 ? resp.Substring(0, 80) : resp;
                            if (!st.Resp.Contains(r)) st.Resp.Add(r);
                        }
                        if (st.Key == "*IDN?" && resp.Length > 0 && c.Ok)
                            inst.Idn = resp.Length > 120 ? resp.Substring(0, 120) : resp;
                    }
                    if (c.Ok || data.Length == 0) inst.Pending = new List<CmdStat>();
                }
            }
            else
            {
                OtherStat o;
                if (!inst.Others.TryGetValue(c.Func, out o))
                {
                    o = new OtherStat { Func = c.Func };
                    inst.Others[c.Func] = o;
                    inst.OtherList.Add(o);
                }
                o.Count++;
                if (!c.Ok) o.Errors++;
                o.Time += dur;
                var vals = new List<string>();
                foreach (string k in c.ParamInOrder)
                    if (!k.StartsWith("&") && !SkipParams.Contains(k)) vals.Add(c.ParamsIn[k]);
                text = Scpi.ShortenBlock(string.Join(", ", vals), 160);
            }

            double rel = Math.Round(absT - FirstAbs.Value, 4);
            if (!c.Ok)
            {
                inst.Errors++;
                Errors.Add(new ErrorEvent
                {
                    Seq = seq, T = rel, Time = c.TimeText, Inst = inst.Idx, Func = c.Func, Text = text,
                    Status = c.StatusName.Length > 0 ? c.StatusName : c.StatusCode, Desc = c.StatusText,
                    Iberr = c.Out("iberr") ?? ""
                });
            }
            Events.Add(new TraceEvent
            {
                Seq = seq, T = rel, Time = c.TimeText, Pid = c.Pid, Inst = inst.Idx, Func = c.Func, Dir = direction,
                Text = text, Bytes = nbytes, DurMs = Math.Round(dur * 1000, 2), Ok = c.Ok, Status = c.StatusName,
                AbsT = absT, Cmds = keys
            });
        }

        // ------------------------------------------------------------------ 統計
        public LiveTotals Live()
        {
            var t = new LiveTotals { Calls = NCalls, Errors = Errors.Count };
            foreach (Instrument i in Instruments)
            {
                if (i.Name == SystemName) continue;
                t.Writes += i.Writes;
                t.Reads += i.Reads;
                t.Commands += i.Commands;
                t.Unique += i.Cmds.Count;
                if (i.Writes > 0 || i.Reads > 0) t.Instruments++;
            }
            return t;
        }

        /// <summary>事件所屬的測項索引 (0 = 第一個標記之前)。</summary>
        public int SegmentOf(double absT)
        {
            int n = 0;
            foreach (var m in Markers)
            {
                if (m.Key <= absT) n++;
                else break;
            }
            return n;
        }

        /// <summary>某指令出現次數最多的參數 (次數相同時依出現順序)。</summary>
        public static List<KeyValuePair<string, int>> TopArgs(CmdStat st, int max)
        {
            return st.ArgOrder.Select(a => new KeyValuePair<string, int>(a, st.Args[a]))
                .OrderByDescending(p => p.Value).Take(max).Where(p => p.Key.Length > 0).ToList();
        }
    }
}
