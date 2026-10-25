using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class UsageNotices
{
    internal const string Scope = "Read-only CPU time and physical memory from local Windows APIs, about every 2 seconds while this window is visible and not minimized. Pause, hiding, minimizing or closing stops new reads. Closing waits for any read already in progress. No process list, file scan or automatic network request is used. Optional assistant requests require separate review and explicit sending.";
    internal const string Limits = "The chart retains up to 120 seconds relative to its last refresh and at most 61 samples. Pausing freezes the chart. Missing values and pauses leave gaps. CPU needs two valid samples; resuming starts a new baseline. CPU is unavailable on systems with multiple processor groups.";
    internal const string Meaning = "Physical memory in use is total minus available memory. Windows available memory includes standby, free and zero-page lists. These volatile values are not a leak diagnosis, cleanup recommendation or a promise to free memory. Reported-path app memory snapshots are available separately. Windows Doctor offers a separate list for reviewing a normal window-close request. Helper ownership and cleanup remain planned.";
    internal const string AssistantScope="Explain Redacted Snapshot freezes only the latest recorded CPU and physical-memory sample, then pauses monitoring. It does not add chart history, process names or paths. Review the complete snapshot and service request before sending. Closing the explanation leaves monitoring paused; press Resume Monitoring to start a new CPU baseline.";
}

internal sealed class UsageMonitorDialog : Window
{
    private readonly App app;
    private readonly CancellationToken cancellation;
    private readonly UsageSampler sampler;
    private readonly UsageHistory history = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock cpu, memory, status;
    private readonly UsageChart cpuChart = new(true), memoryChart = new(false);
    private readonly UsageRingGauge cpuGauge = new(), memoryGauge = new();
    private readonly Button pause, copy, explain;
    private UsagePoint? latest;
    private bool paused, sampling, closed;
    private long samplingVersion;
    private Task assistantPending=Task.CompletedTask;private DoctorAssistantSession? assistant;
    internal UsageHistory History => history;
    internal bool Sampling => sampling;
    internal string CurrentSnapshot
    {
        get
        {
            var text = new StringBuilder(); text.AppendLine(app.L("CPU and Physical Memory"));
            text.AppendLine(status.Text); text.AppendLine(cpu.Text); text.AppendLine(memory.Text);
            if (latest is not null) text.AppendLine(string.Format(app.L("Sample time: {0:F1} s since opening"), latest.Seconds));
            text.AppendLine(app.L(UsageNotices.Scope)); text.AppendLine(app.L(UsageNotices.Limits)); text.AppendLine(app.L(UsageNotices.Meaning));
            return text.ToString();
        }
    }
    public UsageMonitorDialog(App app, CancellationToken token, Func<CancellationToken,UsageReading>? read = null)
    {
        this.app = app; cancellation = token; sampler = new(read ?? UsageReader.Read, token);
        Title = app.L("CPU and Physical Memory"); Width = 780; Height = 850; MinWidth = 500; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) };
        var footer = new WrapPanel(); DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer);
        pause = UI.Button(app.L("Pause Monitoring"), () => { paused = !paused; UpdateSampling(); }); footer.Children.Add(pause);
        copy = UI.Button(app.L("Copy Current Snapshot"), () => app.Try(() => Clipboard.SetText(CurrentSnapshot))); copy.IsEnabled = false; footer.Children.Add(copy);
        explain=UI.Button(app.L("Explain Redacted Snapshot…"),()=>{});explain.IsEnabled=false;explain.Click+=Explain;footer.Children.Add(explain);
        var close = UI.Button(app.L("Close"),() => Close()); close.IsCancel = true; footer.Children.Add(close);
        var body = new StackPanel(); root.Children.Add(UI.Scroll(body)); Content = root;
        status = UI.Text(app.L("Waiting for the first sample…"),14); body.Children.Add(status);
        body.Children.Add(UI.Text(app.L(UsageNotices.Scope),12)); body.Children.Add(UI.Text(app.L(UsageNotices.Limits),12));
        var readings = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        readings.ColumnDefinitions.Add(new ColumnDefinition()); readings.ColumnDefinitions.Add(new ColumnDefinition());
        var cpuReading = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        cpu = UI.Text(app.L("CPU: waiting for two valid samples…"),18); cpuReading.Children.Add(cpu);
        AutomationProperties.SetName(cpuGauge, app.L("CPU Usage Gauge")); cpuReading.Children.Add(cpuGauge);
        Grid.SetColumn(cpuReading, 0); readings.Children.Add(cpuReading);
        var memoryReading = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        memory = UI.Text(app.L("Physical memory: unavailable"),18); memoryReading.Children.Add(memory);
        AutomationProperties.SetName(memoryGauge, app.L("Physical Memory Usage Gauge")); memoryReading.Children.Add(memoryGauge);
        Grid.SetColumn(memoryReading, 1); readings.Children.Add(memoryReading);
        body.Children.Add(readings);
        AutomationProperties.SetName(cpuChart,app.L("CPU Usage Chart")); body.Children.Add(cpuChart);
        AutomationProperties.SetName(memoryChart,app.L("Physical Memory Usage Chart")); body.Children.Add(memoryChart);
        body.Children.Add(UI.Text(app.L("Chart: 0–100%, latest 120 seconds; left is older, right is the latest sample."),12));
        body.Children.Add(UI.Text(app.L(UsageNotices.Meaning),12));
        body.Children.Add(UI.Text(app.L("Copy Current Snapshot copies only the displayed values and limits when you press it. History is not saved or sent automatically."),12));
        body.Children.Add(UI.Text(app.L(UsageNotices.AssistantScope),12));
        timer.Tick += Tick;
        Loaded += (_,_) => UpdateSampling();
        IsVisibleChanged += (_,_) => UpdateSampling(); StateChanged += (_,_) => UpdateSampling();
        Closed += (_,_) => { closed = true; UpdateSampling(); timer.Tick -= Tick; };
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private async void Tick(object? sender, EventArgs args) => await PollAsync();
    private void UpdateSampling()
    {
        bool enable = !closed && !cancellation.IsCancellationRequested && IsLoaded && IsVisible && WindowState != WindowState.Minimized && !paused;
        pause.Content = app.L(paused ? "Resume Monitoring" : "Pause Monitoring");
        if (enable == sampling) return;
        sampling = enable; samplingVersion++; sampler.SetEnabled(enable);
        if (enable)
        {
            status.Text = app.L("Monitoring about every 2 seconds…"); timer.Start(); _ = PollAsync();
        }
        else
        {
            timer.Stop(); history.Break(clock.Elapsed.TotalSeconds); RefreshCharts();
            status.Text = app.L(closed ? "Monitoring stopped." : "Paused. Resuming starts a new CPU baseline.");
        }
    }
    internal async Task PollAsync()
    {
        if (!sampling || closed || cancellation.IsCancellationRequested) return;
        long version=samplingVersion;
        UsageReading? value;
        try { value = await sampler.SampleAsync(); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { value = new(null,null,CpuSampleStatus.Unavailable); }
        if (value is null || version!=samplingVersion || !sampling || closed || cancellation.IsCancellationRequested) return;
        latest = history.Add(value,clock.Elapsed.TotalSeconds);
        cpu.Text = latest.CpuPercent is double percent ? string.Format(app.L("CPU: {0:F1}%"),percent)
            : app.L(value.CpuStatus == CpuSampleStatus.MultipleProcessorGroups ? "CPU: unavailable (multiple processor groups)"
                : value.CpuStatus == CpuSampleStatus.Available && value.Cpu.HasValue ? "CPU: waiting for two valid samples…" : "CPU: unavailable");
        memory.Text = latest.MemoryPercent is double used
            ? string.Format(app.L("Physical memory in use: {0:F1}% · {1:F2} of {2:F2} GiB"),used,latest.UsedBytes!.Value / 1073741824.0,latest.TotalBytes!.Value / 1073741824.0)
            : app.L("Physical memory: unavailable");
        cpuGauge.Update(latest.CpuPercent); memoryGauge.Update(latest.MemoryPercent);
        copy.IsEnabled = true;if(assistantPending.IsCompleted)explain.IsEnabled=true;RefreshCharts();
    }
    private async void Explain(object sender,RoutedEventArgs args)
    {
        if(closed||!assistantPending.IsCompleted||latest is not UsagePoint point)return;
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);assistantPending=completion.Task;explain.IsEnabled=false;pause.IsEnabled=false;
        try
        {
            cancellation.ThrowIfCancellationRequested();paused=true;UpdateSampling();
            var snapshot=DoctorAssistantSummary.Monitor(point);var session=assistant=new DoctorAssistantSession(app,this,new WrapPanel(),snapshot,cancellation,attachButton:false);
            try{await session.OpenAsync();}finally{await session.FinishAsync();}
        }
        catch(OperationCanceledException){}
        catch(Exception error){if(!closed)app.ShowError(error);}
        finally{assistant=null;if(!closed){status.Text=app.L("Monitoring remains paused. Press Resume Monitoring to start a new CPU baseline.");pause.IsEnabled=true;explain.IsEnabled=latest is not null;}completion.TrySetResult();}
    }
    private void RefreshCharts()
    {
        double now = clock.Elapsed.TotalSeconds; cpuChart.Update(history,now); memoryChart.Update(history,now);
        AutomationProperties.SetHelpText(cpuChart,cpu.Text); AutomationProperties.SetHelpText(memoryChart,memory.Text);
        AutomationProperties.SetHelpText(cpuGauge,cpu.Text); AutomationProperties.SetHelpText(memoryGauge,memory.Text);
    }
    internal async Task StopAsync()
    {
        closed = true; UpdateSampling(); timer.Tick -= Tick;explain.IsEnabled=false;explain.Click-=Explain;
        var finishing=FinishAssistantAsync();try{await sampler.DisposeAsync();}finally{await finishing;}
    }
    private async Task FinishAssistantAsync(){try{if(assistant is not null)await assistant.FinishAsync();}finally{await assistantPending;}}
}

// Compact native-system-color readouts supplement the bounded history charts.
// They display only the latest percentage and render missing samples as gaps.
internal sealed class UsageRingGauge : FrameworkElement
{
    private double? value;
    internal UsageRingGauge() { Width = 112; Height = 112; Margin = new Thickness(0, 4, 0, 8); }
    internal void Update(double? percent) { value = percent is double n && double.IsFinite(n) ? Math.Clamp(n, 0, 100) : null; InvalidateVisual(); }
    protected override Size MeasureOverride(Size availableSize) => new(112, 112);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        const double thickness = 10;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - thickness);
        if (radius <= 0) return;
        var track = new Pen(SystemColors.GrayTextBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawEllipse(null, track, center, radius, radius);
        if (value is double percent && percent > 0)
        {
            if (percent >= 100)
                dc.DrawEllipse(null, new Pen(SystemColors.HotTrackBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, center, radius, radius);
            else
            {
                double angle = -90 + percent * 3.6;
                var start = new Point(center.X, center.Y - radius);
                var end = new Point(center.X + radius * Math.Cos(angle * Math.PI / 180), center.Y + radius * Math.Sin(angle * Math.PI / 180));
                var figure = new PathFigure { StartPoint = start, IsClosed = false };
                figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, percent > 50, SweepDirection.Clockwise, true));
                var geometry = new PathGeometry(); geometry.Figures.Add(figure); geometry.Freeze();
                dc.DrawGeometry(null, new Pen(SystemColors.HotTrackBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
            }
        }
        string label = value is double v ? v.ToString("F0", CultureInfo.CurrentCulture) + "%" : "—";
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 18, SystemColors.WindowTextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}

// Two bounded native-window charts. Each missing value begins a new figure;
// there is no interpolation through unavailable samples or paused intervals.
internal sealed class UsageChart(bool cpu) : FrameworkElement
{
    private UsagePoint[] points = Array.Empty<UsagePoint>();
    private double now;
    public UsageChart() : this(true) { }
    protected override Size MeasureOverride(Size availableSize) => new(double.IsFinite(availableSize.Width) ? availableSize.Width : 500,170);
    internal void Update(UsageHistory history,double seconds) { points = history.Points.ToArray(); now = seconds; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); double w = ActualWidth, h = ActualHeight;
        if (w < 70 || h < 50) return;
        var chart = new Rect(40,10,w-50,h-35);
        dc.DrawRectangle(SystemColors.WindowBrush,new Pen(SystemColors.GrayTextBrush,1),chart);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (int level in new[] { 0,50,100 })
        {
            double y = chart.Bottom - chart.Height * level / 100;
            dc.DrawLine(new Pen(SystemColors.GrayTextBrush,0.5),new(chart.Left,y),new(chart.Right,y));
            var label = new FormattedText(level.ToString(CultureInfo.CurrentCulture)+"%",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
                new Typeface(SystemFonts.MessageFontFamily,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),11,SystemColors.WindowTextBrush,dpi);
            dc.DrawText(label,new Point(0,y-7));
        }
        var geometry = new StreamGeometry();
        using (var pen = geometry.Open())
        {
            bool drawing = false;
            foreach (var point in points)
            {
                double? value = cpu ? point.CpuPercent : point.MemoryPercent;
                double age = now - point.Seconds;
                if (value is null || age < 0 || age > UsageHistory.WindowSeconds) { drawing = false; continue; }
                var position = new Point(chart.Right - chart.Width * age / UsageHistory.WindowSeconds,chart.Bottom - chart.Height * value.Value / 100);
                if (!drawing) { pen.BeginFigure(position,false,false); drawing = true; }
                else pen.LineTo(position,true,false);
                dc.DrawEllipse(SystemColors.HotTrackBrush,null,position,2,2);
            }
        }
        geometry.Freeze(); dc.DrawGeometry(null,new Pen(SystemColors.HotTrackBrush,2),geometry);
    }
}
