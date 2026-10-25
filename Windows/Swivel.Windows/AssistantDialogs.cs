using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class AssistantSettingsDialog:Window
{
    public const string Scope="Optional assistant, off by default. Configure a complete API endpoint and your own model. Requests use direct connections without system proxies, redirects, cookies or Windows login credentials. HTTPS is required except literal loopback HTTP. Some service/model parameters differ; there is no automatic fallback, retry, key test or model download.";
    internal AssistantSettingsDialog(App app)
    {
        Title=app.L("Assistant Settings");Width=740;Height=730;MinWidth=460;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();var status=UI.Text("");footer.Children.Add(status);var actions=new WrapPanel();footer.Children.Add(actions);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);var body=new StackPanel();body.Children.Add(UI.Text(app.L(Scope),12));
        var saved=app.Prefs.Assistant;var provider=new ComboBox{ItemsSource=new[]{app.L("Chat Completions compatible"),app.L("Claude Messages")},SelectedIndex=(int)(saved?.Provider??AssistantProvider.ChatCompletions),MinHeight=30};body.Children.Add(UI.Text(app.L("API Protocol")));AutomationProperties.SetName(provider,app.L("API Protocol"));body.Children.Add(provider);
        body.Children.Add(UI.Text(app.L("Complete API Endpoint")));var endpoint=new TextBox{Text=saved?.Endpoint??"",MinHeight=30};AutomationProperties.SetName(endpoint,app.L("Complete API Endpoint"));body.Children.Add(endpoint);body.Children.Add(UI.Text(app.L("Include the complete path, such as /v1/chat/completions or /v1/messages. No address or model is contacted while editing."),12));
        body.Children.Add(UI.Text(app.L("Model Identifier")));var model=new TextBox{Text=saved?.Model??"",MinHeight=30};AutomationProperties.SetName(model,app.L("Model Identifier"));body.Children.Add(model);
        var modern=new CheckBox{Content=app.L("Use max_completion_tokens instead of max_tokens (Chat Completions only)"),IsChecked=saved?.CompletionTokenLimit==true};body.Children.Add(modern);provider.SelectionChanged+=(_,_)=>{modern.IsEnabled=provider.SelectedIndex==0;if(!modern.IsEnabled)modern.IsChecked=false;};modern.IsEnabled=provider.SelectedIndex==0;
        AssistantSettings Current()=>new AssistantSettings((AssistantProvider)provider.SelectedIndex,endpoint.Text,model.Text,modern.IsChecked==true).Normalize();
        body.Children.Add(UI.Text(app.L("Service Key")));var password=new PasswordBox{MinHeight=30};AutomationProperties.SetName(password,app.L("Service Key"));body.Children.Add(password);
        body.Children.Add(UI.Text(app.L("Keys are saved only by Save Key, in Windows Credential Manager for this protocol, scheme, host and port. Changing the path on the same service shares that key. Keys are never saved in Swivel settings or request previews. Other software running as your Windows user may access generic credentials. Managed HTTP strings cannot be guaranteed erased from memory."),12));
        var scope=UI.Text("");body.Children.Add(scope);
        void Run(Action action){try{action();}catch(Exception e){status.Text=e is UserError u?app.L(u.Message):app.L(AssistantCredentials.Failure);}}
        body.Children.Add(UI.Button(app.L("Show Key Destination"),()=>Run(()=>scope.Text=Current().CredentialTarget)));
        body.Children.Add(UI.Button(app.L("Save Key for This Service"),()=>Run(()=>{var s=Current();AssistantCredentials.Save(s,password.Password);password.Clear();scope.Text=s.CredentialTarget;status.Text=app.L("Service key saved. No request was sent.");})));
        body.Children.Add(UI.Button(app.L("Remove Key for This Service"),()=>Run(()=>{var s=Current();AssistantCredentials.Delete(s);password.Clear();scope.Text=s.CredentialTarget;status.Text=app.L("Service key removed. No request was sent.");})));
        actions.Children.Add(UI.Button(app.L("Save Service Settings"),()=>Run(()=>{app.Prefs.SaveAssistant(Current());DialogResult=true;})));var close=UI.Button(app.L("Cancel"),()=>DialogResult=false);close.IsCancel=true;actions.Children.Add(close);
        Closed+=(_,_)=>password.Clear();root.Children.Add(UI.Scroll(body));Content=root;SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}

internal sealed class AssistantDialog:Window
{
    public const string Scope="By default only your typed request, fixed built-in action IDs/titles and file-kind counts are added. Custom app/preset labels are added only if you explicitly include them for this request; labels may contain private information. File contents, names, paths and saved QR text are not read or added automatically. Anything you type is sent verbatim. Review the entire JSON and destination before sending. The model cannot run actions; it only selects a Quick Actions row for your later Return or Run Selected.";
    private Task pending=Task.CompletedTask;private CancellationTokenSource? cancellation;private bool closed;private AssistantPlan? plan;private AssistantSuggestion? suggestion;
    internal string? SelectedId{get;private set;}
    internal bool IncludeCustomSelected{get;private set;}
    public const string DoctorScope="Only the separately redacted snapshot is added: bounded numbers and known states. Names, paths, drive letters, addresses, OS descriptions, private and unknown fields are omitted. Anonymous samples do not identify apps or adapters. Review the full report and service request; your question is sent verbatim. The explanation may be wrong and never applies fixes.";
    public const string NamedDoctorScope="Only the labels you explicitly select are added to the numeric report. Reported labels are untrusted metadata and may themselves contain names, paths, addresses or other private text. Review every selected label and the complete JSON before sending. They do not identify anonymous metrics or grant permission to execute anything. Your question is sent verbatim; the explanation never applies fixes.";
    internal AssistantDialog(App app,AssistantSettings settings,AssistantChoice[] choices,string[] files,string initial,CancellationToken outer,AssistantChoice[]? customChoices=null,IReadOnlyList<DoctorFact>? doctorFacts=null,DoctorReportSnapshot? doctorSnapshot=null,DoctorLabelSet? doctorLabels=null)
    {
        if(doctorFacts is not null&&doctorSnapshot is not null)throw new ArgumentException("Choose one report snapshot.");var reportSnapshot=doctorSnapshot??(doctorFacts is null?null:DoctorAssistantReport.Capture(doctorFacts));var labelSnapshot=doctorLabels??(doctorFacts is null?null:DoctorLabels.Facts(doctorFacts));bool doctor=reportSnapshot is not null;Title=app.L(doctor?"Explain Redacted Doctor Report":"Ask Assistant");Width=840;Height=850;MinWidth=480;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new StackPanel();var status=UI.Text("");footer.Children.Add(status);var actions=new WrapPanel();footer.Children.Add(actions);DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);var body=new StackPanel();var privacy=UI.Text(app.L(doctor?DoctorScope:Scope),12);body.Children.Add(privacy);body.Children.Add(UI.Text(settings.Endpoint,12));var reportScope=doctorSnapshot is null?null:UI.Text(app.L(doctorSnapshot.SingleSample?UsageNotices.AssistantScope:DoctorAssistantSession.Scope),12);if(reportScope is not null)body.Children.Add(reportScope);
        var include=new CheckBox{Content=app.L("Include custom app and preset labels in this request (may contain private information)"),IsChecked=false,Visibility=doctor||customChoices is null?Visibility.Collapsed:Visibility.Visible};body.Children.Add(include);
        var includeLabels=new CheckBox{Content=app.L("Include only the reported labels I select for this request (may contain private text)"),IsChecked=false,Visibility=doctor&&labelSnapshot?.Rows.Count>0?Visibility.Visible:Visibility.Collapsed};body.Children.Add(includeLabels);
        var labelBody=new StackPanel{Visibility=Visibility.Collapsed};body.Children.Add(labelBody);labelBody.Children.Add(UI.Text(app.L("Use Ctrl or Shift to select 1–40 labels. The summary remains complete; labels are a selected subset and are not linked to anonymous samples. Nothing is shortened to fit."),12));
        var labelList=new ListView{Height=180,SelectionMode=SelectionMode.Extended};var labelView=new GridView();labelView.Columns.Add(new GridViewColumn{Header=app.L("Source"),DisplayMemberBinding=new Binding("Source"),Width=210});labelView.Columns.Add(new GridViewColumn{Header=app.L("Reported Label"),DisplayMemberBinding=new Binding("Text"),Width=500});labelList.View=labelView;VirtualizingPanel.SetIsVirtualizing(labelList,true);VirtualizingPanel.SetVirtualizationMode(labelList,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(labelList,true);AutomationProperties.SetName(labelList,app.L("Optional Reported Labels"));labelBody.Children.Add(labelList);
        labelList.ItemsSource=labelSnapshot?.Rows.Select((x,i)=>new DoctorLabelRow(i,app.L(x.Kind switch{DoctorLabelKind.DisplayedProcess=>"Displayed Process Label",DoctorLabelKind.ExecutableBasename=>"Reported Executable Basename",DoctorLabelKind.ReportedApp=>"Reported App Name",_=>"Reported Startup Value Name"}),x.Text)).ToArray();
        var labelCount=UI.Text("",12);labelBody.Children.Add(labelCount);
        if(doctor&&labelSnapshot is not null)body.Children.Add(UI.Text(string.Format(app.L("Compatible labels: {0}. Omitted: {1} missing, {2} invalid, {3} duplicates. Matching is ordinal and case-sensitive. Invalid includes over 160 UTF-16 units, malformed text, control/format characters or line separators. Basic process labels may already be shortened by their source report."),labelSnapshot.Rows.Count,labelSnapshot.Missing,labelSnapshot.Invalid,labelSnapshot.Duplicates),12));
        TextBox? redacted=null;if(doctor){redacted=new TextBox{Text=reportSnapshot!.Json,IsReadOnly=true,Height=180,TextWrapping=TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};AutomationProperties.SetName(redacted,app.L("Redacted Doctor Snapshot"));body.Children.Add(redacted);}
        var query=new TextBox{Text=initial,AcceptsReturn=true,Height=90,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};AutomationProperties.SetName(query,app.L("Assistant Request"));body.Children.Add(query);
        var preview=new TextBox{IsReadOnly=true,AcceptsReturn=true,Height=260,TextWrapping=TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};AutomationProperties.SetName(preview,app.L("Complete Request Preview"));body.Children.Add(preview);
        var reviewed=new CheckBox{Content=app.L("I reviewed the full JSON request and destination."),IsChecked=false};body.Children.Add(reviewed);var result=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MinHeight=80,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};AutomationProperties.SetName(result,app.L("Assistant Suggestion"));body.Children.Add(result);
        var prepare=UI.Button(app.L("Prepare Request Preview"),()=>{});var send=UI.Button(app.L("Send Reviewed Request"),()=>{});var use=UI.Button(app.L("Select Suggested Action"),()=>{if(suggestion?.ActionId is not null){SelectedId=suggestion.ActionId;IncludeCustomSelected=include.IsChecked==true;DialogResult=true;}});send.IsEnabled=use.IsEnabled=false;use.Visibility=doctor?Visibility.Collapsed:Visibility.Visible;
        var copy=UI.Button(app.L("Copy Explanation"),()=>app.Try(()=>{if(suggestion is not null)Clipboard.SetText(suggestion.Explanation);}));copy.IsEnabled=false;copy.Visibility=doctor?Visibility.Visible:Visibility.Collapsed;
        void Invalidate(){plan=null;suggestion=null;preview.Clear();result.Clear();reviewed.IsChecked=false;send.IsEnabled=use.IsEnabled=copy.IsEnabled=false;}
        include.Checked+=(_,_)=>Invalidate();include.Unchecked+=(_,_)=>Invalidate();
        void LabelChange(){Invalidate();redacted?.Clear();if(redacted is not null)AutomationProperties.SetName(redacted,app.L(includeLabels.IsChecked==true?"Doctor Report with Selected Labels":"Redacted Doctor Snapshot"));labelBody.Visibility=includeLabels.IsChecked==true?Visibility.Visible:Visibility.Collapsed;privacy.Text=app.L(includeLabels.IsChecked==true?NamedDoctorScope:DoctorScope);if(reportScope is not null)reportScope.Visibility=includeLabels.IsChecked==true?Visibility.Collapsed:Visibility.Visible;labelCount.Text=string.Format(app.L("Selected labels: {0} (maximum 40)."),labelList.SelectedItems.Count);}
        includeLabels.Checked+=(_,_)=>LabelChange();includeLabels.Unchecked+=(_,_)=>LabelChange();labelList.SelectionChanged+=(_,_)=>LabelChange();
        query.TextChanged+=(_,_)=>Invalidate();reviewed.Checked+=(_,_)=>send.IsEnabled=plan is not null&&pending.IsCompleted;reviewed.Unchecked+=(_,_)=>send.IsEnabled=false;
        prepare.Click+=(_,_)=>{try{Invalidate();if(doctor){var selectedReport=includeLabels.IsChecked==true?DoctorLabels.Include(reportSnapshot!,labelSnapshot!,labelList.SelectedItems.Cast<DoctorLabelRow>().Select(x=>x.Index).ToArray()):reportSnapshot!;plan=AssistantPlan.Explain(settings,query.Text,selectedReport);redacted!.Text=selectedReport.Json;AutomationProperties.SetName(redacted,app.L(includeLabels.IsChecked==true?"Doctor Report with Selected Labels":"Redacted Doctor Snapshot"));}else plan=AssistantPlan.Create(settings,query.Text,include.IsChecked==true?customChoices!:choices,files);preview.Text=plan.Json;status.Text=app.L("Review the entire request. A remote service needs its saved key; a literal loopback service may use no key.");}catch(Exception e){status.Text=e is UserError u?app.L(u.Message):app.L(AssistantClient.Failure);}};
        async Task Send()
        {
            if(plan is null||reviewed.IsChecked!=true)return;var snapshot=plan;var owned=cancellation=CancellationTokenSource.CreateLinkedTokenSource(outer);send.IsEnabled=prepare.IsEnabled=use.IsEnabled=copy.IsEnabled=query.IsEnabled=include.IsEnabled=includeLabels.IsEnabled=labelList.IsEnabled=reviewed.IsEnabled=false;suggestion=null;result.Clear();
            try
            {
                owned.Token.ThrowIfCancellationRequested();string? key=await Task.Run(()=>AssistantCredentials.Read(snapshot.Settings),owned.Token);owned.Token.ThrowIfCancellationRequested();var response=await AssistantClient.SendAsync(snapshot,key,owned.Token);owned.Token.ThrowIfCancellationRequested();
                if(!closed){suggestion=response;string title=doctor?app.L("Read-only report explanation"):response.ActionId is null?app.L("No offered action fits."):snapshot.Choices.Single(x=>x.Id==response.ActionId).Title;result.Text=app.L(title)+Environment.NewLine+response.Explanation;use.IsEnabled=!doctor&&response.ActionId is not null;copy.IsEnabled=doctor;status.Text=app.L(doctor?"Explanation ready. No fixes were applied.":"Suggestion ready. Selecting it does not execute it.");}
            }
            catch(OperationCanceledException){if(!closed)status.Text=app.L("Cancelled.");}
            catch(Exception e){if(!closed)status.Text=e is UserError u?app.L(u.Message):app.L(AssistantClient.Failure);}
            finally{if(ReferenceEquals(cancellation,owned))cancellation=null;owned.Dispose();if(!closed){query.IsEnabled=include.IsEnabled=includeLabels.IsEnabled=labelList.IsEnabled=prepare.IsEnabled=reviewed.IsEnabled=true;reviewed.IsChecked=false;send.IsEnabled=false;}}
        }
        send.Click+=(_,_)=>{if(pending.IsCompleted)pending=Send();};actions.Children.Add(prepare);actions.Children.Add(send);actions.Children.Add(UI.Button(app.L("Stop"),()=>cancellation?.Cancel()));actions.Children.Add(use);actions.Children.Add(copy);var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;actions.Children.Add(close);
        void FeatureChanged(){if(!app.Prefs.IsOn(Feature.Assistant)||doctor&&!app.Prefs.IsOn(Feature.Doctor)){cancellation?.Cancel();if(!closed)Close();}}
        app.Prefs.Changed+=FeatureChanged;
        root.Children.Add(UI.Scroll(body));Content=root;Closed+=(_,_)=>{closed=true;app.Prefs.Changed-=FeatureChanged;cancellation?.Cancel();};SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private sealed record DoctorLabelRow(int Index,string Source,string Text);
    internal async Task FinishAsync(){cancellation?.Cancel();await pending;}
    internal void RequestClose(){if(!closed)Close();}
}
