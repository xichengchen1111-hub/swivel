using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class MarkEditDialog : Window
{
    public ImageMark Mark { get; private set; }
    public MarkEditDialog(App app,ImageMark mark,int width,int height)
    {
        Mark = mark; Title = app.L("Edit Selected Mark…"); Width = 530; Height = 680; MinWidth = 420; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(UI.Text(app.L(mark.Kind.ToString()),20));
        panel.Children.Add(UI.Text(app.L("Position and shape size use upright image pixels. Text and numbered labels move by their anchor; their size follows Stroke / Text Size. Changes can be undone.")));
        TextBox Field(string label,string value,bool enabled = true)
        {
            var row = new DockPanel(); var field = new TextBox { Text = value,Width = 180,MinHeight = 30,MaxLength = 32,IsEnabled = enabled,Margin = new Thickness(0,0,0,8) }; DockPanel.SetDock(field,Dock.Right); row.Children.Add(field); row.Children.Add(UI.Text(app.L(label))); panel.Children.Add(row); AutomationProperties.SetName(field,app.L(label)); return field;
        }
        string Number(double value) => value.ToString("R",CultureInfo.CurrentCulture);
        var frame = MarkEdit.Frame(mark); var x = Field("Position X",Number(frame.X)); var y = Field("Position Y",Number(frame.Y));
        var w = Field("Shape Width",Number(frame.Width),frame.Width>0); var h = Field("Shape Height",Number(frame.Height),frame.Height>0);
        var stroke = Field("Stroke / Text Size",Number(mark.Width),mark.Kind != MarkKind.Redact);
        panel.Children.Add(UI.Text(app.L("Color"))); uint[] colors = { mark.Rgb,0xe53935,0xffffff,0x000000,0x1976d2,0xffc107 };
        var color = new ComboBox { ItemsSource = new[] { app.L("Current Color"),app.L("Red"),app.L("White"),app.L("Black"),app.L("Blue"),app.L("Yellow") },SelectedIndex = 0,MinHeight = 30,IsEnabled = mark.Kind != MarkKind.Redact }; AutomationProperties.SetName(color,app.L("Color")); panel.Children.Add(color);
        panel.Children.Add(UI.Text(app.L("Annotation Text"))); var text = new TextBox { Text = mark.Text,MaxLength = mark.Kind == MarkKind.Number ? 3 : 200,AcceptsReturn = mark.Kind == MarkKind.Text,MinHeight = 60,TextWrapping = TextWrapping.Wrap,IsEnabled = mark.Kind is MarkKind.Text or MarkKind.Number }; AutomationProperties.SetName(text,app.L("Annotation Text")); panel.Children.Add(text);
        var error = UI.Text(""); panel.Children.Add(error); var row = new WrapPanel();
        row.Children.Add(UI.Button(app.L("Apply Changes"),() => {
            try
            {
                double Read(TextBox box) => double.TryParse(box.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double value) ? value : double.NaN;
                Mark = MarkEdit.Apply(mark,new(Read(x),Read(y),Read(w),Read(h)),colors[color.SelectedIndex],Read(stroke),text.Text,width,height); DialogResult = true;
            }
            catch (UserError ex) { error.Text = app.L(ex.Message); }
        }));
        var cancel = UI.Button(app.L("Cancel"),() => DialogResult = false); cancel.IsCancel = true; row.Children.Add(cancel); panel.Children.Add(row); Content = UI.Scroll(panel);
    }
}
