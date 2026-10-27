using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Swivel.Core;

namespace Swivel.Windows;

/// Draws Swivel's original folder icon: a tinted folder with an optional short badge.
internal static class FolderStyleIcons
{
    public static byte[] Icon(FolderStyle style)
        => FolderStyles.Icon(FolderStyles.IconSizes.Select(size => (size, Png(style, size))).ToArray());

    public static BitmapSource Bitmap(FolderStyle style, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) Draw(context, style, size);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Png(FolderStyle style, int size)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(style, size)));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static Color Shade(uint rgb, double factor)
    {
        byte Channel(int shift) => (byte)Math.Clamp(Math.Round(((rgb >> shift) & 0xFF) * factor), 0, 255);
        return Color.FromRgb(Channel(16), Channel(8), Channel(0));
    }

    private static void Draw(DrawingContext context, FolderStyle style, double s)
    {
        uint rgb = FolderStyles.Colors[style.Color].Rgb;
        double corner = Math.Max(1, s * 0.06);
        // Back panel with its tab, then the front panel slightly lower and lighter.
        var back = new StreamGeometry();
        using (var g = back.Open())
        {
            g.BeginFigure(new Point(s * 0.06, s * 0.20), true, true);
            g.LineTo(new Point(s * 0.40, s * 0.20), true, false);
            g.LineTo(new Point(s * 0.48, s * 0.28), true, false);
            g.LineTo(new Point(s * 0.94, s * 0.28), true, false);
            g.LineTo(new Point(s * 0.94, s * 0.84), true, false);
            g.LineTo(new Point(s * 0.06, s * 0.84), true, false);
        }
        back.Freeze();
        context.DrawGeometry(new SolidColorBrush(Shade(rgb, 0.78)), null, back);
        var front = new RectangleGeometry(new Rect(s * 0.06, s * 0.36, s * 0.88, s * 0.48), corner, corner);
        var fill = new LinearGradientBrush(Shade(rgb, 1.12), Shade(rgb, 0.96), 90);
        context.DrawGeometry(fill, null, front);
        if (style.Badge.Length == 0 || s < 24) return;
        var text = new FormattedText(style.Badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Emoji, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            s * 0.26, Brushes.White, 1.0);
        double scale = Math.Min(1, s * 0.76 / Math.Max(1, text.Width));
        context.PushTransform(new ScaleTransform(scale, scale, s / 2, s * 0.60));
        context.DrawText(text, new Point((s - text.Width) / 2, s * 0.60 - text.Height / 2));
        context.Pop();
    }
}

/// Choose a colour and badge, or restore Explorer's default, for 1–20 folders.
internal sealed class FolderStyleDialog : Window
{
    public bool Restore { get; private set; }
    public FolderStyle Chosen => new(color, badge.Text);
    private int color;
    private readonly TextBox badge = new() { MaxLength = FolderStyles.MaxBadgeUnits, Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Image preview = new() { Width = 96, Height = 96, Margin = new Thickness(0, 4, 16, 8) };
    private readonly TextBlock problem = UI.Text("", 12);

    public FolderStyleDialog(App app, IReadOnlyList<(string Path, FolderStyleState State, string? Problem)> folders)
    {
        Title = app.L("Change Folder Style…");
        MoveFilesLayout.Configure(this);
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel();
        var apply = UI.Button(app.L("Apply Style"), () =>
        {
            try { Chosen.Validate(); DialogResult = true; }
            catch (UserError error) { problem.Text = app.L(error.Message); }
        });
        var restore = UI.Button(app.L("Restore Default"), () => { Restore = true; DialogResult = true; });
        restore.IsEnabled = folders.Any(f => f.State == FolderStyleState.Swivel);
        apply.IsEnabled = folders.Any(f => f.State is FolderStyleState.None or FolderStyleState.Swivel);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false);
        cancel.IsCancel = true; apply.IsDefault = true;
        buttons.Children.Add(apply); buttons.Children.Add(restore); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

        var top = new StackPanel();
        top.Children.Add(UI.Text(app.L("Explorer shows a folder's own icon from a hidden desktop.ini file. Swivel adds two hidden system files to each folder (desktop.ini and an icon) and sets the folder's Read-only attribute if it is not set yet, which is how Explorer recognises the icon. Read-only on a folder does not stop changes to the files inside it."), 12));
        top.Children.Add(UI.Text(app.L("If Explorer's Customize tab or a sync service later rewrites desktop.ini, it is no longer recognised as Swivel's and Restore Default leaves it alone; remove the style in Explorer's folder Properties instead."), 12));
        top.Children.Add(UI.Text(app.L("Restore Default removes exactly these two files and clears Read-only only if Swivel set it. Folders that already have their own desktop.ini are left unchanged. Explorer may need a moment, or a refresh, to show the change; sync services such as OneDrive also sync the two files."), 12));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(preview);
        var choices = new StackPanel();
        choices.Children.Add(UI.Text(app.L("Colour"), 14));
        var swatches = new WrapPanel { MaxWidth = 380 };
        var toggles = new List<ToggleButton>();
        for (int i = 0; i < FolderStyles.Colors.Count; i++)
        {
            int index = i; uint rgb = FolderStyles.Colors[i].Rgb;
            var toggle = new ToggleButton
            {
                Width = 34, Height = 34, Margin = new Thickness(0, 0, 6, 6), IsChecked = i == 0, ToolTip = app.L(FolderStyles.Colors[i].Name),
                Content = new Rectangle { Width = 20, Height = 20, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)) },
            };
            AutomationProperties.SetName(toggle, app.L(FolderStyles.Colors[i].Name));
            toggle.Click += (_, _) => { color = index; foreach (var t in toggles) t.IsChecked = t == toggle; Update(); };
            toggles.Add(toggle); swatches.Children.Add(toggle);
        }
        choices.Children.Add(swatches);
        choices.Children.Add(UI.Text(app.L("Badge (optional, up to two characters)"), 14));
        AutomationProperties.SetName(badge, app.L("Badge (optional, up to two characters)"));
        badge.TextChanged += (_, _) => Update();
        choices.Children.Add(badge);
        choices.Children.Add(problem);
        row.Children.Add(choices);
        top.Children.Add(row);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

        var list = new ListBox { Margin = new Thickness(0, 8, 0, 12) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(list, app.L("Folders"));
        list.ItemsSource = folders.Select(f => f.Path + " — " + (f.State == FolderStyleState.Unavailable ? app.L("Unavailable (left unchanged)") + ": " + f.Problem : app.L(f.State switch
        {
            FolderStyleState.Swivel => "Swivel style (will be replaced)",
            FolderStyleState.Other => "Has its own desktop.ini (left unchanged)",
            _ => "Explorer default",
        }))).ToArray();
        root.Children.Add(list);
        Content = root;
        Update();
        Loaded += (_, _) => cancel.Focus();

        void Update()
        {
            try { FolderStyles.ValidateBadge(badge.Text); problem.Text = ""; preview.Source = FolderStyleIcons.Bitmap(Chosen, 96); }
            catch (UserError error) { problem.Text = app.L(error.Message); preview.Source = FolderStyleIcons.Bitmap(new FolderStyle(color), 96); }
        }
    }
}
