using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class MenuMotion : IDisposable
{
    private sealed class Item(Button button,ScaleTransform scale)
    {
        public readonly Button Button = button;
        public readonly ScaleTransform Scale = scale;
        public readonly MotionSpring Spring = new(1);
    }
    private readonly List<Item> items = new();
    private readonly Func<bool> permitted;
    private bool listening;
    private long last;
    public MenuMotion(Func<bool> permitted) { this.permitted = permitted; }
    private bool Enabled => permitted() && SystemParameters.ClientAreaAnimation;
    public void Attach(Button button,double angle)
    {
        var scale = new ScaleTransform(1,1); var group = new TransformGroup(); group.Children.Add(scale); group.Children.Add(new RotateTransform(angle));
        button.RenderTransformOrigin = new Point(.5,.5); button.RenderTransform = group;
        var item = new Item(button,scale); items.Add(item);
        void Target(double value)
        {
            item.Spring.Target = Enabled ? value : 1;
            if (!Enabled) { item.Spring.Snap(); scale.ScaleX = scale.ScaleY = 1; }
            else Wake();
        }
        button.MouseEnter += (_,_) => Target(1.025); button.MouseLeave += (_,_) => Target(1);
        button.PreviewMouseLeftButtonDown += (_,_) => Target(.97);
        button.PreviewMouseLeftButtonUp += (_,_) => Target(button.IsMouseOver ? 1.025 : 1);
        button.LostMouseCapture += (_,_) => Target(button.IsMouseOver ? 1.025 : 1);
        button.DragEnter += (_,_) => Target(1.025); button.DragLeave += (_,_) => Target(1);
        button.Drop += (_,_) => Target(button.IsMouseOver ? 1.025 : 1);
    }
    private void Wake()
    {
        if (listening) return; listening = true; last = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += Frame;
    }
    private void Frame(object? sender,EventArgs args)
    {
        long now = Stopwatch.GetTimestamp(); double dt = (now - last) / (double)Stopwatch.Frequency; last = now;
        bool moving = false;
        foreach (var item in items)
        {
            if (!Enabled) { item.Spring.Target = 1; item.Spring.Snap(); }
            else if (dt > .25) item.Spring.Snap(); else item.Spring.Advance(dt);
            item.Scale.ScaleX = item.Scale.ScaleY = item.Spring.Value; moving |= !item.Spring.Settled;
        }
        if (!moving) Stop();
    }
    private void Stop() { if (!listening) return; CompositionTarget.Rendering -= Frame; listening = false; }
    public void Reset() { Stop(); items.Clear(); }
    public void Dispose() => Reset();
}

internal static class MenuPresentation
{
    public static Canvas Build(App app,ToolAction[] actions,Action<ToolAction> run,Action<ToolAction,string[]> drop,Action close,MenuMotion motion)
    {
        var template = app.Prefs.MenuTemplate; var canvas = new Canvas { Width = 620, Height = 450 };
        var palette = MenuColors.Active(app,template);
        var placements = MenuGeometry.Layout(template,actions.Length);
        if (template is MenuTemplate.Bookshelf or MenuTemplate.Clothesline)
            foreach (double y in template == MenuTemplate.Bookshelf ? new[] { 204d,404d } : new[] { 50d,250d })
                canvas.Children.Add(new Line { X1 = 8,Y1 = y,X2 = 612,Y2 = y,Stroke = palette is null ? SystemColors.ControlDarkBrush : MenuColors.Brush(palette.Rope),StrokeThickness = template == MenuTemplate.Bookshelf ? 6 : 2 });
        for (int i = 0; i < actions.Length; i++)
        {
            var action = actions[i]; var rect = placements[i];
            var button = UI.Button(Catalog.DisplayTitle(action,app.Prefs.Language),() => run(action));
            button.Content = new TextBlock { Text = Catalog.DisplayTitle(action,app.Prefs.Language),TextWrapping = TextWrapping.Wrap,TextAlignment = TextAlignment.Center,FontSize = template == MenuTemplate.List ? 14 : 13 };
            button.Width = rect.Width; button.Height = rect.Height; button.Margin = new Thickness(0);
            if (template == MenuTemplate.Notes) { button.Background = SystemColors.InfoBrush; button.Foreground = SystemColors.InfoTextBrush; }
            MenuColors.Apply(button,palette);
            Canvas.SetLeft(button,rect.X); Canvas.SetTop(button,rect.Y); motion.Attach(button,rect.Angle);
            UI.DropFiles(button,app,paths => drop(action,paths)); canvas.Children.Add(button);
            if (template == MenuTemplate.Clothesline)
            {
                var peg = new Rectangle { Width = 10,Height = 28,Fill = palette is null ? SystemColors.ControlDarkBrush : MenuColors.Brush(palette.Peg),IsHitTestVisible = false };
                Canvas.SetLeft(peg,rect.X + rect.Width / 2 - 5); Canvas.SetTop(peg,rect.Y - 22); canvas.Children.Add(peg);
            }
        }
        if (template == MenuTemplate.Wheel)
        {
            var cancel = UI.Button(app.L("Cancel"),close); cancel.Width = 120; Canvas.SetLeft(cancel,250); Canvas.SetTop(cancel,202); canvas.Children.Add(cancel);
        }
        return canvas;
    }
}
