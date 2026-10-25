using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class CompressionDialog : Window
{
    public int Quality { get; private set; } = 85;
    public long? TargetBytes { get; private set; }
    public CompressionDialog(App app, int count)
    {
        Title = app.L("Compress JPEG…"); Width = 480; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(UI.Text(string.Format(app.L("Apply the same quality to {0} image(s)."), count), 18));
        panel.Children.Add(UI.Text(app.L("JPEG quality (1–100)")));
        var quality = new Slider { Minimum = 1, Maximum = 100, Value = 85, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10 };
        AutomationProperties.SetName(quality, app.L("JPEG quality (1–100)"));
        var value = UI.Text("85");
        quality.ValueChanged += (_, _) => value.Text = ((int)quality.Value).ToString(CultureInfo.InvariantCulture);
        panel.Children.Add(quality); panel.Children.Add(value);
        panel.Children.Add(UI.Text(app.L("New JPEG files keep the image dimensions. Transparency becomes white and metadata is removed. A lower quality can lose detail; a smaller file is not guaranteed.")));
        var limit = new CheckBox { Content = app.L("Fit a maximum file size"), Margin = new Thickness(0, 8, 0, 8) }; panel.Children.Add(limit);
        panel.Children.Add(UI.Text(app.L("Target size (KB)"))); var size = new TextBox { Text = "500", IsEnabled = false, MinHeight = 30 }; panel.Children.Add(size); AutomationProperties.SetName(size, app.L("Target size (KB)"));
        limit.Click += (_, _) => { size.IsEnabled = limit.IsChecked == true; quality.IsEnabled = limit.IsChecked != true; };
        panel.Children.Add(UI.Text(app.L("Size mode measures JPEG candidates and keeps dimensions. If the target cannot be met, no file is saved for that image. Resize first if needed.")));
        var error = UI.Text(""); panel.Children.Add(error); var row = new WrapPanel();
        var apply = UI.Button(app.L("Apply"), () => {
            try {
                TargetBytes = null;
                if (limit.IsChecked == true) { if (!int.TryParse(size.Text, out int kb) || kb is < 4 or > 65536) throw new UserError("Choose a target size from 4 to 65536 KB."); TargetBytes = kb * 1024L; }
                Quality = (int)quality.Value; DialogResult = true;
            } catch (UserError ex) { error.Text = app.L(ex.Message); }
        }); apply.IsDefault = true; row.Children.Add(apply);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel);
        panel.Children.Add(row); Content = UI.Scroll(panel);
    }
}

internal sealed class PresetDialog : Window
{
    public ImagePreset? Preset { get; private set; }
    public PresetDialog(App app, ImagePreset? existing = null, PhotoSettings? photo = null)
    {
        Title = app.L(existing is null ? "New Preset…" : "Edit Preset…"); Width = 540; Height = 720; MinHeight = 440; MinWidth = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(UI.Text(app.L("Preset Name")));
        var name = new TextBox { Text = existing?.Name ?? "", MaxLength = 48, MinHeight = 30 }; panel.Children.Add(name);
        AutomationProperties.SetName(name, app.L("Preset Name"));
        panel.Children.Add(UI.Text(app.L("Output Format")));
        var format = new ComboBox { ItemsSource = new[] { "PNG", "JPEG", "BMP", "TIFF", "GIF" }, SelectedIndex = Array.IndexOf(ImageRecipe.Formats, existing?.Recipe.Format ?? "jpg"), MinHeight = 30 };
        AutomationProperties.SetName(format, app.L("Output Format")); panel.Children.Add(format);
        var gifNotes = new StackPanel { Visibility = format.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed };
        gifNotes.Children.Add(UI.Text(app.L(GifExport.Notice), 12)); gifNotes.Children.Add(UI.Text(app.L(ImageRecipe.GifNotice), 12)); gifNotes.Children.Add(UI.Text(app.L(ImageRecipe.GifDowngrade), 12)); panel.Children.Add(gifNotes);
        format.SelectionChanged += (_, _) => gifNotes.Visibility = format.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        var resize = new CheckBox { Content = app.L("Limit the long edge"), IsChecked = existing?.Recipe.LongEdge > 0, Margin = new Thickness(0, 14, 0, 4) }; panel.Children.Add(resize);
        panel.Children.Add(UI.Text(app.L("Maximum long edge (pixels)")));
        var edge = new TextBox { Text = (existing?.Recipe.LongEdge > 0 ? existing.Recipe.LongEdge : 1920).ToString(CultureInfo.InvariantCulture), MinHeight = 30, IsEnabled = resize.IsChecked == true };
        AutomationProperties.SetName(edge, app.L("Maximum long edge (pixels)")); panel.Children.Add(edge);
        resize.Click += (_, _) => edge.IsEnabled = resize.IsChecked == true;
        panel.Children.Add(UI.Text(app.L("JPEG quality (1–100)")));
        var quality = new TextBox { Text = (existing?.Recipe.Quality ?? 85).ToString(CultureInfo.InvariantCulture), MinHeight = 30, IsEnabled = format.SelectedIndex == 1 };
        AutomationProperties.SetName(quality, app.L("JPEG quality (1–100)")); panel.Children.Add(quality);
        format.SelectionChanged += (_, _) => quality.IsEnabled = format.SelectedIndex == 1;
        panel.Children.Add(UI.Text(app.L("Images never enlarge. Metadata is always removed. JPEG and BMP use white behind transparency. Preset names do not change output filenames.")));
        var adjustments = new PhotoRecipeControl(app,photo ?? existing?.Recipe.Adjustments,photo is null && existing?.Recipe.AutoPerImage == true); panel.Children.Add(adjustments);
        var error = UI.Text("");
        var row = new WrapPanel();
        var save = UI.Button(app.L("Save Preset"), () => {
            try
            {
                int limit = 0, level = format.SelectedIndex == 4 ? existing?.Recipe.Quality ?? 85 : 85;
                if (resize.IsChecked == true && (!int.TryParse(edge.Text, out limit) || limit is < 16 or > 8192)) throw new UserError("Choose a long edge from 16 to 8192 pixels.");
                if (format.SelectedIndex == 1 && (!int.TryParse(quality.Text, out level) || level is < 1 or > 100)) throw new UserError("Choose JPEG quality from 1 to 100.");
                var preset = new ImagePreset(existing?.Id ?? Guid.NewGuid().ToString("N"), name.Text.Trim(), new ImageRecipe(ImageRecipe.Formats[format.SelectedIndex], limit, level,adjustments.Read(),adjustments.AutoPerImage));
                preset.Validate();
                // Validate and save here so duplicate/limit errors leave the form editable.
                app.Prefs.SavePreset(preset); Preset = preset; DialogResult = true;
            }
            catch (UserError ex) { error.Text = app.L(ex.Message); }
        }); save.IsDefault = true; row.Children.Add(save);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel);
        var root = new DockPanel(); var footer = new StackPanel { Margin = new Thickness(24, 0, 24, 16) }; footer.Children.Add(error); footer.Children.Add(row); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(UI.Scroll(panel)); Content = root;
        SourceInitialized += (_, _) => FitWorkArea(Owner ?? this); DpiChanged += (_, _) => FitWorkArea(this);
        Loaded += (_, _) => name.Focus();
    }
    private void FitWorkArea(Window reference)
    {
        var area = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(reference).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(reference); double height = Math.Max(1, area.Height / dpi.DpiScaleY - 16), width = Math.Max(1, area.Width / dpi.DpiScaleX - 16);
        MinHeight = Math.Min(440, height); MinWidth = Math.Min(440, width); MaxHeight = height; MaxWidth = width;
        Height = Math.Min(Height, height); Width = Math.Min(Width, width);
    }
}

internal sealed class CropDialog : Window
{
    public PixelCrop Selection { get; private set; }
    public CropDialog(App app, BitmapSource source)
    {
        Title = app.L("Crop…"); Width = 860; Height = 760; MinWidth = 580; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var top = new StackPanel();
        top.Children.Add(UI.Text(app.L("Drag on the image to select a crop, or enter pixel coordinates below. Only Save Crop creates a file.")));
        var aspect = new ComboBox { ItemsSource = new[] { app.L("Free Crop"), "1:1", "4:3", "3:2", "16:9", "9:16" }, SelectedIndex = 0, Width = 150, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(aspect, app.L("Aspect Ratio"));
        top.Children.Add(aspect); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var surface = new CropSurface(source);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var fields = new WrapPanel();
        TextBox Field(string title, int value)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            stack.Children.Add(UI.Text(title));
            var field = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 95, MinHeight = 28 };
            AutomationProperties.SetName(field, title); stack.Children.Add(field); fields.Children.Add(stack); return field;
        }
        var x = Field(app.L("Left (px)"), 0); var y = Field(app.L("Top (px)"), 0);
        var width = Field(app.L("Width (px)"), source.PixelWidth); var height = Field(app.L("Height (px)"), source.PixelHeight);
        bottom.Children.Add(fields);
        var error = UI.Text(""); bottom.Children.Add(error);
        var details = UI.Text(app.L("Crop saves a new PNG beside the original. The original stays unchanged.")); bottom.Children.Add(details);
        void Sync()
        {
            var crop = surface.Selection;
            x.Text = crop.X.ToString(CultureInfo.InvariantCulture); y.Text = crop.Y.ToString(CultureInfo.InvariantCulture);
            width.Text = crop.Width.ToString(CultureInfo.InvariantCulture); height.Text = crop.Height.ToString(CultureInfo.InvariantCulture);
            error.Text = "";
        }
        bool ReadFields()
        {
            try
            {
                if (!int.TryParse(x.Text, out int left) || !int.TryParse(y.Text, out int topPixel) || !int.TryParse(width.Text, out int w) || !int.TryParse(height.Text, out int h))
                    throw new UserError("Enter whole-pixel crop coordinates.");
                var crop = new PixelCrop(left, topPixel, w, h); crop.Validate(source.PixelWidth, source.PixelHeight);
                surface.SetSelection(crop); Selection = crop; return true;
            }
            catch (UserError ex) { error.Text = app.L(ex.Message); return false; }
        }
        surface.Changed += Sync;
        var ratios = new[] { 0d, 1, 4d / 3, 3d / 2, 16d / 9, 9d / 16 };
        aspect.SelectionChanged += (_, _) => {
            surface.Ratio = ratios[aspect.SelectedIndex];
            if (surface.Ratio > 0) surface.SetSelection(PixelCrop.Center(source.PixelWidth, source.PixelHeight, surface.Ratio));
        };
        var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Update Crop"), () => { if (ReadFields()) aspect.SelectedIndex = 0; }));
        row.Children.Add(UI.Button(app.L("Reset Crop"), () => { aspect.SelectedIndex = 0; surface.SetSelection(new(0, 0, source.PixelWidth, source.PixelHeight)); }));
        var save = UI.Button(app.L("Save Crop"), () => { if (ReadFields()) DialogResult = true; }); row.Children.Add(save);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel);
        bottom.Children.Add(row); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        // Input coordinates are provided by WPF in the child surface's coordinate system;
        // Viewbox handles DPI/letterboxing, so one surface unit is one upright source pixel.
        root.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = surface, Margin = new Thickness(0, 12, 0, 0) });
        Content = root;
    }
}

internal sealed class CropSurface : FrameworkElement
{
    private readonly BitmapSource bitmap;
    private Point start;
    private bool dragging;
    public PixelCrop Selection { get; private set; }
    public double Ratio { get; set; }
    public event Action? Changed;
    public CropSurface(BitmapSource bitmap)
    {
        this.bitmap = bitmap; Width = bitmap.PixelWidth; Height = bitmap.PixelHeight;
        Selection = new(0, 0, bitmap.PixelWidth, bitmap.PixelHeight); Cursor = Cursors.Cross;
        MouseLeftButtonDown += (_, e) => {
            start = e.GetPosition(this); dragging = CaptureMouse();
            if (dragging) Update(e.GetPosition(this)); e.Handled = true;
        };
        MouseMove += (_, e) => { if (dragging && e.LeftButton == MouseButtonState.Pressed) Update(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, e) => { if (dragging) Update(e.GetPosition(this)); dragging = false; ReleaseMouseCapture(); e.Handled = true; };
        LostMouseCapture += (_, _) => dragging = false;
    }
    private void Update(Point end) => SetSelection(PixelCrop.Drag(bitmap.PixelWidth, bitmap.PixelHeight, start.X, start.Y, end.X, end.Y, Ratio));
    public void SetSelection(PixelCrop crop)
    {
        crop.Validate(bitmap.PixelWidth, bitmap.PixelHeight);
        Selection = crop; InvalidateVisual(); Changed?.Invoke();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawImage(bitmap, new Rect(0, 0, Width, Height));
        var r = Selection;
        var shade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        dc.DrawRectangle(shade, null, new Rect(0, 0, Width, r.Y));
        dc.DrawRectangle(shade, null, new Rect(0, r.Y + r.Height, Width, Height - r.Y - r.Height));
        dc.DrawRectangle(shade, null, new Rect(0, r.Y, r.X, r.Height));
        dc.DrawRectangle(shade, null, new Rect(r.X + r.Width, r.Y, Width - r.X - r.Width, r.Height));
        double stroke = Math.Max(1, Math.Max(Width, Height) / 400);
        dc.DrawRectangle(null, new Pen(Brushes.Black, stroke * 2), new Rect(r.X, r.Y, r.Width, r.Height));
        dc.DrawRectangle(null, new Pen(Brushes.White, stroke), new Rect(r.X, r.Y, r.Width, r.Height));
    }
}
