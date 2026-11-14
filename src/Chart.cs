using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BcnMiner
{
    // Hashrate over the last hour: one sample every 10 s, newest on the right (design: area + line + end dot).
    public class HashChart : FrameworkElement
    {
        public const int Slots = 360;
        public readonly List<double> Data = new List<double>();

        public void Push(double v)
        {
            Data.Add(v);
            while (Data.Count > Slots) Data.RemoveAt(0);
            InvalidateVisual();
        }

        public double Max()
        {
            double m = 0;
            foreach (var d in Data) if (d > m) m = d;
            return m;
        }

        static double Nice(double v)
        {
            if (v <= 0) return 1;
            double e = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (var m in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
                if (m * e >= v) return m * e;
            return 10 * e;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;
            var grid = Theme.Get("Grid");
            var muted = Theme.Get("Muted");
            var line = Theme.Get("ChartLine");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            double max = Max();
            double unit = Fmt.UnitOf(max);
            double top = Nice(max / unit * 1.15);
            const double left = 36, padTop = 8, padBot = 6;
            double plotW = w - left, plotH = h - padTop - padBot;
            Func<double, double> Y = v => padTop + plotH * (1 - Math.Min(v / unit / top, 1));

            var pen = new Pen(grid, 1);
            for (int i = 0; i <= 3; i++)
            {
                double v = top * (3 - i) / 3.0;
                double y = Math.Round(padTop + plotH * i / 3.0) + 0.5;
                dc.DrawLine(pen, new Point(left, y), new Point(w, y));
                if (i < 3)
                {
                    string s = v >= 10 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);
                    var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11, muted, dpi);
                    dc.DrawText(ft, new Point(0, y - ft.Height / 2));
                }
            }

            int n = Data.Count;
            if (n < 2)
            {
                var ft = new FormattedText(n == 0 ? L.T("chart.empty") : L.T("chart.collecting"), CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, face, 12, muted, dpi);
                dc.DrawText(ft, new Point(left + (plotW - ft.Width) / 2, padTop + (plotH - ft.Height) / 2));
                return;
            }

            var pts = new Point[n];
            for (int i = 0; i < n; i++)
            {
                double x = left + plotW * (Slots - n + i) / (double)(Slots - 1);
                pts[i] = new Point(x, Y(Data[i]));
            }

            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(pts[0].X, padTop + plotH), true, true);
                foreach (var p in pts) g.LineTo(p, true, true);
                g.LineTo(new Point(pts[n - 1].X, padTop + plotH), true, true);
            }
            area.Freeze();
            var fill = line.Clone();
            fill.Opacity = 0.12;
            dc.DrawGeometry(fill, null, area);

            var stroke = new StreamGeometry();
            using (var g = stroke.Open())
            {
                g.BeginFigure(pts[0], false, false);
                for (int i = 1; i < n; i++) g.LineTo(pts[i], true, true);
            }
            stroke.Freeze();
            dc.DrawGeometry(null, new Pen(line, 2) { LineJoin = PenLineJoin.Round }, stroke);
            dc.DrawEllipse(line, null, pts[n - 1], 4, 4);
        }
    }
}
