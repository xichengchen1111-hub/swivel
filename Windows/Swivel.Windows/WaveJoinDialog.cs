using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class WaveJoinDialog : Window
{
    private Task pending = Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private WaveDialog.PreviewSound? sound;
    private bool closed;
    public WaveJoinDialog(App app, WaveJoinDocument document)
    {
        var info = document.Info.Format;
        var order = Enumerable.Range(0, document.Paths.Count).ToList();
        Title = app.L("Join WAV Files…"); Width = 800; Height = 720; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) }; var panel = new StackPanel();
        panel.Children.Add(UI.Text(string.Format(app.L("{0:0.###} seconds · {1} Hz · {2} channel(s) · {3}-bit PCM"), document.Info.Seconds, info.Rate, info.Channels, info.Bits), 18));
        panel.Children.Add(UI.Text(app.L("Join 2–32 integer PCM WAV files in the displayed order. Every sample value is preserved with no gap, resampling, gain or crossfade. Abrupt joins can click. Metadata and markers are removed.")));
        panel.Children.Add(UI.Text(app.L(WaveJoin.Limit), 12));
        panel.Children.Add(UI.Text(app.L("All sources must have the same sample rate, channel count and bit depth. Use the separate conversion tools first if needed. Duplicate path spellings are removed by file selection; different hard-link paths can repeat audio and count again toward the source budget."), 12));
        panel.Children.Add(UI.Text(app.L("Preview plays the first 10 seconds of the complete order as PCM16, matching the saved WAV decoded to PCM16. All sources and the full joined output are validated even for preview. Save creates a new WAV beside the first file in this list without replacing any source."), 12));
        var list = new ListBox { Height = 180, SelectionMode = SelectionMode.Single, Margin = new Thickness(0, 8, 0, 8) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto); panel.Children.Add(list);
        var destination = UI.Text("", 12); panel.Children.Add(destination);
        var moves = new WrapPanel(); panel.Children.Add(moves);
        var up = UI.Button(app.L("Move Up"), () => { }); var down = UI.Button(app.L("Move Down"), () => { }); moves.Children.Add(up); moves.Children.Add(down);
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        list.SelectionChanged += (_, _) => EnableMoves();
        void EnableMoves() { up.IsEnabled = pending.IsCompleted && list.SelectedIndex > 0; down.IsEnabled = pending.IsCompleted && list.SelectedIndex >= 0 && list.SelectedIndex < order.Count - 1; }
        void Refresh(int selected)
        {
            list.ItemsSource = order.Select((index, i) => (i + 1) + ". " + document.Paths[index]).ToArray(); list.SelectedIndex = selected;
            destination.Text = string.Format(app.L("Output beside first file: {0}"), document.Paths[order[0]]); EnableMoves();
        }
        void Move(int delta)
        {
            if (!pending.IsCompleted) return; int selected = list.SelectedIndex, target = selected + delta;
            if (selected < 0 || target < 0 || target >= order.Count) return;
            Stop(); (order[selected], order[target]) = (order[target], order[selected]); Refresh(target);
        }
        Refresh(0);
        var footer = new StackPanel(); var status = UI.Text(""); footer.Children.Add(status); var buttons = new WrapPanel(); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(UI.Scroll(panel)); Content = root;
        var preview = UI.Button(app.L("Play Audio Preview"), () => { });
        var save = UI.Button(app.L("Save WAV"), () => { Order = order.ToArray(); Stop(); DialogResult = true; });
        preview.Click += (_, _) => { if (pending.IsCompleted) { var snapshot = order.ToArray(); pending = Play(snapshot); } };
        buttons.Children.Add(preview); buttons.Children.Add(UI.Button(app.L("Stop"), Stop)); buttons.Children.Add(save);
        var cancel = UI.Button(app.L("Cancel"), () => { Stop(); DialogResult = false; }); cancel.IsCancel = true; buttons.Children.Add(cancel);
        SourceInitialized += (_, _) => FitWorkArea(Owner ?? this); DpiChanged += (_, _) => FitWorkArea(this);
        Closed += (_, _) => { closed = true; Stop(); };
        async Task Play(int[] snapshot)
        {
            Stop(); var owned = cancellation = new CancellationTokenSource(); var token = owned.Token;
            save.IsEnabled = preview.IsEnabled = up.IsEnabled = down.IsEnabled = false; status.Text = app.L("Preparing audio preview…"); WaveDialog.PreviewSound? prepared = null;
            try
            {
                prepared = await Task.Run(() => WaveDialog.PreviewSound.Create(document.Preview(snapshot, token), token));
                token.ThrowIfCancellationRequested(); if (closed) return;
                sound = prepared; prepared = null; sound.Play(); status.Text = app.L("Playing up to 10 seconds. Stop ends playback.");
            }
            catch (OperationCanceledException) { if (!closed) status.Text = app.L("Cancelled."); }
            catch (Exception ex) { if (!closed) status.Text = ex is UserError error ? app.L(error.Message) : app.L("Audio preview could not play. You can still save the WAV."); }
            finally
            {
                prepared?.Dispose(); if (ReferenceEquals(cancellation, owned)) cancellation = null; owned.Dispose();
                if (!closed) { save.IsEnabled = preview.IsEnabled = true; up.IsEnabled = list.SelectedIndex > 0; down.IsEnabled = list.SelectedIndex >= 0 && list.SelectedIndex < order.Count - 1; }
            }
        }
    }
    public int[] Order { get; private set; } = Array.Empty<int>();
    private void FitWorkArea(Window reference)
    {
        var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(reference).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(reference);
        double height = Math.Max(1, area.Height / dpi.DpiScaleY - 16), width = Math.Max(1, area.Width / dpi.DpiScaleX - 16);
        MinHeight = Math.Min(400, height); MinWidth = Math.Min(500, width); MaxHeight = height; MaxWidth = width;
        Height = Math.Min(Height, height); Width = Math.Min(Width, width);
    }
    private void Stop() { cancellation?.Cancel(); sound?.Dispose(); sound = null; }
    public async Task FinishPreviewAsync() { Stop(); await pending; }
}
