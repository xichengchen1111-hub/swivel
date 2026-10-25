using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Swivel.Core;
using Forms = System.Windows.Forms;

namespace Swivel.Windows;

internal static class MenuColors
{
    public static Brush Brush(uint rgb) { var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb)); brush.Freeze(); return brush; }
    public static MenuPalette? Active(App app,MenuTemplate template) => SystemParameters.HighContrast ? null : app.Prefs.Palette(template);
    public static MenuPalette SystemPalette(MenuTemplate template)
    {
        static uint Rgb(Color color) => (uint)(color.R << 16 | color.G << 8 | color.B);
        uint edge = Rgb(SystemColors.ControlDarkColor);
        return new(Rgb(template == MenuTemplate.Notes ? SystemColors.InfoColor : SystemColors.ControlColor),edge,edge,edge);
    }
    public static void Apply(Button button,MenuPalette? palette)
    {
        if (palette is null) return;
        button.Background = Brush(palette.Surface); button.Foreground = Brush(palette.TextColor); button.BorderBrush = Brush(palette.Outline);
        // Default WPF themes replace only the background on hover/press. A dark
        // custom surface would then show white text over a pale system hover fill.
        // Keep the supplied color pair in every state; MenuMotion owns press/hover
        // feedback. The dedicated focus ring keeps keyboard navigation visible.
        button.Template = CustomTemplate; button.FocusVisualStyle = null;
    }
    private static ControlTemplate CustomTemplate
    {
        get
        {
            var template = new ControlTemplate(typeof(Button)); var grid = new FrameworkElementFactory(typeof(Grid));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(Border.CornerRadiusProperty,new CornerRadius(3));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentProperty,new TemplateBindingExtension(ContentControl.ContentProperty));
            content.SetValue(ContentPresenter.ContentTemplateProperty,new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty,true);
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty,new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            content.SetValue(FrameworkElement.VerticalAlignmentProperty,new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            border.AppendChild(content); grid.AppendChild(border);
            var focus = new FrameworkElementFactory(typeof(Border),"FocusRing"); focus.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.ForegroundProperty)); focus.SetValue(Border.BorderThicknessProperty,new Thickness(1)); focus.SetValue(FrameworkElement.MarginProperty,new Thickness(3)); focus.SetValue(UIElement.OpacityProperty,0d); focus.SetValue(UIElement.IsHitTestVisibleProperty,false); grid.AppendChild(focus);
            template.VisualTree = grid;
            var keyboard = new Trigger { Property = UIElement.IsKeyboardFocusedProperty,Value = true }; keyboard.Setters.Add(new Setter(UIElement.OpacityProperty,1d,"FocusRing")); template.Triggers.Add(keyboard);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty,Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.5)); template.Triggers.Add(disabled);
            return template;
        }
    }
}

internal sealed class MenuColorsDialog : Window
{
    public MenuColorsDialog(App app)
    {
        MenuTemplate template = app.Prefs.MenuTemplate; MenuPalette? palette = app.Prefs.Palette(template);
        Title = app.L("Menu Colors…"); Width = 540; Height = 600; MinWidth = 420; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        string title = template == MenuTemplate.Notes ? "Sticky Notes" : template.ToString(); panel.Children.Add(UI.Text(app.L(title),20));
        panel.Children.Add(UI.Text(app.L("Each menu style keeps its own four colors. Text contrast is automatic. Windows high-contrast mode uses system colors. Clothesline colors also apply to hung prints.")));
        var presets = new WrapPanel(); var swatches = new StackPanel(); var preview = new Canvas { Width = 400,Height = 135 }; var mode = UI.Text("");
        void Update()
        {
            swatches.Children.Clear(); preview.Children.Clear();
            string[] names = { "Surface Color","Outline Color","Rope Color","Peg Color" };
            mode.Text = app.L(palette is null ? "System Default" : "Custom Colors");
            var current = palette ?? MenuColors.SystemPalette(template);
            uint[] colors = { current.Surface,current.Outline,current.Rope,current.Peg };
            for (int i = 0; i < 4; i++)
            {
                int index = i; var button = UI.Button(app.L(names[i]),() => {
                    using var picker = new Forms.ColorDialog { FullOpen = true,Color = System.Drawing.Color.FromArgb((int)(0xff000000u | colors[index])) };
                    if (picker.ShowDialog(new OwnerHandle(new WindowInteropHelper(this).Handle)) != Forms.DialogResult.OK) return;
                    uint color = (uint)picker.Color.ToArgb() & 0xffffff; var source = palette ?? MenuColors.SystemPalette(template);
                    palette = index switch { 0 => source with { Surface = color },1 => source with { Outline = color },2 => source with { Rope = color },_ => source with { Peg = color } }; Update();
                });
                var row = new DockPanel(); DockPanel.SetDock(button,Dock.Left); row.Children.Add(button);
                row.Children.Add(new Rectangle { Width = 60,Height = 25,Fill = MenuColors.Brush(colors[i]),Stroke = SystemColors.ControlTextBrush,StrokeThickness = 1,HorizontalAlignment = HorizontalAlignment.Left }); swatches.Children.Add(row);
            }
            var shown = SystemParameters.HighContrast ? null : palette;
            var line = new Line { X1 = 0,X2 = 400,Y1 = 22,Y2 = 22,Stroke = shown is null ? SystemColors.ControlDarkBrush : MenuColors.Brush(shown.Rope),StrokeThickness = 3 }; preview.Children.Add(line);
            var sample = UI.Button(app.L("Preview"),() => { }); sample.IsTabStop = false; sample.Width = 180; sample.Height = 70; MenuColors.Apply(sample,shown); Canvas.SetLeft(sample,110); Canvas.SetTop(sample,35); preview.Children.Add(sample);
            var peg = new Rectangle { Width = 10,Height = 28,Fill = shown is null ? SystemColors.ControlDarkBrush : MenuColors.Brush(shown.Peg),IsHitTestVisible = false }; Canvas.SetLeft(peg,195); Canvas.SetTop(peg,16); preview.Children.Add(peg);
        }
        void Preset(string name,MenuPalette? value) => presets.Children.Add(UI.Button(app.L(name),() => { palette = value; Update(); }));
        Preset("System Default",null); Preset("Warm Palette",MenuPalette.Warm); Preset("Ocean Palette",MenuPalette.Ocean); Preset("Night Palette",MenuPalette.Night);
        panel.Children.Add(presets); panel.Children.Add(mode); panel.Children.Add(swatches); panel.Children.Add(preview);
        var row = new WrapPanel(); row.Children.Add(UI.Button(app.L("Save Colors"),() => { app.Prefs.SetPalette(template,palette); DialogResult = true; }));
        var cancel = UI.Button(app.L("Cancel"),() => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel); panel.Children.Add(row); Content = UI.Scroll(panel);
        PropertyChangedEventHandler changed = (_,e) => { if (e.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.Invoke(Update); };
        SystemParameters.StaticPropertyChanged += changed; Closed += (_,_) => SystemParameters.StaticPropertyChanged -= changed; Update();
    }
    private sealed class OwnerHandle(IntPtr handle) : Forms.IWin32Window { public IntPtr Handle => handle; }
}
