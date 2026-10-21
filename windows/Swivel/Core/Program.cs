using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Swivel;

/// The WPF application: Fluent (Windows 11) styling that follows the system light/dark theme, no main window.
sealed class App : Application
{
    public App()
    {
        ThemeMode = ThemeMode.System;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"Unhandled: {e.Exception}");
            e.Handled = true;
            try { Toast.Shared.Show(e.Exception.Message, "error", tint: ToastTint.Red); } catch (Exception) { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Fatal: {e.ExceptionObject}");
    }

    /// Runs `work` once the dispatcher is up, then quits with its exit code (for the command-line modes).
    public int RunThenExit(Func<Task<int>> work)
    {
        int code = 0;
        Startup += async (_, _) =>
        {
            try { code = await work(); }
            catch (Exception ex) { Console.WriteLine($"FAIL {ex.Message}"); code = 2; }
            Shutdown(code);
        };
        Run();
        return code;
    }
}

static class Log
{
    static readonly object gate = new();

    public static void Write(string message)
    {
        lock (gate)
        {
            try { File.AppendAllText(System.IO.Path.Combine(Prefs.AppFolder, "swivel.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }
}

static partial class Program
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [STAThread]
    static int Main(string[] args)
    {
        // A GUI executable has no console; print to the terminal that started us (command-line modes).
        if (args.Length > 0 && Console.IsOutputRedirected == false) AttachConsole(-1);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var app = new App();

        string mode = args.Length > 0 ? args[0] : "";
        switch (mode)
        {
            // Isolated preview: no screenshot watcher or drag watcher. Uses its own settings file.
            case "--tool" when args.Length >= 2:
                return RunTool(app, args);

            // `Swivel --feedback <check|play>`: check reports whether the sounds are ready; play runs every one once.
            case "--feedback" when args.Length == 2:
                return app.RunThenExit(() => Feedback.CommandLine(args[1]));

            // File-only OCR for smoke tests; does not touch the clipboard or start watchers.
            case "--recognize" when args.Length == 2:
                return app.RunThenExit(async () =>
                {
                    Console.WriteLine(await TextRecognition.Recognize(System.IO.Path.GetFullPath(args[1])));
                    return 0;
                });

            // `Swivel --run <convert|tools|actions> <item title> <file>...` runs one menu item headlessly (for testing).
            case "--run" when args.Length >= 4:
                return app.RunThenExit(() => RunItem(args));

            // `Swivel --edit <crop|compress|edit|bg|redact|metadata|annotate|collage|trim> <file>...` opens one editor.
            case "--edit" when args.Length >= 3:
                return app.RunThenExit(async () =>
                {
                    var files = args.Skip(2).Select(System.IO.Path.GetFullPath).ToList();
                    Editor editor = args[1] switch
                    {
                        "compress" => new CompressEditor(files[0]),
                        "edit" => new AdjustEditor(files[0]),
                        "bg" => new BackgroundEditor(files[0]),
                        "redact" => new RedactEditor(files[0]),
                        "metadata" => new MetadataEditor(files[0]),
                        "annotate" => new AnnotateEditor(files[0]),
                        "collage" => new CollageEditor(files),
                        "trim" => new TrimEditor(files[0]),
                        _ => new CropEditor(files[0]),
                    };
                    try { Console.WriteLine("OK " + string.Join(" ", await editor.PresentAsync())); }
                    catch (Exception ex) { Console.WriteLine("FAIL " + ex.Message); }
                    return 0;
                });

            // `Swivel --frames <wheel|list|shelf|notes|clothesline> <out dir> [light|dark]` renders the menu's
            // animations offscreen to PNGs and prints the draw times.
            case "--frames" when args.Length >= 3:
                return app.RunThenExit(() => WheelFrames.Render(args[1], args[2], args.Length >= 4 ? args[3] : null));

            // `Swivel --rope-frames <out dir>` renders rope and incoming-card filmstrips offscreen (for review).
            case "--rope-frames" when args.Length == 2:
                return app.RunThenExit(() => Task.FromResult(ClotheslineFrames.Render(args[1])));

            // `Swivel --demo <convert|tools|actions> <file> [script] [style]` shows a menu at the pointer without a drag.
            case "--demo" when args.Length >= 3:
                return app.RunThenExit(() => WheelDemo.Run(args.Skip(1).ToArray()));
        }

        return AppHost.Run(app, args);
    }

    static int RunTool(App app, string[] args)
    {
        if (Environment.GetEnvironmentVariable("SWIVEL_SETTINGS") is not { Length: > 0 })
            Prefs.FilePath = System.IO.Path.Combine(Prefs.AppFolder, "settings.preview.json");
        foreach (var feature in new[] { Feature.TextCapture, Feature.Shelf, Feature.ImagePresets, Feature.QuickActions })
            Prefs.SetFeatureEnabled(feature, true);
        if (Environment.GetEnvironmentVariable("SWIVEL_PREVIEW_APPEARANCE") is { } appearance)
            app.ThemeMode = appearance == "light" ? ThemeMode.Light : ThemeMode.Dark;
        app.ShutdownMode = ShutdownMode.OnLastWindowClose;
        app.Startup += (_, _) =>
        {
            QuickActions.Shared.SyncSettings();
            Prefs.Changed += () => app.Dispatcher.BeginInvoke(QuickActions.Shared.SyncSettings);
            var files = args.Skip(2).Select(System.IO.Path.GetFullPath).ToList();
            switch (args[1])
            {
                case "text":
                    if (files.Count > 0) TextCaptureWindow.Present(files[0]);
                    else AppWindow.Shared.Show(AppTab.TextCapture);
                    break;
                case "shelf": Shelf.Shared.Add(files); break;
                case "quick": QuickActions.Shared.Show(files); break;
                case "presets": AppWindow.Shared.Show(AppTab.ImagePresets); break;
                default: AppWindow.Shared.Show(); break;
            }
        };
        return app.Run();
    }

    static async Task<int> RunItem(string[] args)
    {
        var mode = WheelModes.FromId(args[1]) ?? WheelMode.Convert;
        var files = args.Skip(3).Select(System.IO.Path.GetFullPath).ToList();
        var items = Catalog.Items(files, mode);
        var item = items.FirstOrDefault(i => string.Equals(i.Title, args[2], StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            Console.WriteLine($"No item “{args[2]}”. Available: {string.Join(", ", items.Select(i => i.Title))}");
            return 1;
        }
        try
        {
            var result = await item.Run(files);
            Console.WriteLine($"OK {result.Message} [{string.Join(", ", result.Files)}]");
            return 0;
        }
        catch (Info info)
        {
            Console.WriteLine($"OK {info.Message}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {ex.Message}");
            return 2;
        }
    }
}
