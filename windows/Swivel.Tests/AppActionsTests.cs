using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Swivel;

namespace Swivel.Tests;

/// App Actions and the keyboard tool wheel (1.4; a port of the app parts of the Mac's FileActionsTests): saved rules,
/// extension filters, delivery through an injected launcher (nothing is ever started), Codex / Claude Code deep links,
/// the 1.4 key migration, the tool wheel's items, shortcut conflicts and the clickable menu's keys and clicks.
[TestClass]
public sealed class AppActionsTests
{
    [TestInitialize]
    public void Reset()
    {
        TestSetup.ResetPrefs();
        Prefs.SoundEffects = false;
        Prefs.LanguageId = "en";
        Sounds.DeviceDisabled = true;
    }

    /// Records what would have been started, opened or copied.
    sealed class FakeLauncher : AppLauncher
    {
        public readonly List<string> Calls = [];
        public string? Handler = "Codex";
        public bool PackagedExists = true;
        public bool ExeExists = true;
        public Exception? StartFails;
        public bool CopySucceeds = true;
        public AppLocation? Started;
        public IReadOnlyList<string>? StartedWith;
        public string? Url;
        public IReadOnlyList<string>? Copied;

        public override Task Start(AppLocation app, IReadOnlyList<string> files)
        {
            Calls.Add("start");
            if (StartFails is { } failure) throw failure;
            Started = app;
            StartedWith = files;
            return Task.CompletedTask;
        }

        public override Task OpenUrl(string url)
        {
            Calls.Add("url");
            if (StartFails is { } failure) throw failure;
            Url = url;
            return Task.CompletedTask;
        }

        public override string? SchemeHandler(string scheme) => Handler;
        public override bool PackagedAppExists(string appUserModelId) => PackagedExists;
        public override bool ExecutableExists(string path) => ExeExists;

        public override bool CopyFiles(IReadOnlyList<string> files)
        {
            Calls.Add("copy");
            Copied = files;
            return CopySucceeds;
        }
    }

    static FileAppTarget Exe(string id = "paint", string name = "Paint", params string[] extensions) =>
        new() { Id = id, Name = name, AppPath = @"C:\Apps\paint.exe", Delivery = AppDelivery.OpenFiles, Extensions = extensions };

    static List<string> Files(params string[] names)
    {
        var dir = TestSetup.NewFolder("apps14-files");
        return names.Select(n =>
        {
            var path = Path.Combine(dir, n);
            File.WriteAllText(path, "x");
            return path;
        }).ToList();
    }

    // MARK: Saved rules

    [TestMethod]
    public void StartersUntilSomethingIsSaved()
    {
        CollectionAssert.AreEqual(new[] { "codex", "claude-code" }, AppTargets.All.Select(t => t.Id).ToArray());
        Assert.AreEqual(AppDelivery.CodexDraft, AppTargets.All[0].Delivery);
        Assert.AreEqual(AppDelivery.ClaudeCodeDraft, AppTargets.All[1].Delivery);
        Assert.IsTrue(AppTargets.All.All(t => t.Enabled && t.Extensions.Count == 0));
        Assert.AreEqual("app.codex", AppTargets.All[0].ActionId);
        // An empty list stays empty (the user removed the starters).
        AppTargets.All = [];
        Assert.IsEmpty(AppTargets.All);
        // A damaged value starts over with the starters.
        Prefs.Set("appTargets.v1", "not a list");
        Assert.HasCount(2, AppTargets.All);
    }

    [TestMethod]
    public void RulesPersistInTheMacShapeAndBadOnesAreLeftOut()
    {
        AppTargets.All =
        [
            Exe("a", "Paint", "png", "jpg"),
            new() { Id = "b", Name = "Photos", BundleId = "Microsoft.Windows.Photos_8wekyb3d8bbwe!App", Delivery = AppDelivery.CopyFiles, Enabled = false },
            Exe("a", "Duplicate"),
            Exe("", "No id"),
            Exe("c", "   "),
            Exe("d", new string('x', 81)),
        ];
        Prefs.Reload();
        var saved = AppTargets.All;
        CollectionAssert.AreEqual(new[] { "a", "b" }, saved.Select(t => t.Id).ToArray());
        Assert.AreEqual(Exe("a", "Paint", "png", "jpg"), saved[0]);
        Assert.IsFalse(saved[1].Enabled);
        Assert.AreEqual(AppDelivery.CopyFiles, saved[1].Delivery);

        var json = JsonNode.Parse(File.ReadAllText(Prefs.FilePath))!["appTargets.v1"]!.AsArray();
        var first = json[0]!.AsObject();
        CollectionAssert.AreEquivalent(new[] { "id", "name", "bundleID", "appPath", "delivery", "extensions", "enabled" },
            first.Select(p => p.Key).ToArray());
        Assert.AreEqual("openFiles", (string?)first["delivery"]);
        Assert.AreEqual("copyFiles", (string?)json[1]!["delivery"]);

        // Rules written elsewhere: an unknown delivery, a repeated id and a nameless rule are skipped; missing
        // `enabled` / `extensions` take their defaults, as on the Mac.
        File.WriteAllText(Prefs.FilePath, """
            {"appTargets.v1": [
              {"id": "x", "name": "X", "bundleID": "", "appPath": "C:\\x.exe", "delivery": "teleport"},
              {"id": "y", "name": "Y", "bundleID": "", "appPath": "C:\\y.exe", "delivery": "codexDraft"},
              {"id": "y", "name": "Y again", "bundleID": "", "appPath": "", "delivery": "openFiles"},
              {"id": "z", "name": "", "bundleID": "", "appPath": "", "delivery": "openFiles"}
            ]}
            """);
        Prefs.Reload();
        var loaded = AppTargets.All;
        Assert.HasCount(1, loaded);
        Assert.AreEqual("Y", loaded[0].Name);
        Assert.IsTrue(loaded[0].Enabled);
        Assert.IsEmpty(loaded[0].Extensions);

        // Save replaces by id, or appends.
        AppTargets.Save(loaded[0] with { Name = "Why" });
        AppTargets.Save(Exe("new", "New"));
        CollectionAssert.AreEqual(new[] { "Why", "New" }, AppTargets.All.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public void ExtensionsFilterFilesAndNormalize()
    {
        var images = Exe("i", "Images", "png", "jpg");
        Assert.IsTrue(images.Accepts([@"C:\a.PNG", @"D:\b c\d.jpg"]));
        Assert.IsFalse(images.Accepts([@"C:\a.png", @"C:\b.pdf"]));
        Assert.IsFalse(images.Accepts([@"C:\folder"]));
        Assert.IsFalse(images.Accepts([]));
        var any = Exe();
        Assert.IsTrue(any.Accepts([@"C:\folder", @"C:\x.pdf"]));
        Assert.IsFalse(any.Accepts([]));

        CollectionAssert.AreEqual(new[] { "png", "jpg", "pdf" }, FileAppTarget.NormalizedExtensions(" PNG, .jpg，pdf  png ").ToArray());
        Assert.IsEmpty(FileAppTarget.NormalizedExtensions(",,  . "));
        CollectionAssert.AreEqual(new[] { "mp4" }, FileAppTarget.NormalizedExtensions("mp4, *.?"));
    }

    [TestMethod]
    public void CustomizeAndTheAppsMenuFollowTheRules()
    {
        Prefs.SetFeatureEnabled(Feature.AppRouting, true);
        AppTargets.All = [Exe("img", "Images", "png", "txt"), Exe("all", "Anything"), Exe("pdf", "PDFs", "pdf") with { Enabled = false }];
        // A category counts every configured extension in it (TXT is a document, PNG an image).
        CollectionAssert.AreEqual(new[] { "app.img", "app.all" }, Catalog.AllItems(WheelMode.Apps, FileKind.Image).ToArray());
        CollectionAssert.AreEqual(new[] { "app.img", "app.all" }, Catalog.AllItems(WheelMode.Apps, FileKind.Document).ToArray());
        CollectionAssert.AreEqual(new[] { "app.all" }, Catalog.AllItems(WheelMode.Apps, FileKind.Video).ToArray());
        Assert.AreEqual("Images", Catalog.DisplayTitle("app.img"));
        // A drag offers the rules that accept all of its files; the keyboard wheel (no files) offers every enabled one.
        CollectionAssert.AreEqual(new[] { "app.all" }, Catalog.DefaultItems([@"C:\x.pdf"], WheelMode.Apps).Select(i => i.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "app.img", "app.all" }, Catalog.DefaultItems([], WheelMode.Apps).Select(i => i.Title).ToArray());
        Prefs.SetFeatureEnabled(Feature.AppRouting, false);
        Assert.IsEmpty(Catalog.DefaultItems([], WheelMode.Apps));
        Assert.IsFalse(WheelMode.Apps.IsAvailable());
    }

    // MARK: Delivery

    [TestMethod]
    public async Task OpenFilesStartsTheAppWithTheFiles()
    {
        Prefs.SetFeatureEnabled(Feature.AppRouting, true);
        var target = Exe("p", "Paint", "png");
        AppTargets.All = [target];
        var files = Files("a.png", "b c.png");
        var launcher = new FakeLauncher();
        var result = await FileAppRouting.Run(target, files, launcher);
        Assert.AreEqual("Opened files in Paint", result.Message);
        Assert.AreEqual(AppLocation.Kinds.Executable, launcher.Started!.Kind);
        Assert.AreEqual(@"C:\Apps\paint.exe", launcher.Started.Value);
        CollectionAssert.AreEqual(files, launcher.StartedWith!.ToArray());
        CollectionAssert.AreEqual(new[] { "start" }, launcher.Calls);

        // A packaged app is activated by its AppUserModelID.
        var photos = new FileAppTarget { Id = "ph", Name = "Photos", BundleId = "Microsoft.Windows.Photos_8wekyb3d8bbwe!App" };
        AppTargets.All = [target, photos];
        launcher = new FakeLauncher();
        await FileAppRouting.Run(photos, files, launcher);
        Assert.AreEqual(new AppLocation(AppLocation.Kinds.Packaged, photos.BundleId, "Photos"), launcher.Started);
    }

    [TestMethod]
    public async Task UnavailableRulesFilesAndAppsAreRefused()
    {
        var target = Exe("p", "Paint", "png");
        AppTargets.All = [target];
        var files = Files("a.png");
        var launcher = new FakeLauncher();
        async Task<string> Refused(FileAppTarget t, IReadOnlyList<string> f) =>
            (await Assert.ThrowsExactlyAsync<ToolError>(() => FileAppRouting.Run(t, f, launcher))).Message;

        Assert.AreEqual("This app action is unavailable or turned off.", await Refused(target, files)); // feature off
        Prefs.SetFeatureEnabled(Feature.AppRouting, true);
        Assert.AreEqual("This app action is unavailable or turned off.", await Refused(target with { Enabled = false }, files));
        Assert.AreEqual("This app action is unavailable or turned off.", await Refused(target with { Name = "Changed" }, files)); // not saved
        Assert.AreEqual("This app action is unavailable or turned off.", await Refused(target, Files("a.pdf")));
        Assert.AreEqual("File is no longer available", await Refused(target, [Path.Combine(TestSetup.Scratch, "gone.png")]));
        launcher.ExeExists = false;
        Assert.AreEqual("Paint is not installed. Choose its application in App Actions.", await Refused(target, files));
        launcher.ExeExists = true;
        launcher.StartFails = new Win32Exception(2);
        Assert.AreEqual("Could not open Paint.", await Refused(target, files));
        // Too many files for one command line.
        var many = Enumerable.Repeat(files[0], FileAppRouting.CommandLineLimit / files[0].Length + 1).ToList();
        launcher.StartFails = null;
        Assert.AreEqual("Too many files for one app launch. Choose fewer files.", await Refused(target, many));
        Assert.IsNull(launcher.Started, "nothing was started");
    }

    [TestMethod]
    public async Task CopyFilesFillsTheClipboardOnlyAfterTheAppStarted()
    {
        Prefs.SetFeatureEnabled(Feature.AppRouting, true);
        var target = Exe("chat", "Chat") with { Delivery = AppDelivery.CopyFiles };
        AppTargets.All = [target];
        var files = Files("a.png", "b.pdf");
        var launcher = new FakeLauncher();
        var result = await FileAppRouting.Run(target, files, launcher);
        Assert.AreEqual("Files copied. Paste them in Chat.", result.Message);
        CollectionAssert.AreEqual(new[] { "start", "copy" }, launcher.Calls);
        Assert.IsEmpty(launcher.StartedWith!, "the app starts without the files");
        CollectionAssert.AreEqual(files, launcher.Copied!.ToArray());

        launcher = new FakeLauncher { StartFails = new InvalidOperationException("no") };
        await Assert.ThrowsExactlyAsync<ToolError>(() => FileAppRouting.Run(target, files, launcher));
        CollectionAssert.AreEqual(new[] { "start" }, launcher.Calls, "a failed launch leaves the clipboard alone");

        launcher = new FakeLauncher { CopySucceeds = false };
        var error = await Assert.ThrowsExactlyAsync<ToolError>(() => FileAppRouting.Run(target, files, launcher));
        Assert.AreEqual("Could not copy the selected files.", error.Message);
    }

    // MARK: Drafts

    static Dictionary<string, string> Query(string url)
    {
        var query = url[(url.IndexOf('?') + 1)..];
        return query.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
    }

    [TestMethod]
    public void DraftLinksNameTheFilesAsEscapedText()
    {
        var dir = @"C:\Users\me\Desktop\Q3 & “final” #1 100%+";
        var files = new[] { dir + @"\报告 a.png", dir + @"\b""c.txt" };
        var codex = AssistantDraft.Url(AppDelivery.CodexDraft, files);
        StringAssert.StartsWith(codex, "codex://threads/new?prompt=");
        Assert.IsFalse(codex.Contains(' ') || codex.Contains('"') || codex.Contains('#') || codex.Contains('+') || codex.Contains('\\'), codex);
        var q = Query(codex);
        CollectionAssert.AreEquivalent(new[] { "prompt", "path" }, q.Keys.ToArray());
        Assert.AreEqual("Selected local files:\n- \"" + dir.Replace("\\", "\\\\") + "\\\\报告 a.png\"\n- \""
            + dir.Replace("\\", "\\\\") + "\\\\b\\\"c.txt\"", q["prompt"]);
        Assert.AreEqual(dir, q["path"]);

        var claude = AssistantDraft.Url(AppDelivery.ClaudeCodeDraft, files);
        StringAssert.StartsWith(claude, "claude://code/new?q=");
        q = Query(claude);
        CollectionAssert.AreEquivalent(new[] { "q", "folder" }, q.Keys.ToArray(), "Claude's file parameter is not used");
        Assert.AreEqual(dir, q["folder"]);

        // Files in different folders: no workspace at all (never a broad common root).
        Assert.IsFalse(Query(AssistantDraft.Url(AppDelivery.CodexDraft, [@"C:\a\x.png", @"C:\b\y.png"])).ContainsKey("path"));
        // A folder is its own workspace.
        var folder = TestSetup.NewFolder("apps14-draft");
        Assert.AreEqual(folder, Query(AssistantDraft.Url(AppDelivery.ClaudeCodeDraft, [folder]))["folder"]);
        Assert.AreEqual(folder, Query(AssistantDraft.Url(AppDelivery.ClaudeCodeDraft, [folder, Path.Combine(folder, "in.png")]))["folder"]);

        Assert.ThrowsExactly<ToolError>(() => AssistantDraft.Url(AppDelivery.CodexDraft, []));
        Assert.ThrowsExactly<ToolError>(() => AssistantDraft.Url(AppDelivery.OpenFiles, files));
        Assert.ThrowsExactly<ToolError>(() => AssistantDraft.Url(AppDelivery.CodexDraft, ["relative.png"]));
        var tooMany = Enumerable.Range(0, 400).Select(i => $@"C:\Users\me\Documents\Project folder\file number {i}.png").ToArray();
        Assert.AreEqual("Too many file paths for one draft. Choose fewer files.",
            Assert.ThrowsExactly<ToolError>(() => AssistantDraft.Url(AppDelivery.CodexDraft, tooMany)).Message);
    }

    [TestMethod]
    public async Task DraftsOpenTheirLinkAndNeverStartAnApp()
    {
        Prefs.SetFeatureEnabled(Feature.AppRouting, true);
        var files = Files("notes.md");
        var codex = AppTargets.All.Single(t => t.Id == "codex");
        var launcher = new FakeLauncher();
        var result = await FileAppRouting.Run(codex, files, launcher);
        Assert.AreEqual("Opened Codex draft with file references. Review and send there.", result.Message);
        CollectionAssert.AreEqual(new[] { "url" }, launcher.Calls);
        StringAssert.StartsWith(launcher.Url, "codex://threads/new?prompt=");

        launcher = new FakeLauncher { Handler = null };
        var claude = AppTargets.All.Single(t => t.Id == "claude-code");
        var error = await Assert.ThrowsExactlyAsync<ToolError>(() => FileAppRouting.Run(claude, files, launcher));
        Assert.AreEqual("No app on this PC opens claude:// links. Install Claude Code, then try again.", error.Message);
        Assert.IsEmpty(launcher.Calls);
        Assert.IsNull(FileAppRouting.Locate(claude, launcher));
        Assert.AreEqual("Codex", FileAppRouting.Locate(codex, new FakeLauncher())!.DisplayName);
    }

    [TestMethod]
    public void UnknownSchemesHaveNoHandler()
    {
        Assert.IsNull(Association.ForScheme("swivel-test-no-such-scheme-14"));
        Assert.IsNotNull(Association.ForScheme("https"), "a browser opens https links");
        Assert.IsFalse(PackagedApps.Exists("No.Such.Package_0000000000000!App"));
    }

    [TestMethod]
    public void ThePickerListsStartMenuAndPackagedApps()
    {
        var apps = InstalledApps.Load().GetAwaiter().GetResult();
        Assert.IsNotEmpty(apps);
        Assert.IsTrue(apps.All(a => a.Name.Length > 0 && (a.AppPath.Length > 0) != (a.BundleId.Length > 0)));
        Assert.IsTrue(apps.Where(a => a.AppPath.Length > 0).All(a => a.AppPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(a.AppPath)));
        Assert.IsTrue(apps.Where(a => a.BundleId.Length > 0).All(a => a.BundleId.Contains('!')));
        // Every Windows 11 PC has some packaged app (Settings, Calculator, Photos…), and they can be found again.
        var packaged = apps.FirstOrDefault(a => a.BundleId.Length > 0);
        if (packaged != null) Assert.IsTrue(PackagedApps.Exists(packaged.BundleId), packaged.BundleId);
    }

    [STATestMethod]
    public void RenderTheForm()
    {
        foreach (bool dark in new[] { false, true })
        {
            var form = new AppTargetForm(Exe("p", "Paint", "png", "jpg"), _ => { }, new FakeLauncher(), () => Task.FromResult(new List<InstalledApp> { new("Paint", @"C:\Apps\paint.exe", "") }))
            {
                Left = -32000, Top = -32000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light,
            };
            form.Show();
            try
            {
                PumpUntil(() => form.InstalledBox.Items.Count == 2);
                var root = (FrameworkElement)form.Content;
                form.UpdateLayout();
                int w = (int)Math.Ceiling(root.ActualWidth) + 48, h = (int)Math.Ceiling(root.ActualHeight) + 48;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(root.TryFindResource("ApplicationBackgroundBrush") as Brush ?? (dark ? Brushes.Black : Brushes.White), null, new Rect(0, 0, w, h));
                    dc.DrawRectangle(new VisualBrush(root), null, new Rect(24, 24, root.ActualWidth, root.ActualHeight));
                }
                var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var folder = Environment.GetEnvironmentVariable("SWIVEL_TEST_PNG_DIR") is { Length: > 0 } dir ? dir : TestSetup.NewFolder("apps14-form-png");
                Directory.CreateDirectory(folder);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(folder, $"app-form-{(dark ? "dark" : "light")}.png"));
                encoder.Save(file);
            }
            finally { form.Close(); }
        }
    }

    // MARK: Settings form

    [STATestMethod]
    public void TheFormChecksNameExtensionsAndApp()
    {
        FileAppTarget? saved = null;
        var launcher = new FakeLauncher();
        var exe = Files("tool.exe")[0];
        var form = new AppTargetForm(null, v => saved = v, launcher, () => Task.FromResult(new List<InstalledApp>
        {
            new("Notepad", @"C:\Windows\notepad.exe", ""),
            new("Photos", "", "Microsoft.Windows.Photos_8wekyb3d8bbwe!App"),
        }));
        PumpUntil(() => form.InstalledBox.Items.Count == 3);
        Assert.AreEqual("No application selected", form.AppLabelText);
        form.NameBox.Text = "";
        Assert.IsFalse(form.TrySave());
        Assert.AreEqual("Enter a name of 1–80 characters.", form.IssueText);

        // Picking an app fills the empty name.
        form.InstalledBox.SelectedIndex = 2;
        Assert.AreEqual("Photos", form.NameBox.Text);
        Assert.AreEqual("Photos", form.AppLabelText);
        form.ExtensionsBox.Text = ", .";
        Assert.IsFalse(form.TrySave());
        Assert.AreEqual("Enter file extensions separated by commas.", form.IssueText);
        form.ExtensionsBox.Text = "PNG, jpg";
        launcher.PackagedExists = false;
        Assert.IsFalse(form.TrySave());
        Assert.AreEqual("Choose an installed application first.", form.IssueText);
        launcher.PackagedExists = true;
        Assert.IsTrue(form.TrySave());
        Assert.AreEqual("Microsoft.Windows.Photos_8wekyb3d8bbwe!App", saved!.BundleId);
        Assert.AreEqual("", saved.AppPath);
        CollectionAssert.AreEqual(new[] { "png", "jpg" }, saved.Extensions.ToArray());
        Assert.AreEqual(AppDelivery.OpenFiles, saved.Delivery);

        // A browsed .exe; anything else (or Swivel itself) is refused.
        Assert.IsFalse(form.UseExecutable(Files("notes.txt")[0]));
        Assert.AreEqual("Choose an installed application first.", form.IssueText);
        Assert.IsTrue(form.UseExecutable(exe));
        Assert.AreEqual("tool.exe", form.AppLabelText);
        Assert.AreEqual("", form.Value.BundleId);
        Assert.AreEqual("Photos", form.NameBox.Text, "a typed name is kept");

        // Drafts go to the link's app: no app to pick, and no link handler means no save.
        form.DeliveryBox.SelectedIndex = Array.IndexOf(AppDeliveries.All, AppDelivery.ClaudeCodeDraft);
        Assert.IsFalse(form.InstalledBox.IsEnabled);
        Assert.AreEqual("Codex", form.AppLabelText); // the fake's handler name
        launcher.Handler = null;
        Assert.IsFalse(form.TrySave());
        Assert.AreEqual("No app on this PC opens claude:// links. Install Photos, then try again.", form.IssueText);

        // Editing keeps the id and finds the installed app.
        var edit = new AppTargetForm(saved, v => saved = v, new FakeLauncher(), () => Task.FromResult(new List<InstalledApp>
        {
            new("Notepad", @"C:\Windows\notepad.exe", ""),
            new("Photos", "", "Microsoft.Windows.Photos_8wekyb3d8bbwe!App"),
        }));
        PumpUntil(() => edit.InstalledBox.Items.Count == 3);
        Assert.AreEqual(2, edit.InstalledBox.SelectedIndex);
        Assert.AreEqual("png, jpg", edit.ExtensionsBox.Text);
        var id = saved.Id;
        edit.NameBox.Text = "My Photos";
        Assert.IsTrue(edit.TrySave());
        Assert.AreEqual(id, saved.Id);
        Assert.AreEqual("My Photos", saved.Name);
    }

    static void PumpUntil(Func<bool> done, double seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Assert.IsTrue(done(), "timed out");
    }

    // MARK: Keys

    [TestMethod]
    public void OldKeyChoicesAreKeptAndTheNewMenusGetFreeKeys()
    {
        // Defaults: five different keys.
        CollectionAssert.AreEqual(new[] { TriggerKey.Shift, TriggerKey.Control, TriggerKey.Alt, TriggerKey.Space, TriggerKey.Tab },
            WheelModes.All.Select(Prefs.Key).ToArray());
        // A 1.3 settings file: Convert on Alt, Actions on Shift (Tools left at its default).
        Prefs.Set("key.convert", "alt");
        Prefs.Set("key.actions", "shift");
        Prefs.Reload();
        Assert.AreEqual(TriggerKey.Alt, Prefs.Key(WheelMode.Convert));
        Assert.AreEqual(TriggerKey.Shift, Prefs.Key(WheelMode.Actions));
        Assert.AreEqual(TriggerKey.Control, Prefs.Key(WheelMode.Tools));
        Assert.AreEqual(TriggerKey.Space, Prefs.Key(WheelMode.Apps));
        Assert.AreEqual(TriggerKey.Tab, Prefs.Key(WheelMode.Codes));
        // A saved key a new menu would want: it goes to the first free key instead.
        Prefs.Set("key.tools", "space");
        Assert.AreEqual(TriggerKey.Space, Prefs.Key(WheelMode.Tools));
        Assert.AreEqual(TriggerKey.Control, Prefs.Key(WheelMode.Apps));
        Assert.HasCount(5, WheelModes.All.Select(Prefs.Key).Distinct().ToList());
        // Swapping still keeps every key different.
        Prefs.SetKey(TriggerKey.Tab, WheelMode.Convert);
        Assert.AreEqual(TriggerKey.Tab, Prefs.Key(WheelMode.Convert));
        Assert.AreEqual(TriggerKey.Alt, Prefs.Key(WheelMode.Codes));
        Assert.HasCount(5, WheelModes.All.Select(Prefs.Key).Distinct().ToList());
    }

    // MARK: Tool wheel

    sealed class FakeHotKey(bool registered) : IHotKeyRegistration
    {
        public bool IsRegistered => registered && !Disposed;
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    sealed class Registrar
    {
        public readonly List<(uint Modifiers, uint Key, FakeHotKey HotKey)> Made = [];
        public bool Succeeds = true;
        public Action? Fire;

        public IHotKeyRegistration Register(uint modifiers, uint key, Action action)
        {
            var hotKey = new FakeHotKey(Succeeds);
            Made.Add((modifiers, key, hotKey));
            Fire = action;
            return hotKey;
        }
    }

    [TestMethod]
    public void ToolWheelItemsFollowTheirSwitches()
    {
        var wheel = new ShortcutWheel(new Registrar().Register);
        CollectionAssert.AreEqual(new[] { "Open Swivel…" }, wheel.Items().Select(i => i.Title).ToArray());
        foreach (var feature in new[] { Feature.BatchRename, Feature.PdfPages, Feature.QrCodes, Feature.AppRouting }) Prefs.SetFeatureEnabled(feature, true);
        var all = wheel.Items();
        CollectionAssert.AreEqual(new[] { "Batch Rename", "Arrange PDF Pages", "Read QR Codes", "QR Library", "App Actions", "Open Swivel…" },
            all.Select(i => i.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "Batch Rename", "PDF Pages", "Scan QR", "QR Library", "App Actions", "Open Swivel…" },
            all.Select(i => i.DisplayTitle).ToArray());
        Prefs.SetFeatureEnabled(Feature.PdfPages, false);
        Prefs.SetFeatureEnabled(Feature.QrCodes, false);
        CollectionAssert.AreEqual(new[] { "Batch Rename", "App Actions", "Open Swivel…" }, wheel.Items().Select(i => i.Title).ToArray());
        // An item whose tool was turned off after the wheel opened refuses to run.
        var rename = wheel.Items()[0];
        Prefs.SetFeatureEnabled(Feature.BatchRename, false);
        Assert.ThrowsExactly<ToolError>(() => rename.Run([]).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void TheShortcutRegistersAndAConflictIsNeverReady()
    {
        var registrar = new Registrar();
        var wheel = new ShortcutWheel(registrar.Register);
        int changes = 0;
        wheel.Changed += () => changes++;
        wheel.Sync();
        Assert.IsEmpty(registrar.Made, "off by default");
        Assert.IsFalse(wheel.Available);

        Prefs.ShortcutWheelEnabled = true;
        wheel.Sync();
        Assert.HasCount(1, registrar.Made);
        Assert.AreEqual(HotKey.MOD_CONTROL | HotKey.MOD_ALT, registrar.Made[0].Modifiers);
        Assert.AreEqual(0x4Au, registrar.Made[0].Key); // J
        Assert.IsTrue(wheel.Available);
        Assert.AreEqual(WheelShortcut.J, wheel.RegisteredShortcut);
        Assert.AreEqual(1, changes);
        wheel.Sync();
        Assert.HasCount(1, registrar.Made, "an unchanged shortcut isn't registered again");

        // Another shortcut: the old one is released first. One another app holds is reported, and tried again later.
        registrar.Succeeds = false;
        Prefs.WheelShortcut = WheelShortcut.Space;
        wheel.Sync();
        Assert.IsTrue(registrar.Made[0].HotKey.Disposed);
        Assert.AreEqual(0x20u, registrar.Made[1].Key);
        Assert.IsFalse(wheel.Available);
        Assert.AreEqual(WheelShortcut.Space, wheel.RegisteredShortcut);
        registrar.Succeeds = true;
        wheel.Sync();
        Assert.IsTrue(wheel.Available);
        Assert.HasCount(3, registrar.Made);

        // Turning the tool wheel (or the Wheel) off releases it.
        Prefs.IsEnabled = false;
        wheel.Sync();
        Assert.IsTrue(registrar.Made[2].HotKey.Disposed);
        Assert.IsFalse(wheel.Available);
        Assert.IsNull(wheel.RegisteredShortcut);
        Prefs.IsEnabled = true;
        wheel.Sync();
        Assert.HasCount(4, registrar.Made);
        Assert.IsTrue(wheel.Available);
        Prefs.ShortcutWheelEnabled = false;
        wheel.Sync();
        Assert.IsTrue(registrar.Made[3].HotKey.Disposed);
        Assert.IsFalse(wheel.Available);
        // The registered shortcut opens the wheel: here it does nothing while the tool wheel is off.
        registrar.Fire!();
    }

    // MARK: The clickable menu

    static List<WheelItem> Items(int count, List<string>? ran = null) => Enumerable.Range(0, count)
        .Select(i => new WheelItem($"Item {i}", _ =>
        {
            ran?.Add($"Item {i}");
            return Task.FromResult(new ActionResult(""));
        }))
        .ToList();

    static WheelView ClickableMenu(MenuStyle style, int count, int pages = 1)
    {
        var view = new WheelView { ViewSize = WheelView.MenuSize(style, count, live: style == MenuStyle.Wheel), MenuStyle = style, ManualClock = true, IsLive = true };
        view.IsClickable = true;
        view.Configure(Items(count), WheelMode.Tools, 0, pages);
        view.BeginOpen(new Point(view.ViewSize.Width / 2, view.ViewSize.Height / 2));
        view.Settle();
        return view;
    }

    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    [STATestMethod]
    public void ArrowKeysStepThroughTheItemsAndEnterRunsOne()
    {
        foreach (var style in MenuStyles.All)
        {
            var view = ClickableMenu(style, 4);
            var clicked = new List<string>();
            int cancels = 0, pages = 0;
            view.Clicked = item => clicked.Add(item.Title);
            view.Cancelled = () => cancels++;
            view.NextPage = () => pages++;
            Assert.IsTrue(view.HandleKey(Key.Enter), style.ToString());
            Pump();
            Assert.IsEmpty(clicked, "Enter with nothing highlighted does nothing");
            Assert.IsTrue(view.HandleKey(Key.Right));
            Assert.AreEqual(0, view.Hovered);
            view.HandleKey(Key.Down);
            Assert.AreEqual(1, view.Hovered);
            view.HandleKey(Key.Up);
            view.HandleKey(Key.Left);
            Assert.AreEqual(3, view.Hovered, "wraps around");
            view.HandleKey(Key.Right);
            Assert.AreEqual(0, view.Hovered);
            Assert.IsTrue(view.HandleKey(Key.Space));
            Assert.AreEqual(1, pages);
            Assert.IsFalse(view.HandleKey(Key.A));
            view.HandleKey(Key.Enter);
            Assert.AreEqual(0, view.Pressed, "pressed in on the key, before the action runs");
            Pump();
            CollectionAssert.AreEqual(new[] { "Item 0" }, clicked);
            // While it is pressed, keys do nothing more.
            view.HandleKey(Key.Enter);
            view.HandleKey(Key.Escape);
            Pump();
            Assert.HasCount(1, clicked);
            Assert.AreEqual(0, cancels);

            var fresh = ClickableMenu(style, 4);
            int closed = 0;
            fresh.Cancelled = () => closed++;
            fresh.HandleKey(Key.Escape);
            Assert.AreEqual(1, closed);
        }
        // A drag menu ignores keys.
        var drag = ClickableMenu(MenuStyle.Wheel, 3);
        drag.IsClickable = false;
        Assert.IsFalse(drag.HandleKey(Key.Right));
        Assert.IsNull(drag.Hovered);
    }

    [STATestMethod]
    public void ClicksRunTheItemUnderThePointerAndTheHubCancels()
    {
        var view = ClickableMenu(MenuStyle.Wheel, 4);
        var clicked = new List<string>();
        int cancels = 0;
        view.Clicked = item => clicked.Add(item.Title);
        view.Cancelled = () => cancels++;
        var c = new Point(view.ViewSize.Width / 2, view.ViewSize.Height / 2);
        // The hub cancels, even without moving first.
        Assert.IsFalse(view.Click(c));
        Assert.AreEqual(1, cancels);
        // The top sector is item 0; a click there works without a hover first.
        Assert.IsTrue(view.Click(new Point(c.X, c.Y - 90)));
        Pump();
        CollectionAssert.AreEqual(new[] { "Item 0" }, clicked);

        var list = ClickableMenu(MenuStyle.List, 3);
        list.Clicked = item => clicked.Add(item.Title);
        // The second row of the list (rows start under the header).
        Assert.IsTrue(list.Click(new Point(list.ViewSize.Width / 2, WheelView.HeaderHeight + WheelView.ListRow * 1.5 + 4)));
        Pump();
        Assert.AreEqual("Item 1", clicked[^1]);
    }

    [STATestMethod]
    public void ClickableMenusOfferEveryItemToUIAutomation()
    {
        var view = ClickableMenu(MenuStyle.Notes, 3, pages: 2);
        var clicked = new List<string>();
        int cancels = 0, pages = 0;
        view.Clicked = item => clicked.Add(item.Title);
        view.Cancelled = () => cancels++;
        view.NextPage = () => pages++;
        var peer = (WheelViewPeer)UIElementAutomationPeer.CreatePeerForElement(view);
        Assert.AreEqual(AutomationControlType.Menu, peer.GetAutomationControlType());
        var children = peer.GetChildren();
        CollectionAssert.AreEqual(new[] { "Item 0", "Item 1", "Item 2", "Cancel", "Next Page" }, children.Select(p => p.GetName()).ToArray());
        Assert.IsTrue(children.All(p => p.GetAutomationControlType() == AutomationControlType.Button));
        ((IInvokeProvider)children[1].GetPattern(PatternInterface.Invoke)).Invoke();
        Pump();
        CollectionAssert.AreEqual(new[] { "Item 1" }, clicked);
        ((IInvokeProvider)children[3].GetPattern(PatternInterface.Invoke)).Invoke();
        ((IInvokeProvider)children[4].GetPattern(PatternInterface.Invoke)).Invoke();
        Assert.AreEqual(1, cancels);
        Assert.AreEqual(1, pages);
        // A drag menu has no buttons.
        view.IsClickable = false;
        peer.ResetChildrenCache();
        Assert.AreNotEqual(AutomationControlType.Menu, peer.GetAutomationControlType());
        Assert.IsTrue(peer.GetChildren() is null || peer.GetChildren().Count == 0);
    }

    [STATestMethod]
    public void TheControllerPagesAClickableMenuWithSpace()
    {
        // The window is never shown: tests drive the controller's pages directly.
        var controller = new WheelController();
        var pages = WheelController.Paginate(Items(11));
        Assert.HasCount(2, pages);
        Assert.IsFalse(controller.IsVisible);
        controller.Dismiss(); // nothing to close
        Assert.IsFalse(controller.IsVisible);
    }

    // MARK: Pictures

    [STATestMethod]
    public void RenderClickableWheels()
    {
        foreach (var feature in new[] { Feature.BatchRename, Feature.PdfPages, Feature.QrCodes, Feature.AppRouting }) Prefs.SetFeatureEnabled(feature, true);
        var items = new ShortcutWheel(new Registrar().Register).Items();
        var folder = Environment.GetEnvironmentVariable("SWIVEL_TEST_PNG_DIR") is { Length: > 0 } dir ? dir : TestSetup.NewFolder("apps14-wheel-png");
        Directory.CreateDirectory(folder);
        foreach (var style in MenuStyles.All)
        {
            foreach (bool dark in new[] { false, true })
            {
                foreach (var (hover, paged, name) in new[] { ((int?)null, false, "idle"), (1, false, "hover"), ((int?)2, true, "paged") })
                {
                    var shown = paged ? items.Concat(items).Concat(items).Take(10).ToList() : items;
                    var pageItems = WheelController.Paginate(shown);
                    var view = new WheelView
                    {
                        ViewSize = WheelView.MenuSize(style, pageItems[0].Count),
                        MenuStyle = style,
                        ManualClock = true,
                        DarkOverride = dark,
                        Subtitle = "Click or use arrows · Enter · Space for next page",
                    };
                    view.IsClickable = true;
                    view.Configure(pageItems[0], WheelMode.Tools, 0, pageItems.Count);
                    view.ShowHover(hover);
                    view.Settle();
                    var size = view.ViewSize;
                    view.Measure(size);
                    view.Arrange(new Rect(size));
                    view.Redraw();
                    int w = (int)size.Width + 40, h = (int)size.Height + 40;
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        // A plain desktop-ish backdrop so the frosted surfaces read.
                        dc.DrawRectangle(new LinearGradientBrush(dark ? Color.FromRgb(20, 30, 48) : Color.FromRgb(160, 190, 220),
                            dark ? Color.FromRgb(50, 30, 60) : Color.FromRgb(230, 210, 190), 45), null, new Rect(0, 0, w, h));
                        dc.DrawRectangle(new VisualBrush(view), null, new Rect(20, 20, size.Width, size.Height));
                    }
                    var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(folder, $"tool-wheel-{style.Id()}-{(dark ? "dark" : "light")}-{name}.png"));
                    encoder.Save(file);
                }
            }
        }
        Assert.HasCount(30, Directory.GetFiles(folder, "tool-wheel-*.png"));
    }
}
