using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Swivel.Core;

namespace Swivel.Windows;

public static class QrTools
{
    public static BitmapSource Render(string text) => Render(QrCode.Encode(text));
    internal static BitmapSource Render(QrCode code)
    {
        int side = (code.Size + QrCode.QuietZone * 2) * 8;
        var bitmap = BitmapSource.Create(side,side,96,96,PixelFormats.Gray8,null,code.Raster(),side);
        bitmap.Freeze(); return bitmap;
    }
    public static string Save(string text, string directory, CancellationToken token)=>Save(text,QrCorrection.Low,directory,token);
    public static string Save(string text,QrCorrection correction,string directory,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Render(QrCode.Encode(text,correction))));
        return NativeFiles.WriteInDirectory(directory,"QR code","png",encoder.Save,token);
    }
}

internal sealed class QrDialog : Window
{
    private Task gardenPending=Task.CompletedTask;private QrGardenDialog? garden;
    public string OutputDirectory { get; private set; } = "";
    public string Payload { get; private set; } = "";
    public QrCorrection Correction { get; private set; } = QrCorrection.Low;
    public QrDialog(App app, QrEntry? entry = null)
    {
        Title = app.L("Create QR Code"); Width = 620; Height = 790; MinWidth = 450; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) };
        var actions = new WrapPanel(); DockPanel.SetDock(actions,Dock.Bottom); root.Children.Add(actions);
        var panel = new StackPanel(); root.Children.Add(UI.Scroll(panel)); Content = root;
        panel.Children.Add(UI.Text(app.L("Choose L, M, Q or H error correction: up to 2,952 / 2,330 / 1,662 / 1,272 UTF-8 bytes. Higher correction adds redundancy and reduces capacity. Chinese and emoji use multiple bytes. Changing the level never shortens your text.")));
        panel.Children.Add(UI.Text(app.L("QR Error Correction")));
        var correction=new ComboBox{MinHeight=30};correction.Items.Add(app.L("L — more capacity"));correction.Items.Add(app.L("M — more error correction"));correction.Items.Add(app.L("Q — higher error correction"));correction.Items.Add(app.L("H — highest error correction"));
        correction.SelectedIndex=entry?.Correction switch { QrCorrection.Medium=>1,QrCorrection.Quartile=>2,QrCorrection.High=>3,_=>0 };AutomationProperties.SetName(correction,app.L("QR Error Correction"));panel.Children.Add(correction);
        QrCorrection SelectedCorrection()=>correction.SelectedIndex switch { 1=>QrCorrection.Medium,2=>QrCorrection.Quartile,3=>QrCorrection.High,_=>QrCorrection.Low };
        // Preserve an invalid paste in full so the user can shorten it explicitly.
        // MaxLength would silently turn an over-capacity URL into a different code.
        var field = new TextBox { Text = entry?.Text ?? "", AcceptsReturn = true, MinHeight = 90, MaxHeight = 160, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(field,app.L("QR Text")); panel.Children.Add(field);
        var capacity = UI.Text(""); panel.Children.Add(capacity);
        var status = UI.Text(""); panel.Children.Add(status);
        var image = new Image { Width = 260, Height = 260, Stretch = Stretch.Uniform, Margin = new Thickness(0,8,0,8), SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.NearestNeighbor);
        panel.Children.Add(new ScrollViewer { Content=image,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,MaxWidth=380 });
        var details = UI.Text("",12); panel.Children.Add(details);
        panel.Children.Add(UI.Text(app.L("Garden Template")));var template=new ComboBox{MinHeight=30,ItemsSource=new[]{app.L("Tree"),app.L("Bouquet"),app.L("Island")},SelectedIndex=(int)(entry?.Template??QrGardenTemplate.Tree)};AutomationProperties.SetName(template,app.L("Garden Template"));panel.Children.Add(template);
        panel.Children.Add(UI.Text(app.L("Garden Season")));var season=new ComboBox{MinHeight=30,ItemsSource=new[]{app.L("Summer"),app.L("Spring"),app.L("Autumn")},SelectedIndex=(int)(entry?.Season??QrGardenSeason.Summer)};AutomationProperties.SetName(season,app.L("Garden Season"));panel.Children.Add(season);
        panel.Children.Add(UI.Text(app.L("Garden choices are saved only with the library entry. Windows 0.50 and earlier can discard these choices when rewriting settings and reject backups containing them. Keep a pre-upgrade backup before downgrading."),12));
        panel.Children.Add(UI.Text(app.L("Black-and-white QR with a four-module white border, the selected error correction and UTF-8 text. Test the code with your scanner before printing. Links are never opened automatically."),12));
        panel.Children.Add(UI.Text(app.L("Longer text creates a denser code. Exported PNG images use eight pixels per module; test your scanner at the final display or print size."),12));
        BitmapSource? ready = null;QrCode? readyCode=null; string readyText = ""; QrCorrection readyCorrection=QrCorrection.Low;
        var showGarden=UI.Button(app.L("Open 3D Garden…"),()=>{});actions.Children.Add(showGarden);
        showGarden.Click+=async(_,_)=>
        {
            if(readyCode is null||!gardenPending.IsCompleted)return;var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);gardenPending=completion.Task;
            try{garden=new QrGardenDialog(app,readyCode,(QrGardenTemplate)template.SelectedIndex,(QrGardenSeason)season.SelectedIndex){Owner=this};try{garden.ShowDialog();}finally{await garden.FinishAsync();}}
            catch(Exception){status.Text=app.L("The 3D scene could not be prepared. The flat QR image is still available.");}
            finally{garden=null;completion.TrySetResult();}
        };
        Closed+=(_,_)=>garden?.RequestClose();
        var copy = UI.Button(app.L("Copy Image"),() => app.Try(() => { if (ready is not null) { Clipboard.SetImage(ready); status.Text = app.L("Copied."); } }));
        var save = UI.Button(app.L("Save PNG…"),() => app.Try(() => {
            if (ready is null) return;
            var picker = new OpenFolderDialog { Title = app.L("Choose a folder for the QR image") };
            if (picker.ShowDialog(this) != true) return;
            OutputDirectory = picker.FolderName; Payload = readyText; Correction=readyCorrection; DialogResult = true;
        }));
        actions.Children.Add(copy); actions.Children.Add(save);
        panel.Children.Add(UI.Text(app.L("Library Name")));
        var name = new TextBox { Text = entry?.Name ?? "", MaxLength = 48, MinHeight = 30 }; AutomationProperties.SetName(name,app.L("Library Name")); panel.Children.Add(name);
        panel.Children.Add(UI.Text(app.L("QR Group (optional)")));var group=new TextBox { Text=entry?.Group??"",MaxLength=32,MinHeight=30 };AutomationProperties.SetName(group,app.L("QR Group (optional)"));panel.Children.Add(group);
        string id = entry?.Id ?? Guid.NewGuid().ToString("N");
        var library = UI.Button(app.L("Save to QR Library"),() => app.Try(() => {
            if (ready is null) return;
            app.Prefs.SaveQr(new(id,name.Text.Trim(),readyText,group.Text.Trim(),readyCorrection,(QrGardenTemplate)template.SelectedIndex,(QrGardenSeason)season.SelectedIndex)); status.Text = app.L("QR saved to your local library.");
        })); panel.Children.Add(library);
        panel.Children.Add(UI.Text(app.L("Library text is stored in your local Swivel settings only when you save it. PNG files are saved in the folder you choose, with a new name if one already exists."),12));
        var close = UI.Button(app.L("Close"),Close); close.IsCancel = true; actions.Children.Add(close);
        SourceInitialized += (_,_) => FitWorkArea(Owner ?? this); DpiChanged += (_,_) => FitWorkArea(this);
        void Update()
        {
            ready = null;readyCode=null; readyText = ""; image.Source = null; details.Text = ""; capacity.Text = "";
            copy.IsEnabled = save.IsEnabled = library.IsEnabled =showGarden.IsEnabled= false;
            // Counting feedback is bounded independently of encoder validation. Large
            // invalid pastes remain editable without a second full-text UTF-8 scan.
            if (field.Text.Length <= 4096)
            {
                try { capacity.Text = string.Format(app.L("{0} / {1} UTF-8 bytes"),new UTF8Encoding(false,true).GetByteCount(field.Text),QrCode.Limit(SelectedCorrection())); }
                catch (EncoderFallbackException) { /* Encode supplies the localized Unicode error. */ }
            }
            else { status.Text = app.L(QrCode.CapacityMessage(SelectedCorrection())); return; }
            try
            {
                var code = QrCode.Encode(field.Text,SelectedCorrection());
                readyCode=code;
                ready = QrTools.Render(code); readyText = field.Text; readyCorrection=code.Correction; image.Source = ready;
                // Whole 2–4 DIPs per module keep all versions and quiet zones
                // within 380 DIPs. Export/clipboard retain eight pixels per module.
                int side = code.Size + QrCode.QuietZone * 2;
                image.Width = image.Height = side * Math.Min(4,380 / side);
                details.Text = string.Format(app.L("QR version {0} · {1} × {1} modules · {2} error correction"),code.Version,code.Size,QrCode.Level(code.Correction));
                status.Text = "";
                copy.IsEnabled = save.IsEnabled = library.IsEnabled = showGarden.IsEnabled=true;
            }
            catch (UserError error) { status.Text = app.L(error.Message); }
        }
        field.TextChanged += (_,_) => Update(); correction.SelectionChanged += (_,_) => Update(); Update();
    }
    internal async Task FinishAsync(){garden?.RequestClose();await gardenPending;}
    private void FitWorkArea(Window reference)
    {
        var area=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(reference).Handle).WorkingArea;
        var dpi=VisualTreeHelper.GetDpi(reference);
        double height=Math.Max(1,area.Height/dpi.DpiScaleY-16),width=Math.Max(1,area.Width/dpi.DpiScaleX-16);
        MinHeight=Math.Min(380,height);MinWidth=Math.Min(450,width);MaxHeight=height;MaxWidth=width;
        Height=Math.Min(Height,height);Width=Math.Min(Width,width);
    }
}
