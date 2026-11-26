using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AteBench
{
    /// <summary>主視窗: 開始擷取 -> 執行 ATE -> 停止並產生報告，擷取期間即時顯示儀表板。</summary>
    public partial class MainWindow : Window
    {
        enum State { Idle, Busy, Capturing }

        const int MaxStreamRows = 500;

        readonly ObservableCollection<InstrumentVm> _instruments = new ObservableCollection<InstrumentVm>();
        readonly ObservableCollection<StreamItem> _stream = new ObservableCollection<StreamItem>();
        readonly Dictionary<int, InstrumentVm> _instByIdx = new Dictionary<int, InstrumentVm>();
        readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly Stopwatch _frameWatch = Stopwatch.StartNew();
        readonly Stopwatch _bucketWatch = new Stopwatch();

        // KPI 數字以動畫方式逼近目標值
        readonly TextBlock[] _kpiText;
        readonly double[] _kpiShown = new double[6];
        readonly double[] _kpiTarget = new double[6];

        Session _session;
        State _state = State.Idle;
        int _lastEvent;                 // 已顯示到指令流的事件數
        double _bucketW, _bucketR;      // 這一秒累計的指令數 / 讀取數
        string _reportPath;

        public MainWindow()
        {
            InitializeComponent();
            (ThemeManager.Current == AppTheme.Light ? LightThemeButton : DarkThemeButton).IsChecked = true;
            ThemeManager.Changed += OnThemeChanged;
            _kpiText = new[] { KpiInstruments, KpiCommands, KpiUnique, KpiWrites, KpiReads, KpiErrors };
            InstrumentList.ItemsSource = _instruments;
            StreamList.ItemsSource = _stream;
            _poll.Tick += (s, e) => PollTick();
            CompositionTarget.Rendering += OnFrame;
            StateChanged += (s, e) => OnWindowStateChanged();
            Closing += OnClosing;
            Closed += (s, e) =>
            {
                CompositionTarget.Rendering -= OnFrame;
                ThemeManager.Changed -= OnThemeChanged;
            };
        }

        // ------------------------------------------------------------------ 視窗外框
        void Minimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        void Maximize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        void OnWindowStateChanged()
        {
            // 自訂外框的視窗最大化時會超出螢幕邊緣，補上邊界
            bool max = WindowState == WindowState.Maximized;
            Root.Margin = max ? new Thickness(7) : new Thickness(0);
            MaxButton.Tag = Geometry.Parse(max ? "M0,2 H8 V10 H0 Z M2,2 V0 H10 V8 H8" : "M0,0 H10 V10 H0 Z");
        }

        // ------------------------------------------------------------------ 主題
        void Theme_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded && ThemeManager.Current == (sender == LightThemeButton ? AppTheme.Light : AppTheme.Dark)) return;
            ThemeManager.Apply(sender == LightThemeButton ? AppTheme.Light : AppTheme.Dark);
        }

        void OnThemeChanged(object sender, EventArgs e)
        {
            foreach (InstrumentVm vm in _instruments) vm.RefreshTheme();
            Scope.InvalidateVisual();
        }

        // ------------------------------------------------------------------ 每個畫面: 動畫
        void OnFrame(object sender, EventArgs e)
        {
            double dt = Math.Min(0.1, _frameWatch.Elapsed.TotalSeconds);
            _frameWatch.Restart();
            Scope.Advance(dt);
            for (int i = 0; i < _kpiText.Length; i++)
            {
                double diff = _kpiTarget[i] - _kpiShown[i];
                if (diff == 0) continue;
                _kpiShown[i] = Math.Abs(diff) < 0.5 ? _kpiTarget[i] : _kpiShown[i] + diff * Math.Min(1, dt * 9);
                _kpiText[i].Text = Math.Round(_kpiShown[i]).ToString("N0");
            }
        }

        // ------------------------------------------------------------------ 狀態
        void SetState(State state, string title, string hint)
        {
            _state = state;
            CaptureButton.Tag = state == State.Capturing ? "capturing" : state == State.Busy ? "busy" : "idle";
            CaptureButton.IsHitTestVisible = state != State.Busy;
            StateText.Text = title;
            StateText.SetResourceReference(TextBlock.ForegroundProperty, state == State.Capturing ? "CoralBrush" : "InkBrush");
            ElapsedText.SetResourceReference(TextBlock.ForegroundProperty, state == State.Capturing ? "InkBrush" : "MutedBrush");
            HintText.Text = hint;
            NormalizeSwitch.IsEnabled = OpenExistingButton.IsEnabled = state == State.Idle;
        }

        void ShowBanner(bool ok, string title, string detail, bool withReport)
        {
            Banner.SetResourceReference(Border.BackgroundProperty, ok ? "BannerOkBrush" : "BannerErrBrush");
            Banner.SetResourceReference(Border.BorderBrushProperty, ok ? "BannerOkBorderBrush" : "BannerErrBorderBrush");
            BannerTitle.Text = title;
            BannerTitle.SetResourceReference(TextBlock.ForegroundProperty, ok ? "GreenBrush" : "CoralBrush");
            BannerDetail.Text = detail;
            BannerDetail.ToolTip = detail;
            BannerOpenReport.Visibility = BannerOpenFolder.Visibility = withReport ? Visibility.Visible : Visibility.Collapsed;
            Banner.Visibility = Visibility.Visible;
        }

        void BannerClose_Click(object sender, RoutedEventArgs e) { Banner.Visibility = Visibility.Collapsed; }

        void ResetDashboard()
        {
            _instruments.Clear();
            _instByIdx.Clear();
            _stream.Clear();
            _lastEvent = 0;
            _bucketW = _bucketR = 0;
            Array.Clear(_kpiTarget, 0, _kpiTarget.Length);
            Scope.Reset();
            RateText.Text = "最近 60 秒";
            ElapsedText.Text = "00:00:00";
            KpiErrors.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            EmptyInstruments.Visibility = EmptyStream.Visibility = Visibility.Visible;
            Banner.Visibility = Visibility.Collapsed;
        }

        // ------------------------------------------------------------------ 擷取
        async void Capture_Click(object sender, RoutedEventArgs e)
        {
            if (_state == State.Idle) await StartCapture();
            else if (_state == State.Capturing) await StopCapture();
        }

        async Task StartCapture()
        {
            ResetDashboard();
            SetState(State.Busy, "啟動中…", "正在啟動 NI I/O Trace。");
            Session session;
            try
            {
                session = new Session(null, NormalizeSwitch.IsChecked == true);
                await Task.Run(() => session.Start());
            }
            catch (Exception ex)
            {
                SetState(State.Idle, "待命", "按下按鈕開始擷取，再啟動 ATE 程式進行測試。");
                ShowBanner(false, "無法開始擷取", ex.Message, false);
                return;
            }
            _session = session;
            SetState(State.Capturing, "擷取中", "現在可以啟動 ATE 程式。測試結束後再按一次按鈕產生報告。");
            Scope.Active = true;
            _bucketWatch.Restart();
            _poll.Start();
        }

        void PollTick()
        {
            Session s = _session;
            if (s == null || _state != State.Capturing) return;
            s.Poll();
            SyncDashboard(s.Analyzer, true);
            int sec = (int)s.Elapsed;
            ElapsedText.Text = string.Format("{0:00}:{1:00}:{2:00}", sec / 3600, sec % 3600 / 60, sec % 60);

            if (_bucketWatch.Elapsed.TotalSeconds >= 1)
            {
                Scope.Push(_bucketW, _bucketR);
                RateText.Text = string.Format("目前 {0:N0} 指令 / 秒　·　最近 60 秒", _bucketW);
                _bucketW = _bucketR = 0;
                _bucketWatch.Restart();
            }
        }

        /// <summary>把分析器目前的狀態同步到儀表板 (即時擷取與載入既有紀錄檔共用)。</summary>
        void SyncDashboard(Analyzer a, bool live)
        {
            long now = Environment.TickCount;

            // 指令流
            int added = 0;
            for (; _lastEvent < a.Events.Count; _lastEvent++)
            {
                TraceEvent ev = a.Events[_lastEvent];
                if (ev.Dir == "W") _bucketW += ev.Cmds != null ? ev.Cmds.Count : 1;
                else if (ev.Dir == "R") _bucketR += 1;
                if (ev.Dir.Length == 0 && ev.Ok) continue;
                if (!live && a.Events.Count - _lastEvent > MaxStreamRows) continue;     // 載入舊檔只顯示最後幾百筆
                string name = a.Instruments[ev.Inst].Name;
                string text = ev.Text;
                if (!ev.Ok) text = (text.Length > 0 ? text + "   " : ev.Func + "   ") + "[" + (ev.Status.Length > 0 ? ev.Status : "ERR") + "]";
                _stream.Add(new StreamItem
                {
                    Time = ev.Time.Length > 12 ? ev.Time.Substring(0, 12) : ev.Time,
                    Dir = !ev.Ok ? "E" : ev.Dir,
                    Inst = name.EndsWith("::INSTR") ? name.Substring(0, name.Length - 7) : name,
                    Text = text
                });
                added++;
            }
            while (_stream.Count > MaxStreamRows) _stream.RemoveAt(0);
            if (added > 0)
            {
                EmptyStream.Visibility = Visibility.Collapsed;
                if (!StreamList.IsMouseOver) StreamList.ScrollIntoView(_stream[_stream.Count - 1]);   // 滑鼠停在上面時不自動捲動
            }

            // 儀器卡片
            foreach (Instrument inst in a.Instruments)
            {
                if (inst.Name == Analyzer.SystemName || !(inst.Writes > 0 || inst.Reads > 0 || inst.Errors > 0)) continue;
                InstrumentVm vm;
                if (!_instByIdx.TryGetValue(inst.Idx, out vm))
                {
                    vm = new InstrumentVm(inst.Name);
                    _instByIdx[inst.Idx] = vm;
                    _instruments.Add(vm);
                    EmptyInstruments.Visibility = Visibility.Collapsed;
                }
                int io = inst.Writes + inst.Reads;
                if (io != vm.SeenIo)
                {
                    vm.SeenIo = io;
                    vm.LastIoTick = now;
                }
                vm.Update(inst);
                vm.IsActive = live && now - vm.LastIoTick < 700;
            }

            LiveTotals t = a.Live();
            _kpiTarget[0] = t.Instruments;
            _kpiTarget[1] = t.Commands;
            _kpiTarget[2] = t.Unique;
            _kpiTarget[3] = t.Writes;
            _kpiTarget[4] = t.Reads;
            _kpiTarget[5] = t.Errors;
            KpiErrors.SetResourceReference(TextBlock.ForegroundProperty, t.Errors > 0 ? "CoralBrush" : "InkBrush");
        }

        async Task StopCapture()
        {
            Session s = _session;
            if (s == null) return;
            _poll.Stop();
            SetState(State.Busy, "產生報告…", "正在停止擷取並整理資料。");
            string path = null;
            Exception error = null;
            try { path = await Task.Run(() => s.Stop()); }
            catch (Exception ex) { error = ex; }

            SyncDashboard(s.Analyzer, false);
            Scope.Push(_bucketW, _bucketR);
            Scope.Active = false;
            _session = null;
            SetState(State.Idle, "完成", "可以再按一次按鈕開始新的擷取。");
            if (error != null)
            {
                ShowBanner(false, "產生報告失敗", error.Message, false);
                return;
            }
            Finish(s.Analyzer, path);
        }

        void Finish(Analyzer a, string path)
        {
            _reportPath = path;
            LiveTotals t = a.Live();
            ShowBanner(true,
                string.Format("報告已產生　·　{0:N0} 台儀器　·　{1:N0} 個指令　·　{2:N0} 種不重複指令", t.Instruments, t.Commands, t.Unique),
                path, true);
            if (Environment.GetEnvironmentVariable("ATEBENCH_NO_OPEN") != "1") OpenPath(path);
        }

        // ------------------------------------------------------------------ 既有紀錄檔 / 報告
        async void OpenExisting_Click(object sender, RoutedEventArgs e)
        {
            if (_state != State.Idle) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "選擇 I/O Trace XML 紀錄檔", Filter = "I/O Trace XML (*.xml)|*.xml" };
            if (dlg.ShowDialog(this) != true) return;
            string xml = dlg.FileName;
            bool normalize = NormalizeSwitch.IsChecked == true;

            ResetDashboard();
            SetState(State.Busy, "分析中…", Path.GetFileName(xml));
            Analyzer analyzer = null;
            string path = null;
            Exception error = null;
            try { path = await Task.Run(() => Session.AnalyzeFile(xml, null, normalize, out analyzer)); }
            catch (Exception ex) { error = ex; }
            SetState(State.Idle, error == null ? "完成" : "待命", "可以按下按鈕開始新的擷取。");
            if (error != null)
            {
                ShowBanner(false, "分析失敗", error.Message, false);
                return;
            }
            SyncDashboard(analyzer, false);
            Finish(analyzer, path);
        }

        void OpenReport_Click(object sender, RoutedEventArgs e) { OpenPath(_reportPath); }

        void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_reportPath != null) OpenPath(Path.GetDirectoryName(_reportPath));
        }

        void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { Process.Start(path); }
            catch (Exception ex) { ShowBanner(false, "無法開啟", ex.Message + "　" + path, false); }
        }

        // ------------------------------------------------------------------ 關閉
        void OnClosing(object sender, CancelEventArgs e)
        {
            if (_state == State.Busy)
            {
                e.Cancel = true;    // 正在啟動或產生報告，等它完成
                return;
            }
            if (_session == null) return;
            if (MessageBox.Show(this, "擷取仍在進行，要停止並結束嗎？（不會產生報告）", "ATE Bench",
                                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _poll.Stop();
            try { _session.Trace.Stop(); }
            catch (Exception) { /* 結束程式時忽略 */ }
        }
    }
}
