using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class PhotoDialog : Window
{
    public PhotoSettings Settings { get; private set; } = new();
    private readonly Dictionary<string, Slider> sliders = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? renderCancellation;
    private bool closed, updating;
    private int revision;
    public PhotoDialog(App app, PixelImage preview)
    {
        Title = app.L("Edit Photo…"); Width = 1040; Height = 780; MinWidth = 760; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(18) };
        var footer = new StackPanel(); var status = UI.Text(""); footer.Children.Add(status);
        footer.Children.Add(UI.Text(app.L("Preview is reduced for responsiveness. Save renders the full image as a new PNG. Windows looks may differ from the Mac version."), 12));
        var buttons = new WrapPanel();
        var save = UI.Button(app.L("Save Photo"), () => DialogResult = true); buttons.Children.Add(save);
        var savePreset = UI.Button(app.L("Save as Preset…"),() => new PresetDialog(app,photo:Settings) { Owner = this }.ShowDialog()); buttons.Children.Add(savePreset);
        var cancel = UI.Button(app.L("Cancel"), () => DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel);
        footer.Children.Add(buttons); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var side = new StackPanel { Width = 285, Margin = new Thickness(0, 0, 18, 0) };
        var scroll = UI.Scroll(side); DockPanel.SetDock(scroll, Dock.Left); root.Children.Add(scroll);
        var image = new Image { Stretch = Stretch.Uniform };
        var original = ToBitmap(preview); image.Source = original;
        var comparison = new CheckBox { Content = app.L("Show Original"), Margin = new Thickness(0, 8, 0, 8) }; side.Children.Add(comparison);
        BitmapSource current = original;
        comparison.Click += (_, _) => image.Source = comparison.IsChecked == true ? original : current;
        root.Children.Add(image); Content = root;
        side.Children.Add(UI.Text(app.L("Looks"), 18));
        var look = new ComboBox();
        foreach (var value in Enum.GetValues<PhotoLook>()) look.Items.Add(app.L(value.ToString()));
        look.SelectedIndex = 0; side.Children.Add(look); AutomationProperties.SetName(look, app.L("Looks"));
        void Slider(string key, double min, double max, double initial = 0)
        {
            var label = UI.Text(app.L(key)); side.Children.Add(label);
            var slider = new Slider { Minimum = min, Maximum = max, Value = initial, SmallChange = (max - min) / 100, LargeChange = (max - min) / 10 };
            AutomationProperties.SetName(slider, app.L(key)); sliders[key] = slider; side.Children.Add(slider);
            slider.ValueChanged += (_, _) => { label.Text = app.L(key) + "  " + slider.Value.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture); Render(); };
        }
        Slider("Look Strength", 0, 1, 1);
        side.Children.Add(UI.Text(app.L("Light"), 18));
        Slider("Exposure", -3, 3); Slider("Contrast", -1, 1); Slider("Highlights", -1, 1); Slider("Shadows", -1, 1); Slider("Whites", -1, 1); Slider("Blacks", -1, 1);
        side.Children.Add(UI.Text(app.L("Color"), 18));
        Slider("Saturation", -1, 1); Slider("Vibrance", -1, 1); Slider("Warmth", -1, 1); Slider("Tint", -1, 1);
        side.Children.Add(UI.Text(app.L("Effects"), 18));
        Slider("Clarity", -1, 1); Slider("Sharpen", 0, 1); Slider("Noise Reduction", 0, 1); Slider("Vignette", 0, 1);
        void Set(PhotoSettings p)
        {
            updating = true;
            sliders["Exposure"].Value=p.Exposure; sliders["Contrast"].Value=p.Contrast; sliders["Highlights"].Value=p.Highlights; sliders["Shadows"].Value=p.Shadows;
            sliders["Whites"].Value=p.Whites; sliders["Blacks"].Value=p.Blacks; sliders["Saturation"].Value=p.Saturation; sliders["Vibrance"].Value=p.Vibrance;
            sliders["Warmth"].Value=p.Warmth; sliders["Tint"].Value=p.Tint; sliders["Clarity"].Value=p.Clarity; sliders["Sharpen"].Value=p.Sharpen;
            sliders["Noise Reduction"].Value=p.Noise; sliders["Vignette"].Value=p.Vignette; sliders["Look Strength"].Value=p.LookStrength; look.SelectedIndex=(int)p.Look;
            updating = false; Render();
        }
        side.Children.Add(UI.Button(app.L("Auto"), () => Set(PhotoAdjust.Auto(preview))));
        side.Children.Add(UI.Button(app.L("Reset Adjustments"), () => Set(new())));
        look.SelectionChanged += (_, _) => Render();
        Closed += (_, _) => { closed = true; renderCancellation?.Cancel(); };
        async void Render()
        {
            if (updating || closed || sliders.Count != 15) return;
            int currentRevision = ++revision;
            renderCancellation?.Cancel();
            var cancelRender = renderCancellation = new CancellationTokenSource(); var token = cancelRender.Token;
            double V(string key) => sliders[key].Value;
            var settings = new PhotoSettings(V("Exposure"),V("Contrast"),V("Highlights"),V("Shadows"),V("Whites"),V("Blacks"),V("Saturation"),V("Vibrance"),V("Warmth"),V("Tint"),V("Clarity"),V("Sharpen"),V("Noise Reduction"),V("Vignette"),(PhotoLook)look.SelectedIndex,V("Look Strength"));
            Settings = settings; save.IsEnabled = savePreset.IsEnabled = false; status.Text = app.L("Updating preview…");
            try
            {
                await gate.WaitAsync(token);
                PixelImage result;
                try { result = await Task.Run(() => PhotoAdjust.Apply(preview, settings, token), token); }
                finally { gate.Release(); }
                if (closed || currentRevision != revision) return;
                current = ToBitmap(result); image.Source = comparison.IsChecked == true ? original : current;
                status.Text = ""; save.IsEnabled = savePreset.IsEnabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!closed && currentRevision == revision) { status.Text = app.L("The preview could not be updated. Reset the adjustments and try again."); app.ShowError(error); } }
            finally { if (ReferenceEquals(renderCancellation, cancelRender)) renderCancellation = null; cancelRender.Dispose(); }
        }
    }
    private static BitmapSource ToBitmap(PixelImage pixels)
    {
        var image = BitmapSource.Create(pixels.Width,pixels.Height,96,96,PixelFormats.Bgra32,null,pixels.Bgra,pixels.Width*4); image.Freeze(); return image;
    }
}
