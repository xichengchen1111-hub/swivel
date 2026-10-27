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

            // `Swivel --find <request>` prints how Quick Actions reads a file search request, its Windows Search query and
            // the ranked files (`*` = matched every word). Read-only.
            case "--find" when args.Length >= 2:
                return app.RunThenExit(async () =>
                {
                    var request = string.Join(" ", args.Skip(1));
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var plan = FileSearch.Parse(request);
                    Console.WriteLine("rules: " + plan.Summary);
                    Console.WriteLine("meaning of: " + (plan.Topic.Length > 0 ? plan.Topic : "-"));
                    Console.WriteLine("query: " + (FileSearch.QueryString(plan) ?? "-"));
                    var hits = await FileSearch.Search(plan);
                    foreach (var hit in hits)
                        Console.WriteLine($"{(hit.Strict ? "*" : " ")} {(hit.Meaning > 0 ? hit.Meaning.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "    ")} {hit.Path} · {FileSearch.Describe(hit, plan)}");
                    Console.WriteLine($"{hits.Count} files, {clock.Elapsed.TotalSeconds:0.00} s{(FileSearch.IndexReachable ? "" : " (Windows Search is off)")}");
                    return 0;
                });

            // `Swivel --index` reads every document into the semantic index (as Quick Actions does in the background),
            // printing progress; `--index <request>` then lists the closest files with their similarity.
            case "--index":
                return app.RunThenExit(async () =>
                {
                    if (!SemanticIndex.ModelInstalled) { Console.WriteLine("FAIL model files missing in " + SemanticIndex.ModelFolder); return 2; }
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    SemanticIndex.Changed += () => Console.WriteLine($"{clock.Elapsed.TotalSeconds:0.0} s: {SemanticIndex.Progress}");
                    await SemanticIndex.RefreshNow();
                    Console.WriteLine($"{SemanticIndex.Progress?.Ready ?? 0} files, {clock.Elapsed.TotalSeconds:0.0} s");
                    if (args.Length >= 2)
                    {
                        clock.Restart();
                        var closest = SemanticIndex.Closest(string.Join(" ", args.Skip(1)), _ => true, 15);
                        foreach (var (entry, similarity) in closest) Console.WriteLine($"{similarity:0.000} {entry.Path}");
                        Console.WriteLine($"{clock.ElapsedMilliseconds} ms");
                    }
                    return 0;
                });

            // `Swivel --run <convert|tools|actions|apps|codes> <item title> <file>...` runs one menu item headlessly (for testing).
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

            // `Swivel --demo <convert|tools|actions|apps|codes> <file> [script] [style]` shows a menu at the pointer without a drag.
            case "--demo" when args.Length >= 3:
                return app.RunThenExit(() => WheelDemo.Run(args.Skip(1).ToArray()));
        }

        return AppHost.Run(app, args);
    }

    static int RunTool(App app, string[] args)
    {
        if (Environment.GetEnvironmentVariable("SWIVEL_SETTINGS") is not { Length: > 0 })
            Prefs.FilePath = System.IO.Path.Combine(Prefs.AppFolder, "settings.preview.json");
        if (Environment.GetEnvironmentVariable("SWIVEL_QR_LIBRARY_DIR") is { Length: > 0 } library)
            QrLibraryStore.PreviewDirectory = System.IO.Path.GetFullPath(library);
        foreach (var feature in new[] { Feature.TextCapture, Feature.Shelf, Feature.ImagePresets, Feature.QuickActions,
                                        Feature.BatchRename, Feature.PdfPages, Feature.QrCodes, Feature.AppRouting })
            Prefs.SetFeatureEnabled(feature, true);
        if (Environment.GetEnvironmentVariable("SWIVEL_PREVIEW_APPEARANCE") is { } appearance)
            app.ThemeMode = appearance == "light" ? ThemeMode.Light : ThemeMode.Dark;
        app.ShutdownMode = ShutdownMode.OnLastWindowClose;
        app.Startup += (_, _) =>
        {
            QuickActions.Shared.SyncSettings();
            ShortcutWheel.Shared.Sync();
            Prefs.Changed += () => app.Dispatcher.BeginInvoke(() =>
            {
                QuickActions.Shared.SyncSettings();
                ShortcutWheel.Shared.Sync();
            });
            var files = args.Skip(2).Select(System.IO.Path.GetFullPath).ToList();
            switch (args[1])
            {
                case "text":
                    if (files.Count > 0) TextCaptureWindow.Present(files[0]);
                    else AppWindow.Shared.Show(AppTab.TextCapture);
                    break;
                case "shelf": Shelf.Shared.Add(files); break;
                case "quick":
                    QuickActions.Shared.Show(files);
                    if (Environment.GetEnvironmentVariable("SWIVEL_QUICK_QUERY") is { Length: > 0 } query) QuickActions.Shared.Type(query);
                    // Draws the panel into a PNG and prints its rows once the search has settled, then quits.
                    if (Environment.GetEnvironmentVariable("SWIVEL_QUICK_SNAPSHOT") is { Length: > 0 } snapshot)
                    {
                        var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                        later.Tick += (_, _) =>
                        {
                            later.Stop();
                            QuickActions.Shared.Snapshot(System.IO.Path.GetFullPath(snapshot));
                            app.Shutdown();
                        };
                        later.Start();
                    }
                    break;
                case "presets": AppWindow.Shared.Show(AppTab.ImagePresets); break;
                case "feel": AppWindow.Shared.Show(AppTab.Feel); break;
                case "clothesline": AppWindow.Shared.Show(AppTab.Clothesline); break;
                case "style": AppWindow.Shared.Show(AppTab.Style); break;
                case "colors": AppWindow.Shared.Show(AppTab.Colors); break;
                case "rename":
                    if (files.Count > 0) BatchRenameWindow.Present(files);
                    else AppWindow.Shared.Show(AppTab.BatchRename);
                    break;
                case "pdf":
                    if (files.Count > 0) PdfPagesWindow.Present(files);
                    else AppWindow.Shared.Show(AppTab.PdfPages);
                    break;
                case "qr":
                    if (files.Count > 0) QrCodesWindow.Present(files);
                    else AppWindow.Shared.Show(AppTab.QrCodes);
                    break;
                case "apps": AppWindow.Shared.Show(AppTab.AppRouting); break;
                case "library": QrLibraryWindow.Show(); break;
                case "launcher":
                    Prefs.ShortcutWheelEnabled = true;
                    Prefs.IsEnabled = true;
                    ShortcutWheel.Shared.Sync();
                    ShortcutWheel.Shared.Show();
                    // The wheel window hides rather than closes, so quit once it and anything it opened are gone.
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    watch.Tick += (_, _) =>
                    {
                        if (ShortcutWheel.Shared.Wheel.IsVisible || app.Windows.OfType<Window>().Any(w => w.IsVisible)) return;
                        watch.Stop();
                        app.Shutdown();
                    };
                    watch.Start();
                    break;
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
