using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swivel;

/// `Swivel --frames <wheel|list|shelf|notes|clothesline> <out dir> [light|dark]`: renders the menu's animations frame by
/// frame (60 fps) to PNGs for review, offscreen: open, a slow and a fast sweep across items, a page turn, then a drop.
/// `SWIVEL_FRAMES_SCALE` renders at another scale (e.g. 2 for a 200 % display), `SWIVEL_FRAMES_LOG` prints slow frames.
static class WheelFrames
{
    public static Task<int> Render(string styleId, string outDir, string? appearance)
    {
        if (MenuStyles.FromId(styleId) is not { } style)
        {
            Console.WriteLine($"FAIL unknown style “{styleId}”. Use {string.Join(", ", MenuStyles.All.Select(s => s.Id()))}.");
            return Task.FromResult(1);
        }
        Directory.CreateDirectory(outDir);
        bool? dark = appearance switch { "dark" => true, "light" => false, _ => null };
        double scale = double.TryParse(Environment.GetEnvironmentVariable("SWIVEL_FRAMES_SCALE"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1;
        bool logSlow = Environment.GetEnvironmentVariable("SWIVEL_FRAMES_LOG") != null;

        string[] titles = ["JPG", "PNG", "HEIC", "TIFF", "PDF", "GIF", "AVIF", "BMP"];
        var page1 = titles.Select(t => new WheelItem(t, _ => Task.FromResult(new ActionResult("")))).ToList();
        var page2 = new[] { "WebP", "ICO", "PSD" }.Select(t => new WheelItem(t, _ => Task.FromResult(new ActionResult("")))).ToList();
        var size = WheelView.MenuSize(style, 8);
        var view = new WheelView
        {
            ViewSize = size, MenuStyle = style, Subtitle = "photo.heic", IsLive = true, ManualClock = true, DarkOverride = dark,
        };
        view.Configure(page1, WheelMode.Convert, 0, 2);
        view.Measure(size);
        view.Arrange(new Rect(size));

        // The backdrop the glass would blur (the frames show the tint over the wallpaper).
        var glass = new GlassBackdrop { Width = size.Width, Height = size.Height, Dark = dark ?? Native.DarkMode };
        if (view.Backdrop is { } b && !(style == MenuStyle.Wheel && Native.TransparencyOff))
        {
            glass.Shape = b.Rect;
            glass.Radius = b.Radius;
            glass.Ellipse = style == MenuStyle.Wheel;
        }
        glass.Measure(size);
        glass.Arrange(new Rect(size));

        const double fps = 60;
        // The pointer moves along a path like a real drag (y-up degrees around the centre, as on the Mac): out to the
        // top sector, a slow arc clockwise, a fast flick across three sectors, back to the hub (cancel), out again, a
        // page turn, then a drop.
        var c = new Point(size.Width / 2, size.Height / 2);
        Point At(double degrees, double r) =>
            new(c.X + Math.Cos(degrees * Math.PI / 180) * r, c.Y - Math.Sin(degrees * Math.PI / 180) * r);
        bool wheel = style == MenuStyle.Wheel;
        Func<double, Point?> pointer = _ => null;
        if (wheel)
        {
            pointer = t => t switch
            {
                < 0.3 => c,
                < 0.45 => At(90, 20 + (t - 0.3) / 0.15 * 70),
                < 0.95 => At(90 - (t - 0.45) / 0.5 * 50, 90),
                < 1.05 => At(40 - (t - 0.95) / 0.1 * 140, 95),
                < 1.3 => At(-100, 95 - (t - 1.05) / 0.25 * 85),
                < 1.5 => At(-100, 10),
                < 1.7 => At(-100, 10 + (t - 1.5) / 0.2 * 80),
                _ => At(-100, 90),
            };
        }
        (double At, Action Run)[] script = wheel
            ?
            [
                (0, () => view.BeginOpen(c)),
                (1.80, () => { view.Configure(page2, WheelMode.Convert, 1, 2); view.Track(At(-100, 90)); }),
                (2.25, () => view.BeginConfirm(view.Hovered ?? 0)),
            ]
            :
            [
                (0, () => view.BeginOpen()),
                (0.40, () => view.Hovered = 0),
                (0.75, () => view.Hovered = 1),
                (0.85, () => view.Hovered = 2),
                (0.95, () => view.Hovered = 3),
                (1.40, () => view.Configure(page2, WheelMode.Convert, 1, 2)),
                (1.85, () => view.Hovered = 1),
                (2.25, () => view.BeginConfirm(1)),
            ];

        var wallpaper = MenuPreview.WallpaperBrush(new Rect(size), three: false);
        int pixelW = (int)Math.Round(size.Width * scale), pixelH = (int)Math.Round(size.Height * scale);
        double slowest = 0, total = 0;
        int timed = 0, next = 0;
        var clock = new Stopwatch();
        for (int frame = 0; frame < (int)(2.8 * fps); frame++)
        {
            double t = frame / fps;
            while (next < script.Length && script[next].At <= t + 0.0001) { script[next].Run(); next++; }
            if (pointer(t) is { } p && t < 1.8) view.Track(p);
            view.Advance(1 / fps);
            // The window fades out 0.16 s after the drop, over 0.14 s, as the real one does (both divided by the
            // menu's tuned speed).
            double speed = view.Tuning.SafeSpeed;
            double fade = Math.Clamp(1 - (t - 2.25 - 0.16 / speed) / (0.14 / speed), 0, 1);
            view.Opacity = glass.Opacity = fade;

            clock.Restart();
            view.Redraw();
            var bitmap = new RenderTargetBitmap(pixelW, pixelH, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(wallpaper, null, new Rect(size));
            bitmap.Render(backdrop);
            bitmap.Render(glass);
            bitmap.Render(view);
            clock.Stop();
            double took = clock.Elapsed.TotalMilliseconds;
            if (frame > 2) { slowest = Math.Max(slowest, took); total += took; timed++; }
            if (logSlow && took > 5) Console.WriteLine($"frame {frame} (t={t:0.00}) {took:0.00} ms");

            if (pointer(t) is { } q && t < 1.8)
            {
                // Mark the pointer.
                var mark = new DrawingVisual();
                using (var dc = mark.RenderOpen()) dc.DrawEllipse(Brushes.Black, null, q, 3, 3);
                bitmap.Render(mark);
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(System.IO.Path.Combine(outDir, string.Create(CultureInfo.InvariantCulture, $"f{frame:000}_{t:0.000}.png")));
            encoder.Save(file);
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"OK draw: mean {total / Math.Max(timed, 1):0.00} ms, slowest {slowest:0.00} ms"));
        return Task.FromResult(0);
    }
}

/// `Swivel --demo <convert|tools|actions> <file> [script] [style]`: shows the menu at the pointer without a drag (for
/// screenshots and recordings). The script is one hover index ("2"), or steps run every 0.35 s: "0,1,2,n,1,p1"
/// (n = next page, p = drop on that item, which presses it without running it). The style applies to this run only.
static class WheelDemo
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: --demo <convert|tools|actions> <file> [script] [style]");
            return 1;
        }
        var mode = WheelModes.FromId(args[0]) ?? WheelMode.Convert;
        var style = args.Length >= 4 ? MenuStyles.FromId(args[3]) : null;
        var controller = new WheelController { StyleOverride = style };
        controller.KeyTapped(mode, [System.IO.Path.GetFullPath(args[1])]);
        if (Environment.GetEnvironmentVariable("SWIVEL_DEMO_LOG") != null)
        {
            await Task.Delay(300);
            var w = controller.Window;
            var c = Native.CursorPixels();
            Console.WriteLine($"shown {w.IsShown} visible {w.IsVisible} items {w.View.Items.Count} style {w.View.MenuStyle} " +
                              $"window {w.Left:0},{w.Top:0} {w.ActualWidth:0}x{w.ActualHeight:0} cursor {c.X},{c.Y} " +
                              $"dpi {VisualTreeHelper.GetDpi(w).PixelsPerDip} opacity {((FrameworkElement)((System.Windows.Controls.Canvas)w.Content).Children[0]).Opacity:0.00}");
        }
        var steps = args.Length >= 3 ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        double elapsed = 0, lastStep = 0;
        for (int n = 0; n < steps.Length; n++)
        {
            double at = steps.Length == 1 ? 0 : 0.6 + n * 0.35;
            lastStep = at;
            if (at > elapsed) await Task.Delay(TimeSpan.FromSeconds(at - elapsed));
            elapsed = at;
            var step = steps[n].Trim();
            if (step == "n") controller.DemoNextPage();
            else if (step.StartsWith('p') && int.TryParse(step[1..], out var pressed)) controller.DemoPress(pressed);
            else if (int.TryParse(step, out var hover)) controller.DemoHover(hover);
        }
        // SWIVEL_DEMO_TOAST=busy|ok|error|near shows the toast too (for screenshots of it).
        if (Environment.GetEnvironmentVariable("SWIVEL_DEMO_TOAST") is { } toast)
        {
            var file = System.IO.Path.GetFullPath(args[1]);
            switch (toast)
            {
                case "busy": Toast.Shared.Show("Compress…", "busy", autoHide: false, spinning: true); break;
                case "error": Toast.Shared.Show("Couldn't convert this file", "error", tint: ToastTint.Red); break;
                case "near":
                    var c = Native.CursorPixels();
                    Toast.Shared.Show(L("Nothing to show for this file"), "nosign", near: new Point(c.X, c.Y));
                    break;
                default: Toast.Shared.Show(L("Saved %@", System.IO.Path.GetFileName(file)), "ok", [file]); break;
            }
        }
        double end = Math.Max(6, lastStep + 1.5);
        if (Environment.GetEnvironmentVariable("SWIVEL_DEMO_SECONDS") is { } custom &&
            double.TryParse(custom, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) end = seconds;
        if (end > elapsed) await Task.Delay(TimeSpan.FromSeconds(end - elapsed));
        controller.Window.HideNow();
        controller.Window.Close();
        return 0;
    }
}
