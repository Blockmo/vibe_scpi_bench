using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AteBench
{
    /// <summary>一個 SCPI 指令 (一次 write 可拆出多個)。</summary>
    public sealed class ScpiCommand
    {
        public string Key;      // 統計用的鍵 (正規化後的完整路徑，查詢含 '?')
        public string Raw;      // 原始寫法 (不含參數)
        public string Args;     // 參數字串
        public bool IsQuery;
        public string Kind;     // scpi | common | raw | binary
    }

    /// <summary>
    /// SCPI 訊息拆解與正規化。
    /// 一次 write 可能含多個指令 (以 ';' 或換行分隔)，且後續指令可沿用前一個指令的路徑
    /// (例如 "SOUR:VOLT 1;CURR 2" 的第二個指令實際上是 SOUR:CURR)。
    /// </summary>
    public static class Scpi
    {
        static readonly Regex HeaderRe = new Regex(@"^[*:]?[A-Za-z_][A-Za-z0-9_:.\[\]]*\??$", RegexOptions.Compiled);
        static readonly Regex NodeRe = new Regex(@"^([A-Za-z_]+)(\d*)$", RegexOptions.Compiled);
        static readonly Regex BlockRe = new Regex(@"#([1-9])(\d+)", RegexOptions.Compiled);
        static readonly Regex LineRe = new Regex(@"[\r\n]+", RegexOptions.Compiled);
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        static readonly char[] TrimChars = { '\0', ' ', '\t', '\r', '\n' };

        public static string Decode(byte[] data)
        {
            return Latin1.GetString(data);
        }

        public static bool IsMostlyText(byte[] data)
        {
            if (data == null || data.Length == 0) return true;
            int bad = 0;
            foreach (byte b in data)
                if (!((b >= 0x20 && b < 0x7F) || b == 0x09 || b == 0x0A || b == 0x0D)) bad++;
            return bad <= Math.Max(1, data.Length / 20);
        }

        /// <summary>把 IEEE 488.2 區塊資料 (#&lt;n&gt;&lt;len&gt;&lt;data&gt;) 與過長的內容縮短，方便顯示。</summary>
        public static string ShortenBlock(string text, int limit = 120)
        {
            Match m = BlockRe.Match(text);
            if (m.Success)
            {
                int ndig = m.Groups[1].Value[0] - '0';
                string all = m.Groups[2].Value;
                if (all.Length >= ndig)
                    text = text.Substring(0, m.Index) + "#<block " + long.Parse(all.Substring(0, ndig)) + " bytes>";
            }
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text) sb.Append(ch >= 0x20 && ch < 0x7F ? ch : '.');
            text = sb.ToString();
            if (text.Length > limit) text = text.Substring(0, limit - 3) + "...";
            return text;
        }

        /// <summary>以 ';' 分割，但略過引號內的分號；遇到區塊資料就不再分割。</summary>
        static List<string> SplitUnits(string line)
        {
            var units = new List<string>();
            var cur = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (quote != '\0')
                {
                    cur.Append(ch);
                    if (ch == quote) quote = '\0';
                }
                else if (ch == '\'' || ch == '"')
                {
                    quote = ch;
                    cur.Append(ch);
                }
                else if (ch == '#' && i + 1 < line.Length && line[i + 1] >= '1' && line[i + 1] <= '9')
                {
                    cur.Append(line, i, line.Length - i);
                    break;
                }
                else if (ch == ';')
                {
                    units.Add(cur.ToString());
                    cur.Clear();
                }
                else cur.Append(ch);
            }
            units.Add(cur.ToString());
            return units;
        }

        /// <summary>SCPI 助記詞的短格式: VOLTage -> VOLT, POWer -> POW, FREQuency -> FREQ。</summary>
        public static string ShortForm(string node)
        {
            Match m = NodeRe.Match(node);
            if (!m.Success) return node.ToUpperInvariant();
            string word = m.Groups[1].Value.ToUpperInvariant();
            if (word.Length > 4)
                word = "AEIOU".IndexOf(word[3]) >= 0 ? word.Substring(0, 3) : word.Substring(0, 4);
            return word + m.Groups[2].Value;
        }

        public static string NormalizeHeader(string header, bool normalize = true)
        {
            bool query = header.EndsWith("?");
            string body = query ? header.Substring(0, header.Length - 1) : header;
            string key;
            if (body.StartsWith("*")) key = body.ToUpperInvariant();
            else
            {
                var nodes = new List<string>();
                foreach (string p in body.Replace("[", "").Replace("]", "").Split(':'))
                    if (p.Length > 0) nodes.Add(normalize ? ShortForm(p) : p.ToUpperInvariant());
                key = string.Join(":", nodes);
            }
            return key + (query ? "?" : "");
        }

        /// <summary>把一次 write 的資料拆成個別指令。</summary>
        public static List<ScpiCommand> ParseMessage(byte[] data, bool normalize = true)
        {
            var result = new List<ScpiCommand>();
            if (data == null || data.Length == 0) return result;

            string text = Decode(data);
            bool block = BlockRe.IsMatch(text.Length > 4096 ? text.Substring(0, 4096) : text);
            if (!IsMostlyText(data) && !block)
            {
                result.Add(new ScpiCommand { Key = "<binary>", Raw = "<binary>", Args = data.Length + " bytes", IsQuery = false, Kind = "binary" });
                return result;
            }

            // 區塊資料之後都是二進位，不可再用換行分割
            string[] lines = block ? new[] { text } : LineRe.Split(text);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim(TrimChars);
                if (line.Length == 0) continue;
                string prefix = "";   // 前一個指令的父路徑
                bool first = true;
                foreach (string rawUnit in SplitUnits(line))
                {
                    string unit = rawUnit.Trim();
                    if (unit.Length == 0) continue;
                    int ws = 0;
                    while (ws < unit.Length && !char.IsWhiteSpace(unit[ws])) ws++;
                    string header = unit.Substring(0, ws);
                    string args = ws < unit.Length ? unit.Substring(ws).Trim() : "";

                    if (!HeaderRe.IsMatch(header))
                    {
                        // 非 SCPI 語法 (舊式儀器指令等)，原樣保留
                        result.Add(new ScpiCommand
                        {
                            Key = ShortenBlock(unit, 60),
                            Raw = unit.Length > 60 ? unit.Substring(0, 60) : unit,
                            Args = "", IsQuery = unit.EndsWith("?"), Kind = "raw"
                        });
                        first = false;
                        continue;
                    }
                    string full, kind;
                    if (header.StartsWith("*"))
                    {
                        full = header;
                        kind = "common";
                    }
                    else
                    {
                        kind = "scpi";
                        if (header.StartsWith(":") || first || prefix.Length == 0) full = header.TrimStart(':');
                        else full = prefix + ":" + header;
                        int idx = full.LastIndexOf(':');
                        prefix = idx >= 0 ? full.Substring(0, idx) : "";
                    }
                    first = false;
                    result.Add(new ScpiCommand
                    {
                        Key = NormalizeHeader(full, normalize), Raw = full, Args = ShortenBlock(args),
                        IsQuery = header.EndsWith("?"), Kind = kind
                    });
                }
            }
            return result;
        }
    }
}
