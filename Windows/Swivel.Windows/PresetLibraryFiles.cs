using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class PresetLibraryFile : IDisposable
{
    private readonly NativeFiles.Input input;
    public PresetImport Import { get; }
    private PresetLibraryFile(NativeFiles.Input input, PresetImport import) { this.input = input; Import = import; }
    public static PresetLibraryFile Open(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var input = NativeFiles.OpenRead(path);
        try
        {
            if (input.Stream.Length > PresetLibrary.MaxBytes) throw new UserError("Choose a valid Swivel image preset JSON file (64 KB, up to 24 presets).");
            byte[] data = new byte[checked((int)input.Stream.Length)]; input.Stream.ReadExactly(data);
            token.ThrowIfCancellationRequested(); return new(input, PresetLibrary.Decode(data));
        }
        catch { input.Dispose(); throw; }
    }
    public static string Save(IReadOnlyList<ImagePreset> presets, string folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); byte[] data = PresetLibrary.Encode(presets);
        return NativeFiles.WriteInDirectory(folder, "Swivel image presets", "json", stream => stream.Write(data), token);
    }
    public void Dispose() => input.Dispose();
}

internal sealed class PresetLibraryPreview : Window
{
    public PresetLibraryPreview(App app, PresetImport import, bool importing, int additions = 0, string? mergeIssue = null)
    {
        Title = app.L(importing ? "Import Image Presets…" : "Export Image Presets…");
        Width = 690; Height = 640; MinWidth = 440; MinHeight = 340; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var actions = new WrapPanel();
        var apply = UI.Button(app.L(importing ? "Import Compatible Presets" : "Choose Backup Folder…"), () => DialogResult = true);
        apply.IsEnabled = !importing || additions > 0 && mergeIssue is null; actions.Children.Add(apply);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var body = new StackPanel();
        body.Children.Add(UI.Text(app.L(importing ? "Review every preset. Identical names and settings are skipped; conflicting names stop the import. Only compatible entries will be added, with new local IDs. Feature switches and existing menu choices stay unchanged." :
            "This JSON backup includes preset names and all Windows recipe values, including adjustments and per-image Auto. Local IDs, menu choices and feature switches are excluded.")));
        if (import.Compatible.Any(preset => preset.Recipe.Format == "gif"))
        { body.Children.Add(UI.Text(app.L(GifExport.Notice))); body.Children.Add(UI.Text(app.L(ImageRecipe.GifNotice))); body.Children.Add(UI.Text(app.L(ImageRecipe.GifDowngrade))); }
        if (import.FromMac) body.Children.Add(UI.Text(app.L("Mac preset JSON detected. Only JPEG, PNG and TIFF with metadata removal and supported sizes can be imported. Windows rendering and JPEG quality may differ from Mac.")));
        if (mergeIssue is not null) body.Children.Add(UI.Text(app.L(mergeIssue)));
        else if (importing) body.Children.Add(UI.Text(string.Format(app.L("{0} new preset(s) will be added."), additions), 18));
        foreach (var row in import.Rows)
        {
            body.Children.Add(UI.Text(LibraryJson.Visible(row.Name), 20));
            body.Children.Add(UI.Text(row.Description));
            if (row.Issue is not null) body.Children.Add(UI.Text(app.L("Not imported") + " · " + app.L(row.Issue)));
            if (row.Preset is { } preset)
            {
                if (preset.Recipe.LongEdge == 0) body.Children.Add(UI.Text(app.L("Keep original dimensions"), 12));
                body.Children.Add(UI.Text(app.L("Compatible") + " · " + app.L(preset.Recipe.AutoPerImage ? "Per-image Auto" : "Fixed recipe")));
                if (preset.Recipe.Adjustments is { } photo)
                {
                    var fields = new (string Label, double Value)[] {
                        ("Exposure", photo.Exposure), ("Contrast", photo.Contrast), ("Highlights", photo.Highlights), ("Shadows", photo.Shadows),
                        ("Whites", photo.Whites), ("Blacks", photo.Blacks), ("Saturation", photo.Saturation), ("Vibrance", photo.Vibrance),
                        ("Warmth", photo.Warmth), ("Tint", photo.Tint), ("Clarity", photo.Clarity), ("Sharpen", photo.Sharpen),
                        ("Noise Reduction", photo.Noise), ("Vignette", photo.Vignette), ("Look Strength", photo.LookStrength)
                    };
                    foreach (var field in fields) body.Children.Add(UI.Text(app.L(field.Label) + ": " + field.Value.ToString("G", CultureInfo.CurrentCulture), 12));
                    body.Children.Add(UI.Text(app.L("Look") + ": " + app.L(photo.Look.ToString()), 12));
                }
            }
        }
        root.Children.Add(UI.Scroll(body)); Content = root;
    }
}
