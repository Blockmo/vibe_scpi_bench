using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace AteBench
{
    /// <summary>儀器卡片的顯示資料。</summary>
    public sealed class InstrumentVm : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string Name { get; private set; }
        public string Kind { get; private set; }
        readonly string _kindKey;

        /// <summary>介面類型標籤的顏色 (取自目前主題的調色盤)。</summary>
        public Brush KindBrush
        {
            get { return (Application.Current != null ? Application.Current.TryFindResource(_kindKey) as Brush : null) ?? Brushes.Gray; }
        }

        public void RefreshTheme()
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs("KindBrush"));
        }

        string _idn = "", _last = "";
        int _commands, _unique, _writes, _reads, _errors;
        bool _active;
        IList<string> _top = new string[0];
        internal long LastIoTick;       // 最近一次有 I/O 的時間 (Environment.TickCount)
        internal int SeenIo = -1;

        public InstrumentVm(string name)
        {
            Name = name;
            string n = name.ToUpperInvariant();
            if (n.StartsWith("GPIB") || n.StartsWith("NI-488")) { Kind = "GPIB"; _kindKey = "AmberBrush"; }
            else if (n.StartsWith("TCPIP")) { Kind = "LAN"; _kindKey = "CyanBrush"; }
            else if (n.StartsWith("USB")) { Kind = "USB"; _kindKey = "GreenBrush"; }
            else if (n.StartsWith("ASRL")) { Kind = "SERIAL"; _kindKey = "VioletBrush"; }
            else if (n.StartsWith("PXI") || n.StartsWith("VXI")) { Kind = "PXI"; _kindKey = "PinkBrush"; }
            else { Kind = "VISA"; _kindKey = "SlateBrush"; }
        }

        void Set<T>(ref T field, T value, params string[] names)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            var h = PropertyChanged;
            if (h == null) return;
            foreach (string n in names) h(this, new PropertyChangedEventArgs(n));
        }

        public string Idn { get { return _idn; } set { Set(ref _idn, value, "Idn", "IdnText"); } }
        public string IdnText { get { return _idn.Length > 0 ? _idn : "尚未查詢 *IDN?"; } }
        public string Last { get { return _last; } set { Set(ref _last, value, "Last"); } }
        public int Commands { get { return _commands; } set { Set(ref _commands, value, "Commands"); } }
        public int Unique { get { return _unique; } set { Set(ref _unique, value, "Unique"); } }
        public int Writes { get { return _writes; } set { Set(ref _writes, value, "Writes", "WriteStar", "ReadStar"); } }
        public int Reads { get { return _reads; } set { Set(ref _reads, value, "Reads", "WriteStar", "ReadStar"); } }
        public int Errors { get { return _errors; } set { Set(ref _errors, value, "Errors", "HasErrors"); } }
        public bool HasErrors { get { return _errors > 0; } }
        public bool IsActive { get { return _active; } set { Set(ref _active, value, "IsActive"); } }
        public IList<string> Top { get { return _top; } set { _top = value; var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs("Top")); } }

        // 寫入 / 讀取比例條
        public GridLength WriteStar { get { return new GridLength(_writes + _reads == 0 ? 1 : _writes, GridUnitType.Star); } }
        public GridLength ReadStar { get { return new GridLength(_reads, GridUnitType.Star); } }

        public void Update(Instrument inst)
        {
            Idn = inst.Idn;
            Writes = inst.Writes;
            Reads = inst.Reads;
            Errors = inst.Errors;
            Unique = inst.Cmds.Count;
            if (inst.Commands != _commands || _top.Count == 0)
                Top = inst.CmdList.OrderByDescending(c => c.Count).Take(3).Select(c => c.Key + "  ×" + c.Count.ToString("N0")).ToList();
            Commands = inst.Commands;
        }
    }

    /// <summary>即時指令流的一列。</summary>
    public sealed class StreamItem
    {
        public string Time { get; set; }
        public string Dir { get; set; }     // W / R / E
        public string Inst { get; set; }
        public string Text { get; set; }
    }
}
