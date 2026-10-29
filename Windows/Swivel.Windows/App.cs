using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Swivel.Core;
using Forms = System.Windows.Forms;

namespace Swivel.Windows;

public sealed partial class App : Application
{
    public static App CurrentApp => (App)Current;
    public Prefs Prefs { get; private set; } = new();
    public ObservableCollection<string> Shelf { get; } = new();
    public List<IReadOnlyList<RenameLocation>> RenameHistory { get; } = new();
    public List<IReadOnlyList<MoveLocation>> MoveHistory { get; } = new();
    internal MoveOutcome? LastMoveOutcome { get; private set; }
    internal bool LastMoveWasUndo { get; private set; }
    public KeepAwake Awake { get; private set; } = null!;
    public MainWindow Home { get; private set; } = null!;
    private Forms.NotifyIcon? tray;
    private Hotkeys? hotkeys;
    private FloatingShelf? floatingShelf;
    private ClotheslineWindow? clothesline;
    internal ObservableCollection<HungPrint> Prints { get; } = new();
    private Mutex? mutex;
    private readonly List<ActionWindow> panels = new();
    private CancellationTokenSource? workCancellation;
    private Task? work;
    private Feature? workFeature;
    private bool exiting;
    private Window? operationDialog;
    private Window? workOwner;
    public bool Busy => workCancellation is not null;
    public string L(string english) => Strings.L(english, Prefs.Language);

    private readonly bool validationHost;
    internal bool IsValidationHost=>validationHost;
    public App():this(false) { }
    // Internal native-test host only. No command-line/environment switch can
    // bypass the normal product startup or access this constructor.
    internal App(bool validationHost)
    {
        this.validationHost=validationHost;
        if(validationHost)ShutdownMode=ShutdownMode.OnExplicitShutdown;
        // Use the framework's Windows Fluent controls and system theme tracking.
        // Keep the native title bar, focus visuals and high-contrast resources.
        Resources.MergedDictionaries.Add(new ResourceDictionary {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.xaml")
        });
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // Exactly `--mcp` starts the agent server on stdin/stdout; every other argument is
        // ignored and the normal app starts. There is no other command-line execution.
        if (args.Length == 1 && args[0] == "--mcp") return McpHost.Run();
        new App().Run();
        return 0;
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        // Application queues startup in its constructor; native tests may pump
        // the Dispatcher without calling Run. Never load real settings/services.
        if(validationHost)return;
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        mutex = new Mutex(true, @"Local\Swivel.WindowsPreview", out bool created);
        if (!created)
        {
            MessageBox.Show(Strings.L("Swivel is already running. Open it from the notification area.", "system"), "Swivel");
            mutex.Dispose(); mutex = null; Shutdown(); return;
        }
        Prefs = SettingsStore.Load(out bool failed);
        Awake = new KeepAwake();
        Home = new MainWindow(this);
        MainWindow = Home;
        hotkeys = new Hotkeys(() => OpenPanel(true), () => OpenPanel(false), () => OpenPanel(true, custom: true));
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Swivel · Windows Preview", Visible = true };
        tray.DoubleClick += (_, _) => ShowHome();
        Prefs.Changed += PreferencesChanged;
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        Awake.Changed += () => Home.UpdateAwake();
        UpdateTray();
        Home.Show();
        if (!hotkeys.Configure(Prefs)) Home.SetStatus(L("A shortcut is in use. Open the tool from the tray menu."));
        if (failed) Home.SetStatus(L("Preferences could not be loaded. Defaults are in use; settings will be saved only when changed."));
    }
    private void SystemAppearanceChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.HighContrast)) return;
        Dispatcher.Invoke(() => { foreach (var panel in panels.ToArray()) panel.RefreshActions(); clothesline?.Refresh(); });
    }
    private void PreferencesChanged()
    {
        try { SettingsStore.Save(Prefs); }
        catch { Home.SetStatus(L("Preferences could not be saved. Your choices apply only to this session.")); }
        if (!Prefs.IsOn(Feature.KeepAwake)) Try(() => Awake.Stop());
        if (!Prefs.IsOn(Feature.Wheel)) wheelGesture?.Close();
        if (!Prefs.IsOn(Feature.Shelf)) floatingShelf?.Close(); else floatingShelf?.Refresh();
        if (!Prefs.IsOn(Feature.Clothesline)) { clothesline?.Close(); imageArrivals?.Close(); } else clothesline?.Refresh();
        if (workFeature.HasValue && !Prefs.IsOn(workFeature.Value)) CancelWork();
        foreach (var panel in panels.ToArray())
        {
            if (!Prefs.IsOn(panel.PanelFeature)) panel.Close();
            else panel.RefreshActions();
        }
        if (hotkeys is not null && !hotkeys.Configure(Prefs)) Home.SetStatus(L("A shortcut is in use. Open the tool from the tray menu."));
        Home.Refresh();
        UpdateTray();
    }
    private void UpdateTray()
    {
        if (tray is null) return;
        var old = tray.ContextMenuStrip;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L("Open Swivel"), null, (_, _) => ShowHome());
        if (Prefs.IsOn(Feature.Wheel))
        {
            menu.Items.Add(L("Wheel") + "   " + Prefs.Shortcuts.Wheel.Label, null, (_, _) => OpenPanel(true));
            menu.Items.Add(L("Wheel Drag Gesture…"), null, (_, _) => OpenWheelGesture());
        }
        if (Prefs.IsOn(Feature.QuickActions)) menu.Items.Add(L("Quick Actions") + "   " + Prefs.Shortcuts.Quick.Label, null, (_, _) => OpenPanel(false));
        if (Prefs.IsOn(Feature.MyWheel)) menu.Items.Add(L("My Wheel") + "   " + Prefs.Shortcuts.Custom.Label, null, (_, _) => OpenPanel(true, custom: true));
        if (Prefs.IsOn(Feature.Shelf)) menu.Items.Add(L("File Shelf"), null, (_, _) => OpenShelf());
        if (Prefs.IsOn(Feature.Clothesline)) menu.Items.Add(L("Clothesline"), null, (_, _) => OpenClothesline());
        if (Prefs.IsOn(Feature.Clothesline)) menu.Items.Add(L("Image Arrival Cards…"), null, (_, _) => OpenImageArrivalCards());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L("Quit Swivel"), null, async (_, _) => await QuitAsync());
        tray.ContextMenuStrip = menu;
        old?.Dispose();
    }
    public void OpenShelf() => Try(() => {
        if (!Prefs.IsOn(Feature.Shelf)) throw new UserError("Enable File Shelf in Settings first.");
        if (floatingShelf is null) { var window = new FloatingShelf(this); floatingShelf = window; window.Closed += (_,_) => { if (ReferenceEquals(floatingShelf,window)) floatingShelf = null; }; }
        floatingShelf.Show(); if (floatingShelf.WindowState == WindowState.Minimized) floatingShelf.WindowState = WindowState.Normal; floatingShelf.Activate();
    });
    public void ShowHome() { Home.Show(); Home.WindowState = WindowState.Normal; Home.Activate(); }
    public void OpenClothesline() => Try(() => {
        if (!Prefs.IsOn(Feature.Clothesline)) throw new UserError("Enable Clothesline in Settings first.");
        if (clothesline is null) { var window = new ClotheslineWindow(this); clothesline = window; window.Closed += (_,_) => { if (ReferenceEquals(clothesline,window)) clothesline = null; }; }
        clothesline.Show(); if (clothesline.WindowState == WindowState.Minimized) clothesline.WindowState = WindowState.Normal; clothesline.Activate();
    });
    public void OpenPanel(bool wheel, IEnumerable<string>? files = null, bool custom = false)
        => Try(() => OpenPanelCore(wheel, files, custom));
    private void OpenPanelCore(bool wheel, IEnumerable<string>? files, bool custom)
    {
        if (!Prefs.IsOn(custom ? Feature.MyWheel : wheel ? Feature.Wheel : Feature.QuickActions)) return;
        var existing = panels.FirstOrDefault(x => x.IsWheel == wheel && x.IsCustom == custom);
        if (existing is not null) { existing.Show(); if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal; existing.Activate(); if (files is not null) existing.SetFiles(files); return; }
        var panel = new ActionWindow(this, wheel, custom);
        panels.Add(panel);
        panel.Closed += (_, _) => panels.Remove(panel);
        panel.Show();
        if (files is not null) panel.SetFiles(files);
    }
    public void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { ShowError(ex); }
    }
    public void ShowError(Exception error)
    {
        string text = error is UserError user ? user.Localized(L) : L("The operation could not finish. Check the file format, permissions, free space and whether another app is using the file.");
        MessageBox.Show(text, "Swivel", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    public string[] Validate(IEnumerable<string> files)
    {
        var paths = files.Take(101).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > 100) throw new UserError("Choose up to 100 files at a time.");
        foreach (string path in paths) NativeFiles.ValidateInput(path);
        return paths;
    }
    public void AddShelf(IEnumerable<string> files)
    {
        if (!Prefs.IsOn(Feature.Shelf)) throw new UserError("Enable File Shelf in Settings first.");
        var paths = Validate(files);
        if (Shelf.Count + paths.Count(x => !Shelf.Contains(x, StringComparer.OrdinalIgnoreCase)) > 100)
            throw new UserError("Choose up to 100 files at a time.");
        foreach (var path in paths) if (!Shelf.Contains(path, StringComparer.OrdinalIgnoreCase)) Shelf.Add(path);
    }
    public void CancelWork()
    {
        workCancellation?.Cancel();
        operationDialog?.Close();
    }
    internal void ClearPrints() { if (workFeature == Feature.Clothesline) CancelWork(); Prints.Clear(); }
    private bool ShowOperationDialog(Window dialog, Window owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        operationDialog = dialog;
        try { dialog.Owner = owner; return dialog.ShowDialog() == true && !token.IsCancellationRequested; }
        finally { operationDialog = null; }
    }
    private async Task ShowDoctorOperationAsync(Window dialog,DoctorReportSnapshot snapshot,Window owner,CancellationToken token,DoctorLabelSet? labels=null)
    {
        var root=dialog.Content as System.Windows.Controls.DockPanel??throw new InvalidOperationException("Doctor layout requires a dock panel.");
        var footer=root.Children.OfType<System.Windows.Controls.Panel>().Single(x=>System.Windows.Controls.DockPanel.GetDock(x)==System.Windows.Controls.Dock.Bottom);
        var assistant=new DoctorAssistantSession(this,dialog,footer,snapshot,token,labels:labels);
        try{ShowOperationDialog(dialog,owner,token);}finally{await assistant.FinishAsync();}
    }
    // Read-only Doctor workers must finish their own handle cleanup before a
    // cancelled session suppresses a late read failure. Successful resources
    // still reach the caller's using declaration before its token check.
    private static async Task<T> AwaitDoctorReadAsync<T>(Task<T> pending,CancellationToken token)
    {
        try { return await pending; }
        catch when(token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }
    private static Task<T> OnSta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try { completion.SetResult(action()); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "Swivel image worker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal Task PreviewShelfFileAsync(string[] files, Window owner, Action<string> report)
    {
        string id = files.Length == 1 && Catalog.IsImage(files[0]) ? "image.preview" : "file.preview";
        return RunActionAsync(new(id, "Preview File", SingleFile: true), files, owner, report, shelfPreview: true);
    }

    public async Task RunActionAsync(ToolAction action, string[] files, Window owner, Action<string> report, QrEntry? qrEntry = null,string? assistantRequest=null,bool shelfPreview=false)
    {
        // Opening the independent watcher does not validate unrelated file input
        // or reserve the shared editor operation for a whole listening session.
        if (action.Id == "clothesline.watch") { if (!exiting) OpenImageArrivalCards(); return; }
        if (action.Id == "wheel.gesture") { if (!exiting) OpenWheelGesture(); return; }
        if (Busy || exiting) { report(L("Another operation is running.")); return; }
        // Claim the whole operation before opening a modal window or decoding a preview.
        // A session task also makes Quit wait for editor loading/cleanup, not just export.
        var ownedWork = workCancellation = new CancellationTokenSource();
        var token = ownedWork.Token;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        work = finished.Task;
        workOwner = owner;
        Home.UpdateBusy();
        NotifyWorkStateChanged();
        EventHandler ownerClosed = (_,_) => ownedWork.Cancel();
        owner.Closed += ownerClosed;
        try
        {
            if(action.Id=="assistant.choose")
            {
                if(!Prefs.IsOn(Feature.Assistant)||!Prefs.IsOn(Feature.QuickActions))throw new UserError("Enable Assistant and Quick Actions in Settings first.");workFeature=Feature.Assistant;
                if(owner is not ActionWindow panel||panel.IsWheel)throw new UserError("Ask the assistant from Quick Actions.");
                var settings=Prefs.Assistant??throw new UserError("Configure the assistant service in Assistant Settings first.");files=Validate(files);
                var offered=AssistantPlan.Offered(Prefs,files);var custom=AssistantPlan.Offered(Prefs,files,true);if(custom.Length==0)throw new UserError("No built-in actions are available for these files. Choose files or enable another tool first.");
                string preferences=Prefs.Serialize();var dialog=new AssistantDialog(this,settings,offered,files,assistantRequest??"",token,custom);
                try{ShowOperationDialog(dialog,owner,token);}finally{await dialog.FinishAsync();}token.ThrowIfCancellationRequested();
                if(dialog.SelectedId is not null){if(Prefs.Serialize()!=preferences){report(L("Settings changed while asking. Review a new request before selecting an action."));return;}panel.ApplyAssistantSuggestion(dialog.SelectedId,files,dialog.IncludeCustomSelected);}return;
            }
            if (action.Id is "presets.import" or "presets.export")
            {
                if (!Prefs.IsOn(Feature.ImagePresets)) throw new UserError("Enable Image Presets in Settings first.");
                workFeature = Feature.ImagePresets;
                if (action.Id == "presets.import")
                {
                    var picker = new Microsoft.Win32.OpenFileDialog { Filter = L("Image preset libraries (*.json)|*.json"), Multiselect = false, CheckFileExists = true };
                    if (picker.ShowDialog(owner) != true) return;
                    token.ThrowIfCancellationRequested(); string path = picker.FileName;
                    using var library = await Task.Run(() => PresetLibraryFile.Open(path, token), token);
                    var compatible = library.Import.Compatible;
                    int additions = 0; string? mergeIssue = null;
                    try { additions = PresetLibrary.Merge(Prefs.Presets, compatible).Length - Prefs.Presets.Count; }
                    catch (UserError error) { mergeIssue = error.Message; }
                    if (!ShowOperationDialog(new PresetLibraryPreview(this, library.Import, true, additions, mergeIssue), owner, token)) return;
                    token.ThrowIfCancellationRequested(); Prefs.ImportPresets(compatible);
                    report(L("Image presets imported. Incompatible entries were not added.")); return;
                }
                var snapshot = Prefs.Presets.ToArray();
                var preview = new PresetImport(snapshot.Select(x => new PresetImportRow(x.Name, PresetLibrary.Describe(x.Recipe), x)).ToArray(), false);
                if (!ShowOperationDialog(new PresetLibraryPreview(this, preview, false), owner, token)) return;
                var folder = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Backup Folder…") };
                if (folder.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested(); string destination = folder.FolderName;
                string output = await Task.Run(() => PresetLibraryFile.Save(snapshot, destination, token), token);
                string presetStatus = L("Image preset backup saved."); report(presetStatus); Home.ShowResults(new[] { output }, presetStatus); return;
            }
            if (action.Id == "qr.exportMacImages")
            {
                if (!Prefs.IsOn(Feature.QrCodes)) throw new UserError("Enable QR & Barcodes in Settings first.");
                workFeature = Feature.QrCodes;
                token.ThrowIfCancellationRequested();
                var plan = QrMacExport.Prepare(Prefs.QrCodes.ToArray());
                if (!ShowOperationDialog(new QrMacExportDialog(this, plan), owner, token)) return;
                token.ThrowIfCancellationRequested();
                var folder = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose QR Export Folder…") };
                if (folder.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested(); string destination = folder.FolderName;
                var entries = await OnSta(() => QrMacExportFiles.Save(plan, destination, token,
                    (index, total) => Dispatcher.Invoke(() => report(string.Format(L("Processing file {0} of {1}…"), index, total)))));
                Home.ShowBatchResults(entries);
                var outputs = entries.Where(x => x.State == BatchState.Saved).Select(x => x.Output!).ToArray();
                string summary = string.Format(L("Saved: {0}. Failed: {1}. Cancelled: {2}. Not started: {3}."),
                    outputs.Length, entries.Count(x => x.State == BatchState.Failed),
                    entries.Count(x => x.State == BatchState.Cancelled), entries.Count(x => x.State == BatchState.NotStarted));
                report(summary); Home.ShowResults(outputs, summary);
                var error = entries.FirstOrDefault(x => x.State == BatchState.Failed)?.Error;
                if (error is not null && !token.IsCancellationRequested && !exiting) ShowError(error);
                return;
            }
            if (action.Id == "qr.importMac")
            {
                if (!Prefs.IsOn(Feature.QrCodes)) throw new UserError("Enable QR & Barcodes in Settings first.");
                workFeature = Feature.QrCodes;
                var picker = new Microsoft.Win32.OpenFileDialog { Filter = L("QR libraries (*.json)|*.json"), Multiselect = false, CheckFileExists = true };
                if (picker.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested(); string path = picker.FileName;
                using var library = await Task.Run(() => MacQrLibraryFile.Open(path, token), token);
                var compatible = library.Import.Compatible; int additions = 0; string? mergeIssue = null;
                try { additions = QrLibrary.Merge(Prefs.QrCodes, compatible).Length - Prefs.QrCodes.Count; }
                catch (UserError error) { mergeIssue = error.Message; }
                if (!ShowOperationDialog(new MacQrLibraryPreview(this, library.Import, additions, mergeIssue), owner, token)) return;
                token.ThrowIfCancellationRequested(); Prefs.ImportQr(compatible);
                report(L("Mac QR entries imported. Incompatible entries were not added.")); return;
            }
            if (action.Id is "qr.import" or "qr.export")
            {
                if (!Prefs.IsOn(Feature.QrCodes)) throw new UserError("Enable QR & Barcodes in Settings first.");workFeature=Feature.QrCodes;
                if(action.Id=="qr.import")
                {
                    var picker=new Microsoft.Win32.OpenFileDialog { Filter=L("QR libraries (*.json)|*.json"),Multiselect=false,CheckFileExists=true };
                    if(picker.ShowDialog(owner)!=true)return;token.ThrowIfCancellationRequested();
                    string selectedLibrary=picker.FileName;using var library=await Task.Run(()=>QrLibraryFile.Open(selectedLibrary,token),token);
                    int additions=QrLibrary.Merge(Prefs.QrCodes,library.Entries).Length-Prefs.QrCodes.Count;
                    if(!ShowOperationDialog(new QrLibraryPreview(this,library.Entries,true,additions),owner,token))return;
                    token.ThrowIfCancellationRequested();Prefs.ImportQr(library.Entries);report(L("QR library imported."));return;
                }
                var snapshot=Prefs.QrCodes.ToArray();
                if(!ShowOperationDialog(new QrLibraryPreview(this,snapshot,false),owner,token))return;
                var folder=new Microsoft.Win32.OpenFolderDialog {Title=L("Choose Backup Folder…")};
                if(folder.ShowDialog(owner)!=true)return;token.ThrowIfCancellationRequested();
                string destination=folder.FolderName;string output=await Task.Run(()=>QrLibraryFile.Save(snapshot,destination,token),token);
                string backupStatus=L("QR library backup saved.");report(backupStatus);Home.ShowResults(new[]{output},backupStatus);return;
            }
            if(action.Id is "qr.readflat" or "qr.recoverflat")
            {
                if(!Prefs.IsOn(Feature.QrCodes))throw new UserError("Enable QR & Barcodes in Settings first.");
                workFeature=Feature.QrCodes;token.ThrowIfCancellationRequested();
                var picker=new Microsoft.Win32.OpenFileDialog{Title=L("Choose a Flat QR Image…"),Filter=L("QR images (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif"),CheckFileExists=true,Multiselect=false};
                if(picker.ShowDialog(owner)!=true)return;token.ThrowIfCancellationRequested();string path=picker.FileName;
                bool recover=action.Id=="qr.recoverflat";
                report(L(recover?"Recovering flat QR text…":"Reading QR image…"));
                using var document=await AwaitDoctorReadAsync(OnSta(()=>recover?QrReadDocument.OpenForRecovery(path,token):QrReadDocument.Open(path,token)),token);
                token.ThrowIfCancellationRequested();ShowOperationDialog(new QrReadDialog(this,document),owner,token);
                report(token.IsCancellationRequested?L("Cancelled."):L("QR reading closed. No text was opened or sent."));return;
            }
            if (action.Id == "qr.create")
            {
                if (!Prefs.IsOn(Feature.QrCodes)) throw new UserError("Enable QR & Barcodes in Settings first.");
                qrEntry?.Validate(); workFeature = Feature.QrCodes;
                var dialog = new QrDialog(this,qrEntry);
                bool save;try{save=ShowOperationDialog(dialog,owner,token);}finally{await dialog.FinishAsync();}
                token.ThrowIfCancellationRequested();if(!save)return;
                string text = dialog.Payload, directory = dialog.OutputDirectory; QrCorrection correction=dialog.Correction;
                report(L("Processing…"));
                string output = await OnSta(() => QrTools.Save(text,correction,directory,token));
                string qrStatus = L("QR image saved in the selected folder."); report(qrStatus); Home.ShowResults(new[] { output },qrStatus); return;
            }
            if(action.Id=="doctor.startupfolders")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading reported startup-folder locations…"));
                var startupChoices=await AwaitDoctorReadAsync(OnSta(()=>StartupFolderReader.ReadLocations(token)),token);token.ThrowIfCancellationRequested();
                var chooser=new StartupFolderChooser(this,startupChoices);if(!ShowOperationDialog(chooser,owner,token))return;
                token.ThrowIfCancellationRequested();var selected=chooser.Selected;if(selected.Length is <1 or >2)throw new UserError("Choose an available startup folder first.");
                report(L("Reading startup-folder entry metadata…"));var startupFolders=await AwaitDoctorReadAsync(Task.Run(()=>StartupFolderReader.ReadEntries(selected,token),token),token);
                token.ThrowIfCancellationRequested();ShowOperationDialog(new StartupFolderDialog(this,startupFolders),owner,token);
                report(token.IsCancellationRequested?L("Cancelled."):L("Startup-folder inspection closed. Nothing was changed."));
                return;
            }
            if(action.Id=="doctor.startupapproved")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading bounded StartupApproved registry data…"));
                var approval=await AwaitDoctorReadAsync(Task.Run(()=>StartupApprovalReader.Read(token),token),token);token.ThrowIfCancellationRequested();
                ShowOperationDialog(new StartupApprovalDialog(this,approval),owner,token);
                report(token.IsCancellationRequested?L("Cancelled."):L("StartupApproved snapshot closed. No registry values were changed."));return;
            }
            if(action.Id=="doctor.closewindow")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading eligible desktop windows…"));
                using var session=await AwaitDoctorReadAsync(Task.Run(()=>CloseWindowsReader.Read(token),token),token);
                token.ThrowIfCancellationRequested();var dialog=new CloseWindowsDialog(this,session,token);
                try { ShowOperationDialog(dialog,owner,token); }
                finally { await dialog.FinishAsync(); }
                report(L("Window review closed. Any close request already sent may still be handled by its app."));return;
            }
            if(action.Id is "doctor.commontextsimilar" or "doctor.commonphotosimilar")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading reported common-folder locations…"));
                var choices=await AwaitDoctorReadAsync(OnSta(()=>CommonFolderReader.Read(token)),token);token.ThrowIfCancellationRequested();
                var kind=action.Id=="doctor.commontextsimilar"?CommonSimilarityKind.Text:CommonSimilarityKind.Images;
                var chooser=new CommonSimilarityChooser(this,choices,kind);if(!ShowOperationDialog(chooser,owner,token))return;
                token.ThrowIfCancellationRequested();var selected=chooser.Selected;if(selected.Length is <1 or >6)throw new UserError("Choose an available common folder first.");
                if(!ShowOperationDialog(new CommonSimilarityConfirmation(this,selected,kind),owner,token))return;
                token.ThrowIfCancellationRequested();report(L("Comparing selected common folders…"));
                var similar=await AwaitDoctorReadAsync(OnSta(()=>CommonSimilarityReader.Read(kind,selected,token)),token);
                token.ThrowIfCancellationRequested();var similarDialog=new CommonSimilarityDialog(this,similar);ShowOperationDialog(similarDialog,owner,token);
                report(similarDialog.Moved>0?string.Format(L("Similar-file comparison finished. Files moved to the Recycle Bin or removed: {0}"),similarDialog.Moved):token.IsCancellationRequested?L("Cancelled."):L("Common-folder comparison closed. No files were changed."));return;
            }
            if(action.Id=="doctor.commonduplicates")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading reported common-folder locations…"));
                var choices=await AwaitDoctorReadAsync(OnSta(()=>CommonFolderReader.Read(token)),token);token.ThrowIfCancellationRequested();
                var chooser=new CommonFolderChooser(this,choices,CommonFolderChooserMode.Compare);if(!ShowOperationDialog(chooser,owner,token))return;
                token.ThrowIfCancellationRequested();var selected=chooser.Selected;if(selected.Length is <1 or >6)throw new UserError("Choose an available common folder first.");
                if(!ShowOperationDialog(new CommonDuplicateConfirmation(this,selected),owner,token))return;token.ThrowIfCancellationRequested();
                report(L("Comparing selected common folders…"));var commonDuplicateResult=await AwaitDoctorReadAsync(Task.Run(()=>CommonDuplicateReader.Read(selected,token),token),token);
                token.ThrowIfCancellationRequested();var commonDuplicateDialog=new CommonDuplicatesDialog(this,commonDuplicateResult);ShowOperationDialog(commonDuplicateDialog,owner,token);
                report(commonDuplicateDialog.Moved>0?string.Format(L("Duplicate comparison finished. Files moved to the Recycle Bin or removed: {0}"),commonDuplicateDialog.Moved):token.IsCancellationRequested?L("Cancelled."):L("Common-folder comparison closed. No files were changed."));return;
            }
            if(action.Id=="doctor.commonfolders")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading reported common-folder locations…"));
                var choices=await AwaitDoctorReadAsync(OnSta(()=>CommonFolderReader.Read(token)),token);token.ThrowIfCancellationRequested();
                var chooser=new CommonFolderChooser(this,choices);if(!ShowOperationDialog(chooser,owner,token))return;token.ThrowIfCancellationRequested();var selected=chooser.Selected;
                if(selected.Length is <1 or >6)throw new UserError("Choose an available common folder first.");
                var results=new List<CommonFolderResult>();var seen=new HashSet<string>(StringComparer.Ordinal);
                foreach(var choice in selected)
                {
                    token.ThrowIfCancellationRequested();
                    if(!seen.Add(choice.Path!)){results.Add(new(choice,null,"Same reported path as an earlier selected folder; not measured again."));continue;}
                    report(string.Format(L("Measuring common folder {0} of {1}: {2}"),results.Count+1,selected.Length,L(CommonFolders.Name(choice.Kind))));
                    try
                    {
                        var scan=await AwaitDoctorReadAsync(Task.Run(()=>CommonFolderReader.Measure(choice,token),token),token);token.ThrowIfCancellationRequested();results.Add(new(choice,scan));
                    }
                    catch(Exception error)when(!token.IsCancellationRequested&&(error is UserError or System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or System.ComponentModel.Win32Exception))
                    {
                        results.Add(new(choice,null,error is UserError?error.Message:"This reported folder could not be measured.",error is System.ComponentModel.Win32Exception native?unchecked((uint)native.NativeErrorCode):null));
                    }
                }
                token.ThrowIfCancellationRequested();ShowOperationDialog(new CommonFolderResultsDialog(this,results,token),owner,token);
                report(token.IsCancellationRequested?L("Cancelled."):L("Common-folder inspection closed. No files were changed."));return;
            }
            if(action.Id=="doctor.scheduledtasks")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading local scheduled-task metadata…"));
                var snapshot=await AwaitDoctorReadAsync(ScheduledTaskReader.ReadAsync(token),token);token.ThrowIfCancellationRequested();
                ShowOperationDialog(new ScheduledTaskDialog(this,snapshot),owner,token);
                report(token.IsCancellationRequested?L("Cancelled."):L("Scheduled-task inspection closed. No task was changed or run."));return;
            }
            if(action.Id=="doctor.services")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading local Windows service status…"));
                var snapshot=await AwaitDoctorReadAsync(Task.Run(()=>ServiceReader.Read(token),token),token);token.ThrowIfCancellationRequested();
                ShowOperationDialog(new ServiceDialog(this,snapshot),owner,token);
                report(token.IsCancellationRequested?L("Cancelled."):L("Service inspection closed. No service was changed."));return;
            }
            if(action.Id=="doctor.sleepstates")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading reported sleep capabilities…"));
                var snapshot=await AwaitDoctorReadAsync(Task.Run(()=>SleepCapabilityReader.Read(token),token),token);token.ThrowIfCancellationRequested();
                ShowOperationDialog(new SleepCapabilityDialog(this,snapshot),owner,token);
                report(token.IsCancellationRequested?L("Cancelled."):L("Sleep-capability inspection closed. No power state or setting was changed."));return;
            }
            if(action.Id=="doctor.caches")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();
                var choices=await AwaitDoctorReadAsync(Task.Run(()=>CacheLocationReader.Read(token),token),token);token.ThrowIfCancellationRequested();
                var preview=new CacheLocationDialog(this,choices);if(!ShowOperationDialog(preview,owner,token))return;
                token.ThrowIfCancellationRequested();var selected=preview.Selected??throw new UserError("Choose an available cache location first.");
                report(L("Reading selected cache-location sizes…"));var scan=await AwaitDoctorReadAsync(Task.Run(()=>CacheLocationReader.Scan(selected,token),token),token);token.ThrowIfCancellationRequested();
                if(selected.Kind==CacheLocationKind.Temporary)
                {
                    var temp=new TempCleanupDialog(this,selected.Path!,scan,DateTime.UtcNow);ShowOperationDialog(temp,owner,token);
                    report(temp.Moved>0?string.Format(L("Temporary-folder review finished. Items moved to the Recycle Bin or removed: {0}"),temp.Moved):token.IsCancellationRequested?L("Cancelled."):L("Cache-location inspection closed. No files were removed."));return;
                }
                ShowOperationDialog(new FolderMapDialog(this,selected.Path!,scan,CacheLocations.ResultContext,CacheLocations.Name(selected.Kind)),owner,token);
                report(token.IsCancellationRequested?L("Cancelled."):L("Cache-location inspection closed. No files were removed."));return;
            }
            if(action.Id=="doctor.appcaches")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Looking for browser and app cache folders…"));
                var found=await AwaitDoctorReadAsync(Task.Run(()=>AppCacheReader.Read(token),token),token);token.ThrowIfCancellationRequested();
                var caches=new AppCacheDialog(this,found,AppCacheReader.RunningNames);ShowOperationDialog(caches,owner,token);
                report(caches.Moved>0?string.Format(L("Cache review finished. Folders moved to the Recycle Bin or removed: {0}"),caches.Moved):token.IsCancellationRequested?L("Cancelled."):L("Cache review closed. No cache folder from this window is in the Recycle Bin now."));return;
            }
            if(action.Id=="doctor.cleanup")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Looking for things to clean…"));
                var found=await AwaitDoctorReadAsync(OnSta(()=>CleanupReader.Find(group=>Dispatcher.BeginInvoke(()=>report(string.Format(L("Looking for things to clean: {0}…"),L(CleanupFiles.Name(group))))),token)),token);
                token.ThrowIfCancellationRequested();
                var cleanup=new CleanupFilesDialog(this,found);ShowOperationDialog(cleanup,owner,token);
                report(cleanup.Moved>0?string.Format(L("Clean Up Files finished. Items moved to the Recycle Bin or removed: {0}"),cleanup.Moved):token.IsCancellationRequested?L("Cancelled."):L("Clean Up Files closed. Nothing from this window is in the Recycle Bin now."));return;
            }
            if(action.Id=="doctor.power")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading power and sleep settings…"));
                var snapshot=await AwaitDoctorReadAsync(Task.Run(()=>PowerPlanReader.Read(token),token),token);token.ThrowIfCancellationRequested();
                ShowOperationDialog(new PowerPlanDialog(this,snapshot),owner,token);
                report(token.IsCancellationRequested?L("Cancelled."):L("Power inspection closed. No power settings were changed."));return;
            }
            if(action.Id=="doctor.drives")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading mounted-drive metadata…"));
                var snapshot=await AwaitDoctorReadAsync(MountedDriveReader.ReadAsync(token),token);token.ThrowIfCancellationRequested();
                ShowOperationDialog(new MountedDriveDialog(this,snapshot),owner,token);
                report(token.IsCancellationRequested?L("Cancelled."):L("Drive inspection closed. No drive was changed or ejected."));return;
            }
            if(action.Id=="doctor.executable")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();
                var picker=new Microsoft.Win32.OpenFileDialog{Title=L("Choose an EXE to Inspect…"),Filter=L("PE executable (*.exe)|*.exe"),CheckFileExists=true,Multiselect=false};
                if(picker.ShowDialog(owner)!=true)return;token.ThrowIfCancellationRequested();string path=picker.FileName;
                report(L("Reading EXE headers…"));using var file=await AwaitDoctorReadAsync(Task.Run(()=>ExecutableStructureFile.Open(path,token),token),token);
                token.ThrowIfCancellationRequested();ShowOperationDialog(new ExecutableStructureDialog(this,file),owner,token);
                report(token.IsCancellationRequested?L("Cancelled."):L("EXE inspection closed. No file was run or changed."));return;
            }
            if(action.Id=="doctor.startup")
            {
                if(!Prefs.IsOn(Feature.Doctor))throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature=Feature.Doctor;token.ThrowIfCancellationRequested();report(L("Reading logon startup records…"));
                var snapshot=await AwaitDoctorReadAsync(Task.Run(()=>StartupReader.Read(token),token),token);
                token.ThrowIfCancellationRequested();await ShowDoctorOperationAsync(new StartupDialog(this,snapshot),DoctorAssistantSummary.Startup(snapshot),owner,token,DoctorLabels.Startup(snapshot));
                report(token.IsCancellationRequested?L("Cancelled."):L("Read-only startup inspection finished. No commands or settings were changed."));return;
            }
            if (action.Id == "doctor.installedapps")
            {
                if (!Prefs.IsOn(Feature.Doctor)) throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature = Feature.Doctor; token.ThrowIfCancellationRequested();
                report(L("Reading desktop installation records…"));
                var installed = await AwaitDoctorReadAsync(Task.Run(() => InstalledAppsReader.Read(token), token),token);
                token.ThrowIfCancellationRequested();
                // The list read first stays the comparison: after a failed or empty search, or Back
                // from the review, the same records window opens again.
                int moved = 0; bool searched = false;
                while (true)
                {
                    var records = new InstalledAppsDialog(this, installed);
                    await ShowDoctorOperationAsync(records,DoctorAssistantSummary.Installed(installed),owner,token,DoctorLabels.Installed(installed));
                    if (!records.LeftoversRequested || token.IsCancellationRequested) break;
                    report(L("Reading installation records again and looking for leftover folders…"));
                    AppLeftoverResult leftovers;
                    try { leftovers = await AwaitDoctorReadAsync(Task.Run(() => AppLeftoverReader.Read(installed, token), token),token); }
                    catch (UserError error) when (!token.IsCancellationRequested)
                    { report(L("Nothing was searched. The installation records read earlier are kept.")); if (!exiting) ShowError(error); continue; }
                    token.ThrowIfCancellationRequested(); searched = true;
                    var review = new AppLeftoversDialog(this, leftovers, AppLeftoverReader.ProgramPaths, AppLeftoverReader.RecordsCheck);
                    bool back = ShowOperationDialog(review,owner,token); moved += review.Moved;
                    report(moved > 0 ? string.Format(L("Leftover review finished. Folders moved to the Recycle Bin or removed: {0}"), moved) : L("Leftover review closed. No folder from this window is in the Recycle Bin now."));
                    if (!back) break;
                }
                report(token.IsCancellationRequested ? L("Cancelled.") : moved > 0 ? string.Format(L("Leftover review finished. Folders moved to the Recycle Bin or removed: {0}"), moved)
                    : searched ? L("Leftover review closed. No folder from this window is in the Recycle Bin now.") : L("Read-only installation-record inspection finished. No apps or files were changed.")); return;
            }
            if (action.Id == "doctor.appmemory")
            {
                if (!Prefs.IsOn(Feature.Doctor)) throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature = Feature.Doctor; token.ThrowIfCancellationRequested();
                report(L("Reading process memory metadata…"));
                var snapshot = await AwaitDoctorReadAsync(Task.Run(() => AppMemoryReader.Read(token),token),token);
                token.ThrowIfCancellationRequested();
                await ShowDoctorOperationAsync(new AppMemoryDialog(this,snapshot),DoctorAssistantSummary.Memory(snapshot),owner,token,DoctorLabels.Memory(snapshot));
                report(token.IsCancellationRequested ? L("Cancelled.") : L("Read-only app memory inspection finished. No processes were changed.")); return;
            }
            if (action.Id == "doctor.monitor")
            {
                if (!Prefs.IsOn(Feature.Doctor)) throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature = Feature.Doctor; token.ThrowIfCancellationRequested();
                var dialog = new UsageMonitorDialog(this,token);
                try { ShowOperationDialog(dialog,owner,token); }
                finally { await dialog.StopAsync(); }
                report(token.IsCancellationRequested ? L("Cancelled.") : L("Monitoring stopped.")); return;
            }
            if (action.Id == "doctor.largefiles")
            {
                if (!Prefs.IsOn(Feature.Doctor)) throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature = Feature.Doctor; token.ThrowIfCancellationRequested();
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Folder to Inspect…"), Multiselect = false };
                if (picker.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested(); string directory = picker.FolderName;
                report(L("Reading file size metadata…"));
                var largeResult = await AwaitDoctorReadAsync(Task.Run(() => DoctorLargeFiles.Run(directory, token), token),token);
                token.ThrowIfCancellationRequested();
                var largeDialog=new LargeFilesDialog(this,directory,largeResult.Scan,largeResult.Files);
                await ShowDoctorOperationAsync(largeDialog,DoctorAssistantSummary.Large(largeResult.Scan,largeResult.Files),owner,token);
                report(largeDialog.Moved>0?string.Format(L("File size inspection finished. Files moved to the Recycle Bin or removed: {0}"),largeDialog.Moved):token.IsCancellationRequested ? L("Cancelled.") : L("Read-only file size inspection finished. No files were changed.")); return;
            }
            if (action.Id == "doctor.projects")
            {
                if (!Prefs.IsOn(Feature.Doctor)) throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature = Feature.Doctor; token.ThrowIfCancellationRequested();
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Folder to Inspect…"), Multiselect = false };
                if (picker.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested(); string directory = picker.FolderName;
                report(L("Reading project folder metadata…"));
                var projectResult = await AwaitDoctorReadAsync(Task.Run(() => DoctorProjects.Run(directory, token), token),token);
                token.ThrowIfCancellationRequested();
                var projectDialog=new ProjectFoldersDialog(this,directory,projectResult.Scan,projectResult.Projects);
                await ShowDoctorOperationAsync(projectDialog,DoctorAssistantSummary.Projects(projectResult.Scan,projectResult.Projects),owner,token);
                report(projectDialog.Moved>0?string.Format(L("Project folder inspection finished. Folders moved to the Recycle Bin or removed: {0}"),projectDialog.Moved):token.IsCancellationRequested?L("Cancelled."):L("Read-only project folder inspection finished. No files were changed.")); return;
            }
            if (action.Id == "doctor.photosimilar")
            {
                if (!Prefs.IsOn(Feature.Doctor)) throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature = Feature.Doctor; token.ThrowIfCancellationRequested();
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Folder to Compare…"), Multiselect = false };
                if (picker.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested(); string directory = picker.FolderName;
                if (!ShowOperationDialog(new PhotoSimilarityConfirmation(this, directory), owner, token)) return;
                token.ThrowIfCancellationRequested(); report(L("Reading images for similarity…"));
                var similar = await AwaitDoctorReadAsync(OnSta(() => DoctorPhotoSimilarity.Run(directory, token)),token);
                token.ThrowIfCancellationRequested();
                var similarDialog=new PhotoSimilarityDialog(this,directory,similar.Folder,similar.Similar);
                await ShowDoctorOperationAsync(similarDialog,DoctorAssistantSummary.Photos(similar.Folder,similar.Similar),owner,token);
                report(similarDialog.Moved>0?string.Format(L("Similar-file comparison finished. Files moved to the Recycle Bin or removed: {0}"),similarDialog.Moved):token.IsCancellationRequested ? L("Cancelled.") : L("Read-only image comparison finished. No files were changed.")); return;
            }
            if (action.Id == "doctor.textsimilar")
            {
                if (!Prefs.IsOn(Feature.Doctor)) throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature = Feature.Doctor; token.ThrowIfCancellationRequested();
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Folder to Compare…"), Multiselect = false };
                if (picker.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested(); string directory = picker.FolderName;
                if (!ShowOperationDialog(new TextSimilarityConfirmation(this, directory), owner, token)) return;
                token.ThrowIfCancellationRequested(); report(L("Reading files for text similarity…"));
                var similar = await AwaitDoctorReadAsync(Task.Run(() => DoctorTextSimilarity.Run(directory, token), token),token);
                token.ThrowIfCancellationRequested();
                var similarDialog=new TextSimilarityDialog(this,directory,similar.Folder,similar.Similar);
                await ShowDoctorOperationAsync(similarDialog,DoctorAssistantSummary.Text(similar.Folder,similar.Similar),owner,token);
                report(similarDialog.Moved>0?string.Format(L("Similar-file comparison finished. Files moved to the Recycle Bin or removed: {0}"),similarDialog.Moved):token.IsCancellationRequested ? L("Cancelled.") : L("Read-only text comparison finished. No files were changed.")); return;
            }
            if (action.Id == "doctor.duplicates")
            {
                if (!Prefs.IsOn(Feature.Doctor)) throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature = Feature.Doctor; token.ThrowIfCancellationRequested();
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Folder to Compare…"), Multiselect = false };
                if (picker.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested(); string directory = picker.FolderName;
                if (!ShowOperationDialog(new DuplicateScanConfirmation(this, directory), owner, token)) return;
                token.ThrowIfCancellationRequested(); report(L("Reading files for duplicate comparison…"));
                var duplicateResult = await AwaitDoctorReadAsync(Task.Run(() => DoctorDuplicates.Run(directory, token), token),token);
                token.ThrowIfCancellationRequested();
                var duplicateDialog=new DuplicateFilesDialog(this,directory,duplicateResult.Folder,duplicateResult.Duplicates);
                await ShowDoctorOperationAsync(duplicateDialog,DoctorAssistantSummary.Duplicates(duplicateResult.Folder,duplicateResult.Duplicates),owner,token);
                report(duplicateDialog.Moved>0?string.Format(L("Duplicate comparison finished. Files moved to the Recycle Bin or removed: {0}"),duplicateDialog.Moved):token.IsCancellationRequested?L("Cancelled."):L("Read-only duplicate comparison finished. No files were changed.")); return;
            }
            if (action.Id == "doctor.folder")
            {
                if (!Prefs.IsOn(Feature.Doctor)) throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature = Feature.Doctor; token.ThrowIfCancellationRequested();
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Folder to Measure…"), Multiselect = false };
                if (picker.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested(); string directory = picker.FolderName; report(L("Reading folder sizes…"));
                var sizes = await AwaitDoctorReadAsync(Task.Run(() => { using var source = NativeFiles.MetadataDirectory.Open(directory); return FolderSizes.Scan(source, token); }, token),token);
                token.ThrowIfCancellationRequested();
                await ShowDoctorOperationAsync(new FolderMapDialog(this,directory,sizes),DoctorAssistantSummary.Folder(sizes),owner,token);
                report(L("Read-only folder measurement finished. No files were changed.")); return;
            }
            if (action.Id == "doctor.check")
            {
                if (!Prefs.IsOn(Feature.Doctor)) throw new UserError("Enable Windows Doctor in Settings first.");
                workFeature = Feature.Doctor; report(L("Running read-only checks…"));
                var snapshot = await AwaitDoctorReadAsync(Task.Run(() => PCDoctor.RunDetailed(token), token),token);
                token.ThrowIfCancellationRequested();var dialog=new DoctorDialog(this,snapshot.Facts,token,snapshot.DriveSpace);try{ShowOperationDialog(dialog,owner,token);}finally{await dialog.FinishAsync();}
                report(token.IsCancellationRequested ? L("Cancelled.") : L("Read-only checks finished. Swivel did not change files or settings.")); return;
            }
            if (action.Id == "folder.style")
            {
                if (!Prefs.IsOn(Feature.FolderStyle)) throw new UserError("Enable Folder Style in Settings first.");
                workFeature = Feature.FolderStyle; token.ThrowIfCancellationRequested();
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Folders to Style…"), Multiselect = true };
                if (picker.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested();
                string[] folders = picker.FolderNames;
                if (folders.Length is < 1 or > FolderStyles.MaxFolders) throw new UserError("Choose 1–20 folders.");
                if (folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != folders.Length) throw new UserError("Choose each folder only once.");
                report(L("Reading folder settings…"));
                // One unreadable folder is shown as unavailable instead of stopping the whole choice.
                var states = await Task.Run(() => folders.Select(f =>
                {
                    token.ThrowIfCancellationRequested();
                    try { return (f, NativeFiles.InspectFolderStyle(f), (string?)null); }
                    catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                    { return (f, FolderStyleState.Unavailable, error is UserError ? L(error.Message) : L("The operation could not finish. Check the file format, permissions, free space and whether another app is using the file.")); }
                }).ToArray(), token);
                token.ThrowIfCancellationRequested();
                var dialog = new FolderStyleDialog(this, states);
                if (!ShowOperationDialog(dialog, owner, token)) return;
                var style = dialog.Chosen; bool restore = dialog.Restore;
                byte[]? icon = restore ? null : FolderStyleIcons.Icon(style);
                report(L("Processing…"));
                var targets = states.Where(s => restore ? s.Item2 == FolderStyleState.Swivel : s.Item2 is FolderStyleState.None or FolderStyleState.Swivel).Select(s => s.f).ToArray();
                var failures = new List<string>(); int changed = 0;
                foreach (string folder in targets)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        await Task.Run(() => { if (restore) NativeFiles.RemoveFolderStyle(folder, token); else NativeFiles.ApplyFolderStyle(folder, icon!, token); }, token);
                        changed++;
                    }
                    catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException) { failures.Add(folder + ": " + (error is UserError ? L(error.Message) : L("The operation could not finish. Check the file format, permissions, free space and whether another app is using the file."))); }
                }
                string summary = string.Format(L(restore ? "Restored Explorer's default look for {0} of {1} folder(s)." : "Styled {0} of {1} folder(s)."), changed, targets.Length);
                int skipped = states.Length - targets.Length;
                if (skipped > 0) summary += " " + string.Format(L("{0} folder(s) were left unchanged."), skipped);
                report(summary); Home.ShowResults(folders, summary);
                if (failures.Count > 0 && !exiting) ShowError(new UserError(string.Join("\n", failures)));
                return;
            }
            if (action.Id == "archive.zipfolder")
            {
                if (!Prefs.IsOn(Feature.Archives)) throw new UserError("Enable Archives in Settings first.");
                workFeature = Feature.Archives; token.ThrowIfCancellationRequested();
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose a Folder to Compress…") };
                if (picker.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested();
                string folder = picker.FolderName;
                report(L("Reading folder…"));
                using var work = await Task.Run(() => FolderZipWork.Open(folder, token), token);
                token.ThrowIfCancellationRequested();
                if (!ShowOperationDialog(new FolderPlanDialog(this, "Compress Folder to ZIP…", work.Plan.Name + ".zip", new[] { work.Folder, L("The ZIP is saved beside the folder under the folder's name, or the next free numbered name. The folder stays unchanged.") }, work.Plan, FolderZip.Notice, "Create ZIP"), owner, token)) return;
                report(L("Processing…"));
                string zip = await Task.Run(() => work.Save(token, (done, total) =>
                    Dispatcher.InvokeAsync(() => report(string.Format(L("Compressing file {0} of {1}…"), done, total)))), token);
                string summary = string.Format(L("Compressed {0} file(s) into a ZIP beside the folder."), work.Plan.Files);
                if (work.Plan.Skipped.Count > 0) summary += " " + string.Format(L("{0} item(s) were skipped."), work.Plan.Skipped.Count);
                report(summary); Home.ShowResults(new[] { zip }, summary);
                return;
            }
            if (action.Id == "file.copyfolder")
            {
                if (!Prefs.IsOn(Feature.FileMoves)) throw new UserError("Enable Move Files in Settings first.");
                workFeature = Feature.FileMoves; token.ThrowIfCancellationRequested();
                var from = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose a Folder to Copy…") };
                if (from.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested();
                var to = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Where to Copy It…") };
                if (to.ShowDialog(owner) != true) return;
                token.ThrowIfCancellationRequested();
                string folder = from.FolderName, destination = to.FolderName;
                report(L("Reading folder…"));
                using var work = await Task.Run(() => FolderCopyWork.Open(folder, destination, token), token);
                token.ThrowIfCancellationRequested();
                if (!ShowOperationDialog(new FolderPlanDialog(this, "Copy Folder to…", work.Plan.Name, new[] { string.Format(L("From: {0}"), work.Folder), string.Format(L("Into: {0}"), work.Destination) }, work.Plan, FolderCopy.Notice, "Copy Folder"), owner, token)) return;
                report(L("Processing…"));
                var outcome = await Task.Run(() => work.Execute(token, (done, total) =>
                    Dispatcher.InvokeAsync(() => report(string.Format(L("Copying file {0} of {1}…"), done, total)))), token);
                string summary = string.Format(L("Copied {0} of {1} file(s) into a new folder."), outcome.FilesCopied, outcome.FileCount);
                if (work.Plan.Skipped.Count > 0) summary += " " + string.Format(L("{0} item(s) were skipped."), work.Plan.Skipped.Count);
                if (outcome.Cancelled) summary += " " + L("Cancelled. Copies already made were kept.");
                else if (outcome.Error is not null) summary += " " + L("Copying stopped. Copies already made were kept; nothing was overwritten.");
                report(summary);
                if (outcome.Folder is not null) Home.ShowResults(new[] { outcome.Folder }, summary);
                if (outcome.Error is not null && !exiting) ShowError(outcome.Error);
                return;
            }
            if (action.Id == "file.move")
            {
                if (!Prefs.IsOn(Feature.FileMoves)) throw new UserError("Enable Move Files in Settings first.");
                workFeature = Feature.FileMoves;
                // Preserve every explicit input. The common Validate helper case-folds
                // paths; Move must reject ambiguous destinations rather than drop a file.
                files = files.ToArray();
                if (files.Length is < 1 or > MoveRules.MaxFiles) throw new UserError("Choose 1–100 distinct files to move.");
                foreach (string path in files) WindowsPathPolicy.Validate(path);
                await RunMoveAsync(files, owner, report, token, false); return;
            }
            if (action.Id == "file.copyto")
            {
                if (!Prefs.IsOn(Feature.FileMoves)) throw new UserError("Enable Move Files in Settings first.");
                workFeature = Feature.FileMoves;
                files = files.ToArray();
                if (files.Length is < 1 or > CopyRules.MaxFiles) throw new UserError("Choose 1–100 distinct files to copy.");
                foreach (string path in files) WindowsPathPolicy.Validate(path);
                await RunCopyAsync(files, owner, report, token); return;
            }
            if (action.Id == "file.undoMove")
            {
                if (!Prefs.IsOn(Feature.FileMoves)) throw new UserError("Enable Move Files in Settings first.");
                workFeature = Feature.FileMoves;
                await RunMoveAsync(files, owner, report, token, true); return;
            }
            if (action.Id == "clothesline.stitch")
            {
                // files[0] is the print above, files[1] the one dropped below it.
                if (!Prefs.IsOn(Feature.Clothesline)) throw new UserError("Enable Clothesline in Settings first.");
                workFeature = Feature.Clothesline;
                if (files.Length != 2) throw new UserError(ImageStitch.ChooseTwo);
                foreach (string path in files) WindowsPathPolicy.Validate(path);
                report(L("Stitching images…"));
                var (made, thumbnail) = await OnSta(() => {
                    string output = ImageStitcher.Save(files[0], files[1], token);
                    using var tile = ImageTools.OpenTile(output, 248, 206, false, CancellationToken.None);
                    return (output, tile.Bitmap);
                });
                string saved = string.Format(L("Saved {0} beside the upper image. The originals were not changed; hang them again to see them on the line."), System.IO.Path.GetFileName(made));
                // Already published: if the line was cleared or the work cancelled meanwhile, say so but leave the line alone.
                if (token.IsCancellationRequested) { report(saved); return; }
                // The new image takes the upper print's place; both originals stay on disk, off the line.
                int place = Prints.ToList().FindIndex(p => string.Equals(p.Path, files[0], StringComparison.OrdinalIgnoreCase));
                foreach (var used in Prints.Where(p => files.Contains(p.Path, StringComparer.OrdinalIgnoreCase)).ToArray()) Prints.Remove(used);
                Prints.Insert(place < 0 ? Prints.Count : Math.Min(place, Prints.Count), new HungPrint(made, thumbnail));
                report(saved); return;
            }
            if (action.Id == "rename.undo")
            {
                if (!Prefs.IsOn(Feature.BatchRename)) throw new UserError("Enable Batch Rename in Settings first.");
                workFeature = Feature.BatchRename;
                await RunRenameAsync(files, owner, report, token, true); return;
            }
            // Page-only routes must work without local files and must not touch
            // unrelated/stale selections. Bind cancellation before preparing.
            if (action.Id.StartsWith("app.",StringComparison.Ordinal) && Prefs.Apps.FirstOrDefault(x => "app." + x.Id == action.Id) is { IsWebPage: true } pageRoute)
            {
                if (!Prefs.IsOn(Feature.AppRouting)) throw new UserError("Enable App Routing in Settings to use these actions.");
                workFeature = Feature.AppRouting;
                var selected = pageRoute.CopyToClipboard ? Validate(files) : Array.Empty<string>();
                if (!Catalog.Available(Prefs,selected).Any(x => x.Id == action.Id)) throw new UserError("This action is not available for these files.");
                using var page = await OnSta(() => WebLaunch.Prepare(pageRoute,selected,token));
                if (!ShowOperationDialog(new WebLaunchDialog(this,page),owner,token)) return;
                if (pageRoute.CopyToClipboard)
                {
                    await OnSta(() => { page.ValidateFiles(token); return true; }); token.ThrowIfCancellationRequested();
                    var copied = new StringCollection(); copied.AddRange(page.Files.ToArray()); Clipboard.SetFileDropList(copied);
                }
                await OnSta(() => { page.Launch(token); return true; });
                report(L(pageRoute.BrowserExecutable is null
                    ? (pageRoute.CopyToClipboard ? "File references copied and page sent to the default browser. Paste or choose the files yourself." : "Page sent to the default browser.")
                    : (pageRoute.CopyToClipboard ? "File references copied and page sent to the selected browser. Paste or choose the files yourself." : "Page sent to the selected browser."))); return;
            }
            files = Validate(files);
            if (action.SingleFile && files.Length != 1) throw new UserError("Select exactly one file for this action.");
            action = Catalog.Available(Prefs, files).FirstOrDefault(x => x.Id == action.Id)
                ?? throw new UserError("This action is not available for these files.");
            workFeature = action.Feature;
            if (shelfPreview && action.Id is ("image.preview" or "file.preview"))
            {
                if (!Prefs.IsOn(Feature.Shelf)) throw new UserError("Enable File Shelf in Settings first.");
                workFeature = Feature.Shelf;
            }
            if (action.Id == "rename.files") { await RunRenameAsync(files, owner, report, token, false); return; }
            if (action.Id == "shelf.add") { AddShelf(files); report(L("Added to File Shelf.")); return; }
            if (action.Id == "clothesline.add")
            {
                var additions = ClotheslineRules.Additions(Prints.Select(x => x.Path),files);
                report(L("Loading image…"));
                var loaded = await OnSta(() => {
                    var result = new List<HungPrint>();
                    foreach (string path in additions) { token.ThrowIfCancellationRequested(); using var document = ImageTools.OpenTile(path,248,206,false,token); result.Add(new(path,document.Bitmap)); }
                    return result;
                });
                token.ThrowIfCancellationRequested();
                foreach (var print in loaded) Prints.Add(print);
                OpenClothesline(); report(L("Images hung. Original files were not changed.")); return;
            }
            if (action.Id == "file.assistantdraft")
            {
                workFeature = Feature.AppRouting; token.ThrowIfCancellationRequested();
                report(L("Preparing file references…"));
                using var draft = await Task.Run(() => AssistantDraftSession.Open(files, Prefs.Language, token), token);
                token.ThrowIfCancellationRequested();
                ShowOperationDialog(new AssistantDraftDialog(this,draft,token),owner,token);
                report(token.IsCancellationRequested ? L("Cancelled.") : L("Draft handoff finished. Swivel did not send a message.")); return;
            }
            if (action.Id == "file.paths") { Clipboard.SetText(string.Join(Environment.NewLine, files)); report(L("Copied.")); return; }
            if (action.Id == "file.copy")
            {
                var list = new StringCollection(); list.AddRange(files); Clipboard.SetFileDropList(list); report(L("Copied.")); return;
            }
            if (action.Id == "file.reveal") { Explorer.Reveal(files[0]); report(L("Opened the first file in Explorer.")); return; }
            if (action.Id == "file.terminal")
            {
                using var launch = await OnSta(() => TerminalLaunch.Open(files[0],token));
                if (!ShowOperationDialog(new TerminalDialog(this,launch),owner,token)) return;
                await OnSta(() => { launch.Launch(token); return true; }); report(L("Command Prompt opened in the file's folder.")); return;
            }
            if (action.Id == "file.preview")
            {
                if (System.IO.Path.GetExtension(files[0]).Equals(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    report(L("Loading audio…"));
                    using var document = await Task.Run(() => WaveDocument.Open(files[0], token), token);
                    token.ThrowIfCancellationRequested(); var dialog = new WavePreviewDialog(this, document);
                    try { ShowOperationDialog(dialog, owner, token); }
                    finally { await dialog.FinishPreviewAsync(); }
                }
                else
                {
                    report(L("Reading document text…"));
                    using var document = await Task.Run(() => DocumentTextFile.Open(files[0], token), token);
                    token.ThrowIfCancellationRequested(); ShowOperationDialog(new DocumentTextDialog(this, document, previewOnly: true), owner, token);
                }
                report(L("Preview closed.")); return;
            }
            if (action.Id == "image.preview")
            {
                using var document = await OnSta(() => ImageTools.Open(files[0],token));
                ShowOperationDialog(new ImagePreviewDialog(this,document.Bitmap),owner,token);
                report(L("Preview closed.")); return;
            }
            if (action.Id.StartsWith("app.", StringComparison.Ordinal))
            {
                var route = Prefs.Apps.Single(x => "app." + x.Id == action.Id);
                using var launch = await OnSta(() => AppLaunch.Open(route,files,token));
                if (!ShowOperationDialog(new LaunchDialog(this,launch),owner,token)) return;
                if (route.CopyToClipboard)
                {
                    launch.ValidateFiles(token); token.ThrowIfCancellationRequested();
                    var copied = new StringCollection(); copied.AddRange(launch.Files.ToArray()); Clipboard.SetFileDropList(copied);
                }
                await OnSta(() => { launch.Launch(token); return true; });
                report(L(route.CopyToClipboard ? "Files copied. Paste them in the selected application." : "Files passed to the selected application.")); return;
            }
            // Immutable value snapshot: changes to the saved recipe do not alter a running batch.
            var recipe = action.Id.StartsWith("preset.", StringComparison.Ordinal)
                ? Prefs.Presets.Single(x => "preset." + x.Id == action.Id).Recipe : null;
            int edge = 1920, quality = 90; ResizeOptions? resize = null; long? targetBytes = null;
            if (action.Id == "image.resize")
            {
                var dialog = new ResizeDialog(this);
                if (!ShowOperationDialog(dialog, owner, token)) return;
                resize = dialog.Options;
            }
            if (action.Id == "image.compress")
            {
                var dialog = new CompressionDialog(this, files.Length);
                if (!ShowOperationDialog(dialog, owner, token)) return;
                quality = dialog.Quality; targetBytes = dialog.TargetBytes;
            }
            BatchResult result;
            if (action.Id == "pdf.images")
            {
                report(L("Loading image…"));
                using var pdf = await OnSta(() => ImagePdfWork.Open(files,token));
                var dialog = new ImagePdfDialog(this,pdf);
                if (!ShowOperationDialog(dialog,owner,token)) return;
                int[] order = dialog.Order; var settings = dialog.Settings;
                report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await OnSta(() => pdf.Save(order,settings,token)));
            }
            else if (action.Id == "archive.extract")
            {
                report(L("Reading archive…"));
                using var archive = await Task.Run(() => ZipExtractWork.Open(files[0], token), token);
                token.ThrowIfCancellationRequested();
                if (!ShowOperationDialog(new ZipExtractDialog(this, archive), owner, token)) return;
                report(L("Processing…"));
                var outcome = await Task.Run(() => archive.Extract(token, (done, total) =>
                    Dispatcher.InvokeAsync(() => report(string.Format(L("Extracting file {0} of {1}…"), done, total)))), token);
                string summary = string.Format(L("Extracted {0} of {1} file(s) into a new folder beside the archive."), outcome.FilesWritten, outcome.FileCount);
                if (outcome.Cancelled) summary += " " + L("Cancelled. Completed files were kept.");
                else if (outcome.Error is not null) summary += " " + L("Extraction stopped. Files already extracted were kept; nothing was overwritten.");
                report(summary); Home.ShowResults(new[] { outcome.Folder! }, summary);
                if (outcome.Error is not null && !exiting) ShowError(outcome.Error);
                return;
            }
            else if (action.Id.StartsWith("archive.", StringComparison.Ordinal))
            {
                var format = action.Id switch { "archive.zip" => ArchiveFormat.Zip, "archive.tar" => ArchiveFormat.Tar, _ => ArchiveFormat.GZip };
                using var archive = await OnSta(() => ArchiveWork.Open(files, format, token));
                if (!ShowOperationDialog(new ArchiveDialog(this, archive, format), owner, token)) return;
                report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await OnSta(() => archive.Save(token)));
            }
            else if (action.Id == "audio.noise")
            {
                report(L("Loading audio…"));
                using var document = await Task.Run(() => WaveDocument.Open(files[0], token), token);
                var dialog = new WaveNoiseDialog(this, document); bool save;
                try { save = ShowOperationDialog(dialog, owner, token); }
                finally { await dialog.FinishPreviewAsync(); }
                if (!save) return;
                var settings = dialog.Settings; report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await Task.Run(() => document.SaveNoise(settings, token), token));
            }
            else if (action.Id == "audio.join")
            {
                report(L("Loading audio…"));
                using var document = await Task.Run(() => WaveJoinDocument.Open(files, token), token);
                token.ThrowIfCancellationRequested();
                var dialog = new WaveJoinDialog(this, document); bool save;
                try { save = ShowOperationDialog(dialog, owner, token); }
                finally { await dialog.FinishPreviewAsync(); }
                if (!save) return;
                var order = dialog.Order; report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await Task.Run(() => document.Save(order, token), token));
            }
            else if (action.Id == "audio.exportaiff")
            {
                report(L("Loading audio…"));
                using var document = await Task.Run(() => WavAiffDocument.Open(files[0], token), token);
                token.ThrowIfCancellationRequested();
                var dialog = new WavAiffDialog(this, document); bool save;
                try { save = ShowOperationDialog(dialog, owner, token); }
                finally { await dialog.FinishPreviewAsync(); }
                if (!save) return;
                report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await Task.Run(() => document.Save(token), token));
            }
            else if (action.Id == "audio.aiff")
            {
                report(L("Loading audio…"));
                using var document = await Task.Run(() => AiffDocument.Open(files[0], token), token);
                token.ThrowIfCancellationRequested();
                var dialog = new AiffDialog(this, document); bool save;
                try { save = ShowOperationDialog(dialog, owner, token); }
                finally { await dialog.FinishPreviewAsync(); }
                if (!save) return;
                report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await Task.Run(() => document.Save(token), token));
            }
            else if(action.Id=="text.ocr")
            {
                if(!Prefs.IsOn(Feature.TextCapture))throw new UserError("Enable Text Capture in Settings first.");workFeature=Feature.TextCapture;
                using var document=await OnSta(()=>TextCaptureDocument.Open(files[0],token));token.ThrowIfCancellationRequested();var dialog=new TextCaptureDialog(this,document);
                try{ShowOperationDialog(dialog,owner,token);}finally{await dialog.FinishAsync();}report(L("Text Capture closed. The original image was kept."));return;
            }
            else if(action.Id=="pdf.raster")
            {
                if(!Prefs.IsOn(Feature.PdfPages))throw new UserError("Enable PDF Pages in Settings first.");workFeature=Feature.PdfPages;
                report(L("Loading PDF source pages…"));
                using var document=await Task.Run(()=>PdfRasterDocument.OpenAsync(files,token),token);token.ThrowIfCancellationRequested();var dialog=new PdfRasterDialog(this,document);bool save;
                try{save=ShowOperationDialog(dialog,owner,token);}finally{await dialog.FinishAsync();}
                token.ThrowIfCancellationRequested();if(!save)return;result=new BatchResult();
                try
                {
                    if(dialog.Output==PdfRasterOutput.CombinedPdf)result.Outputs.Add(await Task.Run(()=>document.SavePdf(dialog.Plan,token),token));
                    else foreach(var page in dialog.Plan){token.ThrowIfCancellationRequested();result.Outputs.Add(await Task.Run(()=>dialog.Output==PdfRasterOutput.Png?document.SavePng(page,token):document.SavePdf(new[]{page},token),token));}
                }
                catch(OperationCanceledException){result.Cancelled=true;}catch(Exception error){result.Error=error;}
            }
            else if(action.Id=="audio.systemdecode")
            {
                report(L("Decoding the complete local audio stream…"));
                using var document=await Task.Run(()=>SystemAudioDocument.OpenAsync(files[0],token),token);
                token.ThrowIfCancellationRequested();var dialog=new SystemAudioDialog(this,document);bool save;
                try{save=ShowOperationDialog(dialog,owner,token);}finally{await dialog.FinishPreviewAsync();}
                token.ThrowIfCancellationRequested();if(!save)return;
                report(L("Processing…"));result=new BatchResult();result.Outputs.Add(await Task.Run(()=>document.Save(token),token));
            }
            else if (action.Id == "video.trim")
            {
                report(L("Loading video…"));
                using var video = await Task.Run(() => VideoTrimmer.OpenAsync(files[0], token), token);
                token.ThrowIfCancellationRequested();
                var dialog = new TrimVideoDialog(this, video); bool trim;
                try { trim = ShowOperationDialog(dialog, owner, token); } finally { await dialog.FinishAsync(); }
                token.ThrowIfCancellationRequested();
                if (!trim || dialog.Plan is not { } plan) return;
                result = new BatchResult();
                try
                {
                    report(string.Format(L("Encoding the video: {0}% done…"), 0));
                    result.Outputs.Add(await Task.Run(() => video.TrimAsync(plan, done => Dispatcher.BeginInvoke(() => report(string.Format(L("Encoding the video: {0}% done…"), (int)(done * 100)))), token), token));
                }
                catch (OperationCanceledException) { result.Cancelled = true; } catch (Exception error) { result.Error = error; }
            }
            else if (action.Id == "video.subtitles")
            {
                report(L("Loading video…"));
                using var video = await Task.Run(() => VideoSubtitles.OpenAsync(files[0], token), token);
                token.ThrowIfCancellationRequested();
                var choice = new BurnSubtitlesDialog(this, video);
                if (!ShowOperationDialog(choice, owner, token)) return;
                result = new BatchResult();
                IReadOnlyList<SubtitleCue> cues; IReadOnlyList<SubtitleImage> images;
                if (choice.Transcribe)
                {
                    report(L("Decoding the video's sound…"));
                    using var sound = await Task.Run(() => video.SoundAsync(token), token);
                    report(L("Loading the speech recognition model…"));
                    await Task.Run(() => SpeechModel.UsesGraphics(token), token);
                    var transcript = await Task.Run(() => sound.Run(choice.SpeechLanguage, (done, total, left) =>
                    {
                        int percent = total > 0 ? (int)(100L * done / total) : 0;
                        int seconds = left is { } time ? (int)Math.Min(int.MaxValue, Math.Ceiling(time.TotalSeconds)) : 0;
                        string text = left is not null
                            ? string.Format(L("Transcribing: {0}% done, about {1} left…"), percent, seconds >= 60 ? string.Format(L("{0} min"), (seconds + 59) / 60) : string.Format(L("{0} s"), seconds))
                            : string.Format(L("Transcribing: {0}% done…"), percent);
                        Dispatcher.BeginInvoke(() => report(text));
                    }, token), token);
                    token.ThrowIfCancellationRequested();
                    if (transcript.Segments.Count == 0) throw new UserError("No speech was recognised in this recording. Nothing was saved.");
                    cues = transcript.Cues;
                    // Drawn first, so a cue too long to fit is refused before anything is saved.
                    report(L("Drawing the subtitles…"));
                    images = await OnSta(() => video.Draw(cues, choice.TextSize, token));
                    token.ThrowIfCancellationRequested();
                    if (choice.SaveSubtitles) result.Outputs.AddRange(await Task.Run(() => sound.Save(transcript, false, true, token), token));
                }
                else
                {
                    byte[] bytes;
                    try
                    {
                        using var subtitles = NativeFiles.OpenRead(choice.SubtitleFile!);
                        if (subtitles.Stream.Length > SubRip.MaxBytes) throw new UserError("Subtitle files are limited to 2 MB.");
                        bytes = new byte[subtitles.Stream.Length]; subtitles.Stream.ReadExactly(bytes);
                    }
                    catch (Exception error) when (error is not (UserError or OperationCanceledException)) { throw new UserError("Swivel could not open the subtitle file. Choose an SRT file on a local drive."); }
                    cues = SubRip.Parse(SubRip.Decode(bytes));
                    report(L("Drawing the subtitles…"));
                    images = await OnSta(() => video.Draw(cues, choice.TextSize, token));
                    token.ThrowIfCancellationRequested();
                }
                // The SRT saved above is still listed when encoding fails or is cancelled.
                try
                {
                    report(string.Format(L("Encoding the video: {0}% done…"), 0));
                    result.Outputs.Add(await Task.Run(() => video.BurnAsync(images, done => Dispatcher.BeginInvoke(() => report(string.Format(L("Encoding the video: {0}% done…"), (int)(done * 100)))), token), token));
                }
                catch (OperationCanceledException) { result.Cancelled = true; } catch (Exception error) { result.Error = error; }
            }
            else if (action.Id == "audio.transcribe")
            {
                report(L("Loading audio…"));
                using var document = await Task.Run(() => TranscribeDocument.OpenAsync(files[0], token), token);
                token.ThrowIfCancellationRequested();
                report(L("Loading the speech recognition model…"));
                bool graphics = await Task.Run(() => SpeechModel.UsesGraphics(token), token);
                token.ThrowIfCancellationRequested();
                var choice = new TranscribeDialog(this, document, graphics);
                if (!ShowOperationDialog(choice, owner, token)) return;
                report(L("Preparing the audio…"));
                var transcript = await Task.Run(() => document.Run(choice.SpeechLanguage, (done, total, left) =>
                {
                    int percent = total > 0 ? (int)(100L * done / total) : 0;
                    int seconds = left is { } time ? (int)Math.Min(int.MaxValue, Math.Ceiling(time.TotalSeconds)) : 0;
                    string text = left is not null
                        ? string.Format(L("Transcribing: {0}% done, about {1} left…"), percent, seconds >= 60 ? string.Format(L("{0} min"), (seconds + 59) / 60) : string.Format(L("{0} s"), seconds))
                        : string.Format(L("Transcribing: {0}% done…"), percent);
                    Dispatcher.BeginInvoke(() => report(text));
                }, token), token);
                token.ThrowIfCancellationRequested();
                result = new BatchResult(); result.Outputs.AddRange(await Task.Run(() => document.Save(transcript, choice.Text, choice.Subtitles, token), token));
            }
            else if(action.Id=="audio.g711")
            {
                report(L("Loading audio…"));
                using var document=await Task.Run(()=>G711Document.Open(files[0],token),token);
                token.ThrowIfCancellationRequested();var dialog=new G711Dialog(this,document);bool save;
                try{save=ShowOperationDialog(dialog,owner,token);}
                finally{await dialog.FinishPreviewAsync();}
                token.ThrowIfCancellationRequested();if(!save)return;
                report(L("Processing…"));result=new BatchResult();result.Outputs.Add(await Task.Run(()=>document.Save(token),token));
            }
            else if (action.Id == "audio.floatpcm")
            {
                report(L("Loading audio…"));
                using var document = await Task.Run(() => FloatWaveDocument.Open(files[0], token), token);
                token.ThrowIfCancellationRequested();
                var dialog = new FloatWaveDialog(this, document); bool save;
                try { save = ShowOperationDialog(dialog, owner, token); }
                finally { await dialog.FinishPreviewAsync(); }
                if (!save) return;
                var options = dialog.Options; report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await Task.Run(() => document.Save(options, token), token));
            }
            else if (action.Id == "audio.speed")
            {
                report(L("Loading audio…"));
                using var document = await Task.Run(() => WaveDocument.Open(files[0], token, scanPeaks: false), token);
                token.ThrowIfCancellationRequested();
                var dialog = new WaveSpeedDialog(this, document); bool save;
                try { save = ShowOperationDialog(dialog, owner, token); }
                finally { await dialog.FinishPreviewAsync(); }
                if (!save) return;
                var speed = dialog.Speed; report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await Task.Run(() => document.SaveSpeed(speed, token), token));
            }
            else if (action.Id == "audio.convert")
            {
                report(L("Loading audio…"));
                using var document = await Task.Run(() => WaveDocument.Open(files[0], token, scanPeaks: false), token);
                var dialog = new WaveFormatDialog(this, document); bool save;
                try { save = ShowOperationDialog(dialog, owner, token); }
                finally { await dialog.FinishPreviewAsync(); }
                if (!save) return;
                var format = dialog.Format; report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await Task.Run(() => document.SaveFormat(format, token), token));
            }
            else if (action.Id is "audio.wav" or "audio.music")
            {
                report(L("Loading audio…"));
                using var document = await Task.Run(() => WaveDocument.Open(files[0],token),token);
                WaveDocument? music = null;
                try
                {
                    if (action.Id == "audio.music")
                    {
                        token.ThrowIfCancellationRequested();
                        var picker = new Microsoft.Win32.OpenFileDialog { Title = L("Choose WAV Background Music…"), Filter = L("PCM WAV audio (*.wav)|*.wav"), CheckFileExists = true, Multiselect = false };
                        if (picker.ShowDialog(owner) != true) return;
                        token.ThrowIfCancellationRequested(); string musicPath = picker.FileName;
                        music = await Task.Run(() => WaveDocument.Open(musicPath, token), token);
                        token.ThrowIfCancellationRequested();
                        if (document.Info.Rate != music.Info.Rate)
                        {
                            var preparation = WaveMusicRatePlan.Create(music.Info, document.Info.Rate);
                            if (!ShowOperationDialog(new MusicRateDialog(this, document, music, preparation), owner, token)) return;
                            token.ThrowIfCancellationRequested(); report(L("Preparing music sample rate…"));
                            await Task.Run(() => music.PrepareMusicRate(document.Info.Rate, token), token);
                            token.ThrowIfCancellationRequested();
                        }
                        new WaveMusic().Range(document.Info, music.MusicInfo, new(0, document.Info.Seconds));
                    }
                    var dialog = new WaveDialog(this,document,music); bool save;
                    try { save = ShowOperationDialog(dialog,owner,token); }
                    finally { await dialog.FinishPreviewAsync(); }
                    if (!save) return;
                    var settings = dialog.Settings; var mix = dialog.MusicSettings; report(L("Processing…")); result = new BatchResult();
                    result.Outputs.Add(await Task.Run(() => music is null ? document.Save(settings,token) : document.SaveMix(music,settings,mix,token),token));
                }
                finally { music?.Dispose(); }
            }
            else if (action.Id == "document.text")
            {
                report(L("Reading document text…"));
                using var document = await Task.Run(() => DocumentTextFile.Open(files[0],token),token);
                token.ThrowIfCancellationRequested();
                var dialog = new DocumentTextDialog(this,document);
                if (!ShowOperationDialog(dialog,owner,token)) return;
                token.ThrowIfCancellationRequested();var format = dialog.ExportFormat;
                report(L("Processing…"));result = new BatchResult();
                result.Outputs.Add(await Task.Run(() => document.Save(format,token),token));
            }
            else if (SubtitleFormats.ForId(action.Id) is { } || DataFileFormats.ForId(action.Id) is { } || FontFileFormats.ForId(action.Id) is { })
            {
                // Like the Mac's Convert menu: no options, one file at a time, stop at the first error.
                var subtitleFormat = SubtitleFormats.ForId(action.Id); var dataFormat = DataFileFormats.ForId(action.Id); var fontFormat = FontFileFormats.ForId(action.Id);
                result = new BatchResult();
                for (int i = 0; i < files.Length; i++)
                {
                    report(string.Format(L("Processing file {0} of {1}…"), i + 1, files.Length));
                    try { string file = files[i]; result.Outputs.Add(await Task.Run(() => subtitleFormat is not null ? SubtitleConvert.Run(file, subtitleFormat, token) : dataFormat is not null ? SubtitleConvert.Run(file, dataFormat, token) : SubtitleConvert.Run(file, fontFormat!, token), token)); }
                    catch (OperationCanceledException) { result.Cancelled = true; break; }
                    catch (Exception error) { result.Error = error; break; }
                }
            }
            else if (action.Id == "subtitle.edit")
            {
                using var subtitle = await OnSta(() => SubtitleFile.Open(files[0], token));
                var dialog = new SubtitleDialog(this, subtitle.Text);
                if (!ShowOperationDialog(dialog, owner, token)) return;
                string text = dialog.EditedText; bool transcript = dialog.Transcript;
                report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await OnSta(() => subtitle.Save(text, transcript, token)));
            }
            else if (action.Id == "image.idphoto")
            {
                report(L("Finding the face…"));
                using var document = await OnSta(() => IdPhotoDocument.Open(files[0], token));
                await document.DetectAsync(token);
                token.ThrowIfCancellationRequested();
                var dialog = new IdPhotoDialog(this, document);
                if (!ShowOperationDialog(dialog, owner, token)) return;
                var settings = dialog.Settings;
                report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await OnSta(() => document.Save(settings, token)));
            }
            else if (action.Id == "image.background")
            {
                report(L("Loading image…"));
                using var document = await OnSta(() => ImageTools.Open(files[0], token));
                var dialog = new ImageFrameDialog(this, document.Bitmap);
                if (!ShowOperationDialog(dialog, owner, token)) return;
                var settings = dialog.Settings;
                var bitmap = await OnSta(() => ImageFrameTools.Render(document.Bitmap, settings, token));
                if (!ShowOperationDialog(new ImageOutputDialog(this, bitmap), owner, token)) return;
                result = new BatchResult(); result.Outputs.Add(await OnSta(() => ImageFrameTools.Save(document, bitmap, token)));
            }
            else if (action.Id == "image.gifpalette")
            {
                report(L("Loading image…"));
                using var document=await OnSta(()=>GifPaletteDocument.Open(files[0],token));
                token.ThrowIfCancellationRequested();var dialog=new GifPaletteDialog(this,document);
                try {if(!ShowOperationDialog(dialog,owner,token))return;}
                finally {await dialog.FinishAsync();}
                token.ThrowIfCancellationRequested();var prepared=dialog.Selection??throw new InvalidOperationException("A prepared GIF preview is required.");
                report(L("Processing…"));result=new BatchResult();
                result.Outputs.Add(await Task.Run(()=>document.Save(prepared,token),token));
            }
            else if (action.Id == "image.jpegmetadata")
            {
                report(L("Inspecting JPEG segments…"));
                using var document = await OnSta(() => JpegMetadataDocument.Open(files[0], token));
                token.ThrowIfCancellationRequested();
                if (!ShowOperationDialog(new JpegMetadataDialog(this, document), owner, token)) return;
                report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await Task.Run(() => document.Save(token), token));
            }
            else if (action.Id == "image.pngmetadata")
            {
                report(L("Inspecting PNG chunks…"));
                using var document = await OnSta(() => PngMetadataDocument.Open(files[0], token));
                token.ThrowIfCancellationRequested();
                if (!ShowOperationDialog(new PngMetadataDialog(this, document), owner, token)) return;
                report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await Task.Run(() => document.Save(token), token));
            }
            else if (action.Id == "image.metadata")
            {
                using var document = await OnSta(() => ImageTools.Open(files[0], token));
                var fields = await OnSta(() => ImageTools.Metadata(document, token));
                if (!ShowOperationDialog(new MetadataDialog(this, fields), owner, token)) return;
                result = new BatchResult(); result.Outputs.Add(await OnSta(() => ImageTools.Export(document, "convert.png", edge, token)));
            }
            else if (action.Id == "image.collage")
            {
                var dialog = new CollageDialog(this, files.Length);
                if (!ShowOperationDialog(dialog, owner, token)) return;
                report(L("Loading image…"));
                using var collage = await OnSta(() => CollageWork.Open(files, dialog.Settings, token));
                token.ThrowIfCancellationRequested();
                if (!ShowOperationDialog(new CollagePreview(this, collage.Bitmap), owner, token)) return;
                report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await OnSta(() => collage.Save(token)));
            }
            else if (action.Id == "image.retouch")
            {
                report(L("Loading image…"));
                using var document = await OnSta(() => ImageTools.Open(files[0],token));
                var preview = await OnSta(() => ImageTools.ReadPixels(document.Bitmap,1200,token));
                var dialog = new RetouchDialog(this,preview,document.Bitmap.PixelWidth,document.Bitmap.PixelHeight);
                if (!ShowOperationDialog(dialog,owner,token)) return;
                var strokes = dialog.Strokes; report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await OnSta(() => ImageTools.Export(document,action.Id,edge,token,retouch:strokes)));
            }
            else if (action.Id is "image.redact" or "image.annotate")
            {
                report(L("Loading image…"));
                using var document = await OnSta(() => ImageTools.Open(files[0], token));
                token.ThrowIfCancellationRequested();
                var dialog = new MarkupDialog(this, document.Bitmap, action.Id == "image.redact");
                if (!ShowOperationDialog(dialog, owner, token)) return;
                var marks = dialog.Marks;
                report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await OnSta(() => ImageTools.Export(document, action.Id, edge, token, marks: marks)));
            }
            else if (action.Id == "image.adjust")
            {
                report(L("Loading image…"));
                using var document = await OnSta(() => ImageTools.Open(files[0], token));
                var pixels = await OnSta(() => ImageTools.ReadPixels(document.Bitmap, 1200, token));
                token.ThrowIfCancellationRequested();
                var dialog = new PhotoDialog(this, pixels);
                if (!ShowOperationDialog(dialog, owner, token)) return;
                var settings = dialog.Settings;
                report(L("Processing…")); result = new BatchResult();
                result.Outputs.Add(await OnSta(() => ImageTools.Export(document, action.Id, edge, token, adjustments: settings)));
            }
            else if (action.Id == "image.crop")
            {
                report(L("Loading image…"));
                using var document = await OnSta(() => ImageTools.Open(files[0], token));
                token.ThrowIfCancellationRequested();
                var dialog = new CropDialog(this, document.Bitmap);
                if (!ShowOperationDialog(dialog, owner, token)) return;
                var crop = dialog.Selection;
                report(L("Processing…"));
                result = new BatchResult();
                result.Outputs.Add(await OnSta(() => ImageTools.Export(document, action.Id, edge, token, crop: crop)));
            }
            else
            {
                string format = ImageOutputInfo.Format(action.Id, recipe);
                int factor = 0, current = 0, count = files.Length;
                if (action.Id == "image.upscale")
                {
                    report(L("Loading the upscaling model…"));
                    bool graphics = await OnSta(() => UpscaleModel.UsesGraphics(token));
                    token.ThrowIfCancellationRequested();
                    var choice = new UpscaleDialog(this, files, graphics);
                    if (!ShowOperationDialog(choice, owner, token)) return;
                    factor = choice.Factor;
                }
                else if (!ShowOperationDialog(new ConversionDialog(this, action, files, format), owner, token)) return;
                var entries = await OnSta(() => ImageBatch.Run(files,
                    file => factor > 0
                        ? Upscaler.Run(file, factor, token, (done, total, left) =>
                        {
                            int index = current;
                            int seconds = left is { } time ? (int)Math.Min(int.MaxValue, Math.Ceiling(time.TotalSeconds)) : 0;
                            string text = left is not null
                                ? string.Format(L("Upscaling file {0} of {1}: block {2} of {3}, about {4} left…"), index, count, done, total,
                                    seconds >= 60 ? string.Format(L("{0} min"), (seconds + 59) / 60) : string.Format(L("{0} s"), seconds))
                                : string.Format(L("Upscaling file {0} of {1}: block {2} of {3}…"), index, count, done, total);
                            Dispatcher.BeginInvoke(() => report(text));
                        })
                        : ImageTools.Run(file, action.Id, edge, token, recipe, quality, resize, targetBytes), token,
                    (index, total) => { current = index; Dispatcher.Invoke(() => report(string.Format(L("Processing file {0} of {1}…"), index, total))); }));
                result = new BatchResult();
                result.Outputs.AddRange(entries.Where(x => x.State == BatchState.Saved).Select(x => x.Output!));
                result.Cancelled = entries.Any(x => x.State == BatchState.Cancelled);
                result.Error = entries.FirstOrDefault(x => x.State == BatchState.Failed)?.Error;
                Home.ShowBatchResults(entries);
                if (result.Error is not null || result.Cancelled)
                {
                    string summary = string.Format(L("Saved: {0}. Failed: {1}. Cancelled: {2}. Not started: {3}."),
                        result.Outputs.Count, entries.Count(x => x.State == BatchState.Failed),
                        entries.Count(x => x.State == BatchState.Cancelled), entries.Count(x => x.State == BatchState.NotStarted));
                    report(summary); Home.ShowResults(result.Outputs, summary);
                    if (result.Error is not null && !exiting) ShowError(result.Error);
                    return;
                }
            }
            string status = string.Format(L("Saved {0} file(s) beside the originals."), result.Outputs.Count);
            if (result.Cancelled) status += " " + L("Cancelled. Completed files were kept.");
            report(status);
            Home.ShowResults(result.Outputs, status);
            if (result.Error is not null && !exiting) ShowError(result.Error);
        }
        catch (OperationCanceledException) { report(L("Cancelled. Completed files were kept.")); }
        catch (Exception error) { if (!exiting) ShowError(error); }
        finally
        {
            owner.Closed -= ownerClosed;
            if (ReferenceEquals(workCancellation, ownedWork))
            {
                ownedWork.Dispose(); workCancellation = null; work = null; workFeature = null; workOwner = null; Home.UpdateBusy();
                NotifyWorkStateChanged();
            }
            finished.SetResult();
        }
    }
    // `directory` is given when Move Files found every file on another drive: then it copies
    // instead, keeping every original.
    private async Task RunCopyAsync(string[] files, Window owner, Action<string> report, CancellationToken token, string? directory = null)
    {
        token.ThrowIfCancellationRequested();
        bool crossDrive = directory is not null;
        if (directory is null)
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Destination Folder…"), Multiselect = false };
            if (picker.ShowDialog(owner) != true) return;
            token.ThrowIfCancellationRequested(); directory = picker.FolderName;
        }
        CopyOutcome outcome;
        using (var session = await Task.Run(() => CopySession.Open(files, directory, token), token))
        {
            token.ThrowIfCancellationRequested();
            if (!ShowOperationDialog(new CopyFilesDialog(this, session, crossDrive), owner, token)) return;
            token.ThrowIfCancellationRequested();
            if (!Prefs.IsOn(Feature.FileMoves)) throw new UserError("Enable Move Files in Settings first.");
            report(L("Processing…"));
            outcome = await Task.Run(() => session.Execute(token, (done, total) =>
                Dispatcher.InvokeAsync(() => report(string.Format(L("Copying file {0} of {1}…"), done, total)))));
        }
        string summary = string.Format(L("Copied {0} of {1} file(s). Originals were not changed."), outcome.Copied.Count, outcome.Total);
        if (crossDrive) summary = L("The destination is on another drive, so the files were copied instead of moved.") + " " + summary;
        if (outcome.Cancelled) summary += " " + L("Cancelled. Completed files were kept.");
        report(summary); Home.ShowResults(outcome.Copied.Select(x => x.Destination), summary);
        if (exiting || token.IsCancellationRequested) return;
        if (outcome.Copied.Count == 0) { if (outcome.Error is not null) ShowError(outcome.Error); return; }
        // The results window shows any problem and can undo copies while it is open.
        var results = new CopyFilesResultDialog(this, outcome, crossDrive);
        ShowOperationDialog(results, owner, token);
        if (results.Moved == 0) return;
        summary += " " + string.Format(L("Copies moved to the Recycle Bin or removed: {0}"), results.Moved);
        report(summary); Home.ShowResults(results.Remaining, summary);
    }

    private async Task RunMoveAsync(string[] files, Window owner, Action<string> report, CancellationToken token, bool undo)
    {
        if (!Prefs.IsOn(Feature.FileMoves)) throw new UserError("Enable Move Files in Settings first.");
        workFeature = Feature.FileMoves; token.ThrowIfCancellationRequested();
        if (undo && MoveHistory.Count == 0) throw new UserError("There is no file move to undo in this session.");
        var receipt = undo ? MoveHistory[^1] : Array.Empty<MoveLocation>();
        string directory = "";
        if (!undo)
        {
            token.ThrowIfCancellationRequested();
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = L("Choose Destination Folder…"), Multiselect = false };
            if (picker.ShowDialog(owner) != true) return;
            token.ThrowIfCancellationRequested(); directory = picker.FolderName;
            // Between drives a move would be a copy followed by deleting the original; Swivel
            // only offers the copy (as on Mac), and a mix of drives is refused.
            int? onDestination = await OnSta(() => NativeFiles.CountOnVolumeOf(files, directory));
            token.ThrowIfCancellationRequested();
            if (onDestination == 0) { await RunCopyAsync(files, owner, report, token, directory); return; }
            if (onDestination < files.Length)
                throw new UserError("Some selected files are on the destination drive and some are on another drive. Move them in separate batches; files on another drive are copied instead and their originals stay.");
            // Only real moves add to the history; a copy instead does not.
            if (MoveHistory.Count >= 20) throw new UserError("Undo or forget move history before starting another batch (20 batches maximum).");
        }
        MoveOutcome? outcome = null;
        IReadOnlyList<MovePair> plan;
        var session = await OnSta(() => undo ? MoveSession.ForUndo(receipt, token) : MoveSession.Open(files, directory, token));
        try
        {
            token.ThrowIfCancellationRequested(); plan = session.Plan;
            if (!ShowOperationDialog(new MoveFilesDialog(this, plan, undo), owner, token)) return;
            token.ThrowIfCancellationRequested();
            if (!Prefs.IsOn(Feature.FileMoves)) throw new UserError("Enable Move Files in Settings first.");
            report(L("Processing…")); outcome = await OnSta(() => session.Execute(token));
        }
        finally
        {
            try { session.Dispose(); }
            catch (Exception cleanup) when (outcome is not null)
            {
                // Cleanup diagnostics must not hide already performed moves or their receipts.
                outcome = outcome with { Error = outcome.Error is null ? cleanup : new AggregateException(outcome.Error, cleanup) };
            }
        }
        LastMoveOutcome = outcome; LastMoveWasUndo = undo;
        // Always account for actual locations, including a partial rollback after cancellation.
        // Undo receipts and references are updated only after all DELETE leases are released.
        IReadOnlyList<MoveLocation> remainingHistory;
        if (undo)
        {
            MoveHistory.RemoveAt(MoveHistory.Count - 1);
            remainingHistory = MoveReceipts.AfterUndo(receipt, outcome);
        }
        else remainingHistory = outcome.Remaining;
        for (int i = 0; i < MoveHistory.Count; i++)
            MoveHistory[i] = MoveReceipts.RefreshHistory(MoveHistory[i], outcome, undo ? receipt : null);
        if (remainingHistory.Count > 0) MoveHistory.Add(remainingHistory);
        var changes = outcome.Locations.Where(x => x.Original != x.Current)
            .ToDictionary(x => x.Original, x => x.Current, StringComparer.Ordinal);
        var floatingSelection = floatingShelf?.SelectedPaths ?? Array.Empty<string>();
        var homeShelfSelection = Home.ShelfSelection;
        string Updated(string path) => changes.TryGetValue(path, out string? current) ? current : path;
        var before = Shelf.ToArray();
        for (int i = 0; i < before.Length; i++) Shelf[i] = Updated(before[i]);
        floatingShelf?.RestoreSelection(floatingSelection.Select(Updated)); Home.RestoreShelfSelection(homeShelfSelection.Select(Updated));
        Home.ApplyMoves(changes);
        for (int i = 0; i < Prints.Count; i++) { string path = Updated(Prints[i].Path); if (path != Prints[i].Path) Prints[i] = Prints[i] with { Path = path }; }
        foreach (var panel in panels.ToArray()) panel.ApplyMoves(changes);
        string status = outcome.Error is null
            ? L(outcome.Locations.All(x => x.Original == x.Current) ? "No files moved." : undo ? "File move undone." : "Files moved. Review locations and session undo in Move Files.")
            : outcome.Remaining.Count > 0 ? L("Some files could not return to their starting folders. Review the recorded current paths in Move Files.")
            : L("The move stopped. Files returned to their starting locations.");
        report(status); Home.SetStatus(status); Home.Refresh();
        if (!token.IsCancellationRequested && !exiting)
            ShowOperationDialog(new MoveFilesResultDialog(this, outcome, undo), owner, token);
    }
    private async Task RunRenameAsync(string[] files, Window owner, Action<string> report, CancellationToken token, bool undo)
    {
        if (undo && RenameHistory.Count == 0) throw new UserError("There is no rename to undo in this session.");
        if (!undo && RenameHistory.Count >= 20) throw new UserError("Undo or clear rename history before starting another batch (20 batches maximum).");
        var receipt = undo ? RenameHistory[^1] : Array.Empty<RenameLocation>();
        RenameOutcome outcome;
        using (var session = await OnSta(() => undo ? RenameSession.ForUndo(receipt, token) : RenameSession.Open(files, token)))
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<RenamePair> plan;
            if (undo)
            {
                if (!ShowOperationDialog(new UndoRenameDialog(this, receipt), owner, token)) return;
                plan = receipt.Select(x => new RenamePair(x.Current, x.Original)).ToArray();
            }
            else
            {
                var dialog = new RenameDialog(this, session.Paths);
                if (!ShowOperationDialog(dialog, owner, token)) return;
                plan = dialog.Plan;
            }
            if (!Prefs.IsOn(Feature.BatchRename)) throw new UserError("Enable Batch Rename in Settings first.");
            report(L("Processing…"));
            outcome = await OnSta(() => session.Execute(plan, token));
        }
        if (undo)
        {
            RenameHistory.RemoveAt(RenameHistory.Count - 1);
            var remaining = outcome.Locations.Select((x, i) => new RenameLocation(receipt[i].Original, x.Current, x.Stamp)).Where(x => x.Original != x.Current).ToArray();
            if (remaining.Length > 0) RenameHistory.Add(remaining);
        }
        else if (outcome.Remaining.Count > 0) RenameHistory.Add(outcome.Remaining);
        // Update references only after DELETE leases are released, so normal reads work.
        var changes = outcome.Locations.Where(x => x.Original != x.Current).ToArray();
        var floatingSelection = floatingShelf?.SelectedPaths ?? Array.Empty<string>();
        var homeShelfSelection = Home.ShelfSelection;
        string Updated(string path) => changes.FirstOrDefault(x => x.Original.Equals(path,StringComparison.OrdinalIgnoreCase))?.Current ?? path;
        // Apply the mapping once to each original reference; sequential substitutions
        // would collapse swaps/chains into the final processed name.
        var shelfBeforeRename = Shelf.ToArray();
        for (int i = 0; i < shelfBeforeRename.Length; i++) Shelf[i] = Updated(shelfBeforeRename[i]);
        floatingShelf?.RestoreSelection(floatingSelection.Select(Updated)); Home.RestoreShelfSelection(homeShelfSelection.Select(Updated));
        Home.ApplyRenames(changes);
        for (int i = 0; i < Prints.Count; i++) { string path = Updated(Prints[i].Path); if (path != Prints[i].Path) Prints[i] = Prints[i] with { Path = path }; }
        foreach (var panel in panels.ToArray()) panel.ApplyRenames(changes);
        string status = outcome.Error is null ? L(undo ? "Rename undone." : "Files renamed. Undo is available in Batch Rename.")
            : outcome.Remaining.Count > 0 ? L("Some names could not be restored. Review the current paths in Batch Rename before trying Undo.")
            : L("The operation stopped. Names were restored to their state before this operation.");
        report(status); Home.SetStatus(status); Home.Refresh();
        if (outcome.Error is not null && !outcome.Cancelled && !exiting) ShowError(outcome.Error);
    }
    public async Task QuitAsync()
    {
        if (exiting) return;
        exiting = true;
        CancelWork();
        await FinishWheelGestureAsync();
        await FinishImageArrivalsAsync();
        if (work is not null) { Home.SetStatus(L("Finishing cancellation before quitting…")); await work; }
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        workCancellation?.Cancel();
        hotkeys?.Dispose();
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        if (Awake is not null) { try { Awake.Dispose(); } catch { /* Thread termination also releases the request. */ } }
        mutex?.ReleaseMutex(); mutex?.Dispose();
        base.OnExit(e);
    }
    private sealed class BatchResult
    {
        public List<string> Outputs { get; } = new();
        public Exception? Error;
        public bool Cancelled;
    }
}
