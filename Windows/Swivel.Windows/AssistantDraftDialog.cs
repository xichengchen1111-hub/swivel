using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;

namespace Swivel.Windows;

internal static class AssistantDraftNotices
{
    internal const string Scope="Only file path text is prepared. Swivel does not read or attach file contents, upload files, choose a workspace or send the draft. Review and send it yourself in the receiving app. The read-only request is text, not an enforced sandbox; the receiving app controls its existing context, permissions and subsequent network activity.";
    internal const string Handler="Open Draft uses the Windows-registered codex or claude protocol handler. Swivel does not verify that handler's identity, version or availability. A compatible desktop app must already be installed. If opening fails, keep this window open and copy the draft instead.";
    internal const string Limits="Choose 1–100 ordinary local files. The complete draft is limited to 12,000 UTF-8 bytes; long or escaped paths can fit fewer files. Open Draft additionally requires a complete link of at most 2,048 characters. Longer links disable opening while Copy Draft retains the full text. No folder permission, file attachment or automatic submission is requested.";
    internal const string References="Paths are held against replacement while this window is open. Only JSON path references are copied or passed to the handler; they are not content snapshots. Files may change after this window closes, so check them again in the receiving app.";
    internal const string Failure="Windows could not open this draft. Check the installed app and protocol association, or copy the draft and paste it yourself.";
}
internal sealed class AssistantDraftDialog : Window
{
    private readonly App app;
    private readonly AssistantDraftSession session;
    private readonly CancellationToken token;
    private readonly ComboBox target;
    private readonly TextBox prompt,address,paths;
    private readonly TextBlock status;
    private readonly Button open;
    internal AssistantDraft.Plan Plan {get;private set;}=null!;
    internal AssistantDraftDialog(App app,AssistantDraftSession session,CancellationToken token)
    {
        this.app=app;this.session=session;this.token=token;
        Title=app.L("Prepare Assistant Draft…");Width=850;Height=820;MinWidth=450;MinHeight=320;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        status=UI.Text("");footer.Children.Add(status);var buttons=new WrapPanel();footer.Children.Add(buttons);
        buttons.Children.Add(UI.Button(app.L("Copy Draft"),()=>app.Try(()=>{session.Validate(Plan,token);Clipboard.SetText(Plan.Text);status.Text=app.L("Draft copied. Paste and review it in the receiving app.");})));
        open=UI.Button(app.L("Open Draft"),()=>{
            try{session.Launch(Plan,token);DialogResult=true;}
            catch(OperationCanceledException){Close();}
            catch(Exception ex)when(ex is UserError or Win32Exception or IOException or InvalidOperationException)
            {status.Text=ex is UserError?app.L(ex.Message):app.L(AssistantDraftNotices.Failure);}
        });buttons.Children.Add(open);
        var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;buttons.Children.Add(close);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(app.L("Target Application")));target=new ComboBox{ItemsSource=new[]{"Codex","Claude Code"},SelectedIndex=0,MinHeight=32};AutomationProperties.SetName(target,app.L("Target Application"));body.Children.Add(target);
        foreach(string note in new[]{AssistantDraftNotices.Scope,AssistantDraftNotices.Handler,AssistantDraftNotices.Limits,AssistantDraftNotices.References})body.Children.Add(UI.Text(app.L(note),12));
        TextBox Field(string key,double height)
        {
            body.Children.Add(UI.Text(app.L(key)));var box=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MinHeight=48,MaxHeight=height,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};AutomationProperties.SetName(box,app.L(key));body.Children.Add(box);return box;
        }
        paths=Field("File references (JSON strings):",150);prompt=Field("Complete Draft",300);address=Field("Address to Open",130);
        body.Children.Add(UI.Text(app.L(TextSimilarityNotices.Downgrade),12));
        target.SelectionChanged+=(_,_)=>Refresh();Refresh();
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private void Refresh()
    {
        Plan=session.Review(target.SelectedIndex==1?DraftTarget.ClaudeCode:DraftTarget.Codex,app.Prefs.Language,token);
        paths.Text=string.Join("\n",Plan.Paths.Select(path=>System.Text.Json.JsonSerializer.Serialize(path)));
        prompt.Text=Plan.Text;address.Text=Plan.Address??app.L(AssistantDraft.LongAddress);open.IsEnabled=Plan.Address is not null;
        status.Text=Plan.Address is null?app.L(AssistantDraft.LongAddress):string.Format(app.L("Complete draft: {0:N0} UTF-8 bytes · link: {1:N0} characters"),Plan.Utf8Bytes,Plan.Address.Length);
    }
}
