using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

/// <summary>Lightweight 10-minute (600-sample) live speed graph rendered with StreamGeometry and frozen pens/brushes.</summary>
public sealed class SpeedGraph : FrameworkElement
{
    static readonly Typeface GraphTypeface = new("Segoe UI");

    SpeedHistory _history = new(600);
    long[] _samples = [];
    long _lastRedrawTicks;

    static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }

    public SpeedGraph()
    {
        MinHeight = 84;
        Height = 96;
        FlowDirection = FlowDirection.LeftToRight;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public SpeedHistory History
    {
        get => _history;
        set
        {
            _history = value ?? new SpeedHistory(600);
            Refresh(force: true);
        }
    }

    /// <summary>Adds a 1-second sample and redraws at most once per second.</summary>
    public void PushSample(long bytesPerSecond, bool forceRedraw = false)
    {
        _history.Add(bytesPerSecond);
        Refresh(forceRedraw);
    }

    public void Refresh(bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && _lastRedrawTicks != 0 && (now - _lastRedrawTicks) < Stopwatch.Frequency * 0.95)
            return;

        _lastRedrawTicks = now;
        _samples = _history.Snapshot();
        InvalidateVisual();
    }

    static Brush GetThemeBrush(string key)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b)
        {
            var copy = new SolidColorBrush(b.Color);
            copy.Freeze();
            return copy;
        }
        return Brushes.Transparent;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 40 || h < 30) return;

        var surfaceBrush = GetThemeBrush("Surface");
        var lineBrush = GetThemeBrush("Line");
        var accentBrush = GetThemeBrush("Accent");
        var mutedBrush = GetThemeBrush("Muted");

        Color accentColor = (accentBrush as SolidColorBrush)?.Color ?? default;
        var areaBrush = Freeze(new LinearGradientBrush(
            Color.FromArgb(0x55, accentColor.R, accentColor.G, accentColor.B),
            Color.FromArgb(0x08, accentColor.R, accentColor.G, accentColor.B),
            new Point(0, 0),
            new Point(0, 1)));

        var borderPen = Freeze(new Pen(lineBrush, 1.0));
        var gridPen = Freeze(new Pen(lineBrush, 1.0) { DashStyle = DashStyles.Dot });
        var strokePen = Freeze(new Pen(accentBrush, 1.75));

        dc.DrawRoundedRectangle(surfaceBrush, borderPen, new Rect(0, 0, w, h), 8, 8);

        const double padLeft = 12;
        const double padRight = 84;
        const double padTop = 22;
        const double padBottom = 10;
        double plotW = Math.Max(10, w - padLeft - padRight);
        double plotH = Math.Max(10, h - padTop - padBottom);
        var plotRect = new Rect(padLeft, padTop, plotW, plotH);

        // Compute scale max (minimum 64 KB/s so idle flatline looks clean)
        long peak = 0;
        for (int i = 0; i < _samples.Length; i++)
            if (_samples[i] > peak) peak = _samples[i];
        long current = _samples.Length > 0 ? _samples[^1] : 0;
        double scaleMax = Math.Max(64 * 1024.0, peak * 1.15);

        // Horizontal grid lines (top, middle, bottom)
        double midY = plotRect.Top + plotH * 0.5;
        dc.DrawLine(gridPen, new Point(plotRect.Left, plotRect.Top), new Point(plotRect.Right, plotRect.Top));
        dc.DrawLine(gridPen, new Point(plotRect.Left, midY), new Point(plotRect.Right, midY));
        dc.DrawLine(borderPen, new Point(plotRect.Left, plotRect.Bottom), new Point(plotRect.Right, plotRect.Bottom));

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi <= 0) dpi = 1.0;

        // Header text (title + current + peak)
        var curStr = current > 0 ? Fmt.Speed(current) : Loc.T($"stats.graph.idle");
        var peakStr = peak > 0 ? Fmt.Speed(peak) : "0 " + Loc.T("core.unit.b") + Loc.T("core.unit.per_sec");
        var headerStr = $"{Loc.T($"stats.graph.title")}   ·   {Loc.T($"stats.graph.current", curStr)}   ·   {Loc.T($"stats.graph.peak", peakStr)}";
        var headerText = new FormattedText(
            headerStr,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            GraphTypeface,
            11.5,
            mutedBrush,
            dpi);
        dc.DrawText(headerText, new Point(padLeft, 4));

        // Y-axis labels on the right using Fmt.Speed
        var topAxisLabel = new FormattedText(
            Fmt.Speed(scaleMax),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            GraphTypeface,
            10.5,
            mutedBrush,
            dpi);
        var midAxisLabel = new FormattedText(
            Fmt.Speed(scaleMax * 0.5),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            GraphTypeface,
            10.5,
            mutedBrush,
            dpi);
        dc.DrawText(topAxisLabel, new Point(plotRect.Right + 6, plotRect.Top - 5));
        dc.DrawText(midAxisLabel, new Point(plotRect.Right + 6, midY - 5));

        if (_samples.Length == 0)
            return;

        int cap = Math.Max(2, _history.Capacity);
        int offset = Math.Max(0, cap - _samples.Length);

        Point PointAt(int i)
        {
            double xRatio = (offset + i) / (double)(cap - 1);
            double x = plotRect.Left + xRatio * plotW;
            double yRatio = Math.Clamp(_samples[i] / scaleMax, 0.0, 1.0);
            double y = plotRect.Bottom - yRatio * plotH;
            return new Point(x, y);
        }

        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            var first = PointAt(0);
            ctx.BeginFigure(new Point(first.X, plotRect.Bottom), isFilled: true, isClosed: true);
            ctx.LineTo(first, isStroked: false, isSmoothJoin: false);
            for (int i = 1; i < _samples.Length; i++)
                ctx.LineTo(PointAt(i), isStroked: false, isSmoothJoin: false);
            var last = PointAt(_samples.Length - 1);
            ctx.LineTo(new Point(last.X, plotRect.Bottom), isStroked: false, isSmoothJoin: false);
        }
        fillGeo.Freeze();

        var lineGeo = new StreamGeometry();
        using (var ctx = lineGeo.Open())
        {
            ctx.BeginFigure(PointAt(0), isFilled: false, isClosed: false);
            for (int i = 1; i < _samples.Length; i++)
                ctx.LineTo(PointAt(i), isStroked: true, isSmoothJoin: true);
        }
        lineGeo.Freeze();

        dc.PushClip(new RectangleGeometry(plotRect));
        dc.DrawGeometry(areaBrush, null, fillGeo);
        dc.DrawGeometry(null, strokePen, lineGeo);
        dc.Pop();
    }
}
