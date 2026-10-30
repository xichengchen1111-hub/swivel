using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

// No export method: the original and its ancestors stay held through the modal session.
internal sealed class FileTextPreview : IDisposable
{
    private readonly NativeFiles.Input input;
    internal string Path => input.Path;
    internal string Text { get; }
    private FileTextPreview(NativeFiles.Input input, string text) { this.input = input; Text = text; }
    internal static FileTextPreview Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var input = NativeFiles.OpenRead(path);
        try
        {
            bool subtitle = FilePreview.Kind(path) == FilePreviewKind.Subtitle;
            int max = subtitle ? SubtitleFormats.MaxBytes : DataFileFormats.MaxBytes;
            if (input.Stream.Length > max) throw new UserError(subtitle ? SubtitleFormats.TooLarge : DataFileFormats.TooLarge);
            var bytes = new byte[checked((int)input.Stream.Length)];
            input.Stream.ReadExactly(bytes); token.ThrowIfCancellationRequested();
            return new(input, FilePreview.ReadText(bytes, path, token));
        }
        catch { input.Dispose(); throw; }
    }
    public void Dispose() => input.Dispose();
}

internal sealed class FileTextPreviewDialog : Window
{
    internal FileTextPreviewDialog(App app, FileTextPreview file)
    {
        Title = app.L("Preview File"); Width = 780; Height = 700; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var close = UI.Button(app.L("Close"), () => DialogResult = false); close.IsCancel = true;
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var body = new StackPanel();
        body.Children.Add(new TextBox { Text = file.Path, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(UI.Text(app.L(FilePreview.TextNotice), 12));
        body.Children.Add(UI.Text(app.L("Plain Text Preview")));
        if (file.Text.Length > DocumentTextDialog.PreviewLimit) body.Children.Add(UI.Text(app.L("Preview is limited to the first 65,536 UTF-16 units. No file is saved from this preview.")));
        body.Children.Add(new TextBox { Text = DocumentTextDialog.Preview(file.Text), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 200, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(UI.Scroll(body)); Content = root;
        SourceInitialized += (_, _) => WebRouteLayout.Fit(this, Owner ?? this);
        DpiChanged += (_, _) => WebRouteLayout.Fit(this, this);
    }
}
