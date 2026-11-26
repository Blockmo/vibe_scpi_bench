using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AteBench
{
    /// <summary>
    /// 示波器風格的即時活動圖: 最近 60 秒每秒送出的指令數 (青) 與讀取次數 (紫)。
    /// 未擷取時顯示一條緩慢起伏的待機波形。
    /// </summary>
    public sealed class ScopeChart : FrameworkElement
    {
        const int N = 60;                               // 顯示的秒數
        readonly double[] _w = new double[N + 2];
        readonly double[] _r = new double[N + 2];
        double _phase = 1, _scale = 5, _scaleTarget = 5, _idleT;
        bool _active;

        static readonly Typeface LabelFace = new Typeface("Segoe UI");

        // 顏色取自目前主題的調色盤 (切換主題後下一個畫面就會套用)
        Brush Res(string key, Color fallback)
        {
            return TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
        }

        Color ResColor(string key, Color fallback)
        {
            var b = TryFindResource(key) as SolidColorBrush;
            return b != null ? b.Color : fallback;
        }

        static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }

        public bool Active
        {
            get { return _active; }
            set { _active = value; InvalidateVisual(); }
        }

        public void Reset()
        {
            Array.Clear(_w, 0, _w.Length);
            Array.Clear(_r, 0, _r.Length);
            _phase = 1;
            _scale = _scaleTarget = 5;
            InvalidateVisual();
        }

        /// <summary>加入一秒的取樣 (該秒送出的指令數、讀取次數)。</summary>
        public void Push(double writes, double reads)
        {
            Array.Copy(_w, 1, _w, 0, N + 1);
            Array.Copy(_r, 1, _r, 0, N + 1);
            _w[N + 1] = writes;
            _r[N + 1] = reads;
            _phase = 0;
            double max = 0;
            for (int i = 0; i < N + 2; i++) max = Math.Max(max, Math.Max(_w[i], _r[i]));
            _scaleTarget = Math.Max(5, Math.Ceiling(max * 1.25));
        }

        /// <summary>每個畫面更新呼叫一次，讓波形平滑向左捲動。</summary>
        public void Advance(double dt)
        {
            _idleT += dt;
            if (_active)
            {
                _phase = Math.Min(1, _phase + dt);
                _scale += (_scaleTarget - _scale) * Math.Min(1, dt * 6);
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 20 || h < 20) return;
            var GridPen = new Pen(Res("ScopeGridBrush", Colors.Gray), 1);
            var BasePen = new Pen(Res("ScopeBaseBrush", Colors.Gray), 1);
            var IdlePen = new Pen(Res("ScopeIdleBrush", Colors.SkyBlue), 1.6);
            Brush LabelBrush = Res("ScopeLabelBrush", Colors.Gray);
            Color Cyan = ResColor("CyanBrush", Colors.DeepSkyBlue);
            Color Violet = ResColor("VioletBrush", Colors.MediumPurple);
            const double top = 8, bottom = 6;
            double plotH = h - top - bottom, baseY = top + plotH;

            // 格線
            for (int i = 1; i < 4; i++)
            {
                double y = Math.Round(top + plotH * i / 4) + 0.5;
                dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
            }
            for (int i = 1; i < 6; i++)
            {
                double x = Math.Round(w * i / 6) + 0.5;
                dc.DrawLine(GridPen, new Point(x, top), new Point(x, baseY));
            }
            dc.DrawLine(BasePen, new Point(0, Math.Round(baseY) + 0.5), new Point(w, Math.Round(baseY) + 0.5));

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (!_active && IsFlat())
            {
                // 待機波形
                var geo = new StreamGeometry();
                using (StreamGeometryContext g = geo.Open())
                {
                    double mid = top + plotH * 0.55;
                    for (int i = 0; i <= 120; i++)
                    {
                        double x = w * i / 120.0;
                        double env = Math.Sin(Math.PI * i / 120.0);
                        double y = mid + Math.Sin(i * 0.22 + _idleT * 1.6) * plotH * 0.16 * env
                                       + Math.Sin(i * 0.07 - _idleT * 0.9) * plotH * 0.07 * env;
                        if (i == 0) g.BeginFigure(new Point(x, y), false, false);
                        else g.LineTo(new Point(x, y), true, true);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, IdlePen, geo);
                return;
            }

            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            DrawSeries(dc, _r, Violet, w, top, plotH, baseY);
            DrawSeries(dc, _w, Cyan, w, top, plotH, baseY);
            dc.Pop();

            var label = new FormattedText(Math.Round(_scale).ToString(CultureInfo.InvariantCulture) + " /s", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFace, 10.5, LabelBrush, dpi);
            dc.DrawText(label, new Point(6, top + 2));
            var span = new FormattedText("-60 s", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10.5, LabelBrush, dpi);
            dc.DrawText(span, new Point(6, baseY - span.Height - 3));
        }

        bool IsFlat()
        {
            for (int i = 0; i < N + 2; i++)
                if (_w[i] != 0 || _r[i] != 0) return false;
            return true;
        }

        void DrawSeries(DrawingContext dc, double[] data, Color color, double w, double top, double plotH, double baseY)
        {
            double step = w / N;
            var pts = new Point[N + 2];
            for (int i = 0; i < N + 2; i++)
            {
                double v = Math.Min(data[i] / _scale, 1.0);
                pts[i] = new Point((i - 1 - _phase) * step + step, baseY - v * (plotH - 4));
            }
            // 最新一點由前一點的高度平滑長出來，並延伸到右緣
            Point last = pts[N + 1], prev = pts[N];
            last.Y = prev.Y + (last.Y - prev.Y) * Ease(_phase);
            last.X = w;
            pts[N + 1] = last;

            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (StreamGeometryContext g = line.Open())
                Trace(g, pts, false, baseY);
            using (StreamGeometryContext g = area.Open())
                Trace(g, pts, true, baseY);
            line.Freeze();
            area.Freeze();

            var fill = new LinearGradientBrush(Color.FromArgb(0x48, color.R, color.G, color.B),
                                               Color.FromArgb(0x00, color.R, color.G, color.B), 90);
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);
            dc.DrawGeometry(null, Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)), 7) { LineJoin = PenLineJoin.Round }), line);
            dc.DrawGeometry(null, Frozen(new Pen(new SolidColorBrush(color), 1.8) { LineJoin = PenLineJoin.Round }), line);
            dc.DrawEllipse(new SolidColorBrush(color), null, new Point(w - 3, last.Y), 3, 3);
        }

        static double Ease(double t)
        {
            return 1 - (1 - t) * (1 - t);
        }

        /// <summary>以中點二次貝茲曲線平滑連接各點 (不會超出資料範圍)。</summary>
        static void Trace(StreamGeometryContext g, Point[] pts, bool closed, double baseY)
        {
            if (closed)
            {
                g.BeginFigure(new Point(pts[0].X, baseY), true, true);
                g.LineTo(pts[0], false, false);
            }
            else g.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Length - 1; i++)
            {
                var mid = new Point((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
                g.QuadraticBezierTo(pts[i], mid, !closed, true);
            }
            g.LineTo(pts[pts.Length - 1], !closed, true);
            if (closed) g.LineTo(new Point(pts[pts.Length - 1].X, baseY), false, false);
        }
    }
}
