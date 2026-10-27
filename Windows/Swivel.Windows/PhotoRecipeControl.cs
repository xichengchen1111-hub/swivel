using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class PhotoRecipeControl : StackPanel
{
    private readonly CheckBox enabled;
    private readonly CheckBox automatic;
    public bool AutoPerImage => enabled.IsChecked == true && automatic.IsChecked == true;
    private readonly ComboBox look;
    private readonly Dictionary<string,TextBox> values = new();
    public PhotoRecipeControl(App app,PhotoSettings? initial,bool autoPerImage = false)
    {
        enabled = new CheckBox { Content = app.L("Apply photo adjustments"),IsChecked = initial is not null || autoPerImage,Margin = new Thickness(0,12,0,8) }; Children.Add(enabled);
        var settings = initial ?? new PhotoSettings(); var body = new StackPanel();
        var expander = new Expander { Header = app.L("Photo recipe values"),Content = body,IsExpanded = false,IsEnabled = enabled.IsChecked == true }; Children.Add(expander);
        enabled.Click += (_,_) => { expander.IsEnabled = enabled.IsChecked == true; if (expander.IsEnabled) expander.IsExpanded = true; };
        automatic = new CheckBox { Content = app.L("Recalculate Auto for each image"),IsChecked = autoPerImage,Margin = new Thickness(0,8,0,8) }; body.Children.Add(automatic);
        body.Children.Add(UI.Text(app.L("Per-image Auto replaces exposure, highlights, shadows and vibrance for each original. Other values and the look still apply. With Auto off, all values stay fixed. Adjustments run before resizing."),12));
        body.Children.Add(UI.Text(app.L("Looks"))); look = new ComboBox(); foreach (var value in Enum.GetValues<PhotoLook>()) look.Items.Add(app.L(value.ToString())); look.SelectedIndex = (int)settings.Look; body.Children.Add(look); AutomationProperties.SetName(look,app.L("Looks"));
        void Field(string key,double value,string range)
        {
            var row = new DockPanel { Margin = new Thickness(0,3,0,3) };
            var box = new TextBox { Text = value.ToString("R",CultureInfo.CurrentCulture),Width = 100,MinHeight = 28,MaxLength = 32 }; DockPanel.SetDock(box,Dock.Right); row.Children.Add(box); row.Children.Add(UI.Text(app.L(key)+" ("+range+")",12));
            values[key] = box; AutomationProperties.SetName(box,app.L(key)); body.Children.Add(row);
        }
        Field("Look Strength",settings.LookStrength,"0–1"); Field("Exposure",settings.Exposure,"−3–3");
        Field("Contrast",settings.Contrast,"−1–1"); Field("Highlights",settings.Highlights,"−1–1"); Field("Shadows",settings.Shadows,"−1–1"); Field("Whites",settings.Whites,"−1–1"); Field("Blacks",settings.Blacks,"−1–1");
        Field("Saturation",settings.Saturation,"−1–1"); Field("Vibrance",settings.Vibrance,"−1–1"); Field("Warmth",settings.Warmth,"−1–1"); Field("Tint",settings.Tint,"−1–1"); Field("Clarity",settings.Clarity,"−1–1");
        Field("Sharpen",settings.Sharpen,"0–1"); Field("Noise Reduction",settings.Noise,"0–1"); Field("Vignette",settings.Vignette,"0–1");
        void AutoState() { foreach (string key in new[] { "Exposure","Highlights","Shadows","Vibrance" }) values[key].IsEnabled = automatic.IsChecked != true; }
        automatic.Click += (_,_) => AutoState(); AutoState();
    }
    public PhotoSettings? Read()
    {
        if (enabled.IsChecked != true) return null;
        double Value(string key) { if (!double.TryParse(values[key].Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double value)) throw new UserError("Photo adjustment values are outside their supported range."); return value; }
        var result = new PhotoSettings(Value("Exposure"),Value("Contrast"),Value("Highlights"),Value("Shadows"),Value("Whites"),Value("Blacks"),Value("Saturation"),Value("Vibrance"),Value("Warmth"),Value("Tint"),Value("Clarity"),Value("Sharpen"),Value("Noise Reduction"),Value("Vignette"),(PhotoLook)look.SelectedIndex,Value("Look Strength"));
        result.Validate(); return result;
    }
}
