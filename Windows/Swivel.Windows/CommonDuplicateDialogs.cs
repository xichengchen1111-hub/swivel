using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Swivel.Core;

namespace Swivel.Windows;

internal sealed class CommonDuplicateConfirmation : Window
{
    internal CommonDuplicateConfirmation(App app, IReadOnlyList<CommonFolderChoice> selection)
    {
        Title=app.L("Compare Common Folders…");Width=860;Height=740;MinWidth=500;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Start Comparison"),()=>DialogResult=true));var cancel=UI.Button(app.L("Cancel"),()=>DialogResult=false);cancel.IsCancel=true;footer.Children.Add(cancel);
        var body=new StackPanel();root.Children.Add(UI.Scroll(body));Content=root;
        body.Children.Add(UI.Text(app.L("Read file contents across these selected common folders?"),18));
        foreach(var choice in selection){body.Children.Add(UI.Text(app.L(CommonFolders.Name(choice.Kind)),14));body.Children.Add(new TextBox{Text=CacheLocations.VisiblePath(choice.Path!),IsReadOnly=true,TextWrapping=TextWrapping.Wrap});}
        foreach(string text in new[]{CommonDuplicates.Scope,CommonDuplicates.Limits,CommonDuplicates.Snapshot})body.Children.Add(UI.Text(app.L(text),12));
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
}

internal sealed class CommonDuplicatesDialog : Window
{
    internal const int CopyLimit=4*1024*1024;
    private readonly App app;private readonly CommonDuplicateResult result;private readonly Dictionary<CommonFolderKind,CommonDuplicateRootResult> roots;
    private readonly List<IssueRow> issues=new();private readonly ListView groups=NativeList(230),paths=NativeList(200);
    private readonly TextBlock selectedHash;private readonly Button copyGroup,copyPath;
    internal string CurrentReport=>Report(null);
    internal CommonDuplicatesDialog(App app,CommonDuplicateResult result)
    {
        this.app=app;this.result=result;roots=result.Roots.ToDictionary(r=>r.Choice.Kind);
        Title=app.L("Common Folder Duplicate Results");Width=1000;Height=830;MinWidth=540;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        foreach(var rootResult in result.Roots)
        {
            if(rootResult.Issue is string issue)issues.Add(new(this,rootResult.Choice.Kind,"",issue));
            if(rootResult.Scan is not {} scan)continue;
            void Visit(FolderSizeNode node,string relative)
            {
                if(node.Issue is string reason)issues.Add(new(this,rootResult.Choice.Kind,relative,reason));
                else if(node.Skipped>node.Children.Sum(c=>c.Skipped))issues.Add(new(this,rootResult.Choice.Kind,relative,"Folder enumeration was incomplete"));
                foreach(var child in node.Children)Visit(child,relative.Length==0?child.Name:relative+"\\"+child.Name);
            }
            Visit(scan.Root,"");
        }
        foreach(var issue in result.Duplicates.Issues)
        {
            if(!result.Sources.TryGetValue(issue.RelativePath,out var source))throw new UserError("Unavailable or unsupported");
            issues.Add(new(this,source.Kind,source.RelativePath,issue.Reason));
        }
        var panel=new DockPanel{Margin=new Thickness(20)};var footer=new WrapPanel();DockPanel.SetDock(footer,Dock.Bottom);panel.Children.Add(footer);
        footer.Children.Add(UI.Button(app.L("Copy Report"),()=>app.Try(()=>Clipboard.SetText(CurrentReport))));
        copyGroup=UI.Button(app.L("Copy Selected Group"),()=>{if(groups.SelectedItem is GroupRow row)app.Try(()=>Clipboard.SetText(Report(row)));});copyGroup.IsEnabled=false;footer.Children.Add(copyGroup);
        copyPath=UI.Button(app.L("Copy Selected Path"),()=>{if(paths.SelectedItem is PathRow row)app.Try(()=>Clipboard.SetText(row.Path));});copyPath.IsEnabled=false;footer.Children.Add(copyPath);
        var close=UI.Button(app.L("Close"),()=>DialogResult=false);close.IsCancel=true;footer.Children.Add(close);
        var tabs=new TabControl();panel.Children.Add(tabs);Content=panel;
        var rootBody=new StackPanel{Margin=new Thickness(12)};var rootList=NativeList(230);rootList.View=Columns(("Folder","Name",150),("Status","Status",290),("Metadata Entries","Entries",130));rootList.ItemsSource=result.Roots.Select(r=>new RootRow(this,r)).ToArray();AutomationProperties.SetName(rootList,app.L("Selected Roots"));
        foreach(string text in new[]{CommonDuplicates.Scope,CommonDuplicates.Limits,CommonDuplicates.Snapshot})rootBody.Children.Add(UI.Text(app.L(text),12));rootBody.Children.Add(rootList);
        var detail=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=240,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};rootList.SelectionChanged+=(_,_)=>detail.Text=rootList.SelectedItem is RootRow row?RootDetail(row.Result):"";rootBody.Children.Add(detail);AddTab(tabs,"Selected Roots",rootBody);
        var matches=new StackPanel{Margin=new Thickness(12)};matches.Children.Add(UI.Text(Summary(),14));matches.Children.Add(UI.Text(app.L(CommonDuplicates.Snapshot),12));matches.Children.Add(UI.Text(app.L(CommonDuplicates.Privacy),12));
        if(result.Duplicates.Groups.Count==0)matches.Children.Add(UI.Text(app.L("No duplicate groups in the compared files. Skipped or unexamined files may still contain duplicates.")));
        groups.View=Columns(("Group","Label",90),("Files","Count",65),("Bytes Per File","Size",150),("First File","First",580));groups.ItemsSource=result.Duplicates.Groups.Select((g,i)=>new GroupRow(this,g,i+1)).ToArray();AutomationProperties.SetName(groups,app.L("Matching Groups"));matches.Children.Add(groups);
        selectedHash=UI.Text("",12);matches.Children.Add(selectedHash);paths.View=Columns(("Full Path","Path",870));AutomationProperties.SetName(paths,app.L("Files in Selected Group"));matches.Children.Add(paths);
        groups.SelectionChanged+=(_,_)=>{copyGroup.IsEnabled=groups.SelectedItem is GroupRow;copyPath.IsEnabled=false;paths.ItemsSource=null;selectedHash.Text="";if(groups.SelectedItem is not GroupRow row)return;selectedHash.Text=app.L("SHA-256")+": "+row.Group.Sha256;paths.ItemsSource=row.Group.Paths.Select(key=>new PathRow(this,key)).ToArray();};paths.SelectionChanged+=(_,_)=>copyPath.IsEnabled=paths.SelectedItem is PathRow;
        AddTab(tabs,"Matching Groups",matches);
        var skipped=new StackPanel{Margin=new Thickness(12)};skipped.Children.Add(UI.Text(Summary(),14));skipped.Children.Add(UI.Text(app.L(CommonDuplicates.Limits),12));skipped.Children.Add(UI.Text(string.Format(app.L("Recorded skipped items: {0}"),issues.Count),14));
        if(issues.Count==0)skipped.Children.Add(UI.Text(app.L("No recorded skipped items.")));var issueList=NativeList(420);issueList.View=Columns(("Full Path","Path",540),("Reason","Reason",350));issueList.ItemsSource=issues;AutomationProperties.SetName(issueList,app.L("Skipped Items"));skipped.Children.Add(issueList);AddTab(tabs,"Skipped Items",skipped);
        SourceInitialized+=(_,_)=>WebRouteLayout.Fit(this,Owner??this);DpiChanged+=(_,_)=>WebRouteLayout.Fit(this,this);
    }
    private string Summary()=>string.Format(app.L("Matching groups: {0} · candidates: {1} · fully hashed files: {2} · read budget used: {3:N0} bytes"),result.Duplicates.Groups.Count,result.Duplicates.Candidates,result.Duplicates.Compared,result.Duplicates.BytesRead)
        +(result.Duplicates.ByteLimit?" · "+app.L("Comparison read limit"):"")+(result.Duplicates.TimeLimit?" · "+app.L("Comparison time limit"):"");
    private string RootStatus(CommonDuplicateRootResult root)=>app.L(root.Issue??(root.Scan!.Root.Incomplete?"Partial":"Counted"))+(root.Error is uint code?" · "+app.L("Windows Error Code")+" "+code.ToString(CultureInfo.InvariantCulture):"");
    private string RootDetail(CommonDuplicateRootResult root)
    {
        var text=new StringBuilder(app.L(CommonFolders.Name(root.Choice.Kind))+Environment.NewLine+CacheLocations.VisiblePath(root.Choice.Path!)+Environment.NewLine+RootStatus(root)+Environment.NewLine);
        text.AppendLine(string.Format(app.L("Allocated metadata entries: {0} · shared-clock deadline: {1} seconds"),root.EntryQuota,root.DeadlineSeconds));
        if(root.Scan is {} scan)
        {
            text.AppendLine(string.Format(app.L("Logical bytes: {0:N0} · counted files: {1} · skipped items/subtrees: {2}"),scan.Root.Bytes,scan.Root.Files,scan.Root.Skipped));
            text.AppendLine(app.L("Metadata Entries")+": "+scan.Entries.ToString(CultureInfo.CurrentCulture));if(scan.EntryLimit)text.AppendLine(app.L("Entry limit reached"));if(scan.TimeLimit)text.AppendLine(app.L("Folder scan time limit reached"));
        }
        text.Append(app.L("Result Recorded")).Append(": ").Append(root.Recorded.ToString("g",CultureInfo.CurrentCulture));return text.ToString();
    }
    private string FullPath(string key)
    {
        if(!result.Sources.TryGetValue(key,out var source))throw new UserError("Unavailable or unsupported");
        return DisplayPath(source.Kind,source.RelativePath);
    }
    private string DisplayPath(CommonFolderKind kind,string relative)=>CacheLocations.VisiblePath(roots[kind].Choice.Path!)+(relative.Length==0?"":"\\"+string.Join("\\",relative.Split('\\').Select(CacheLocations.VisiblePath)));
    private string Report(GroupRow? selected)
    {
        var text=new CopyText();text.Line(app.L("Windows Doctor")+" · "+Title);text.Line(Summary());
        foreach(string key in new[]{CommonDuplicates.Scope,CommonDuplicates.Limits,CommonDuplicates.Snapshot,CommonDuplicates.Privacy})text.Line(app.L(key));
        foreach(var root in result.Roots){text.Line("");text.Line(RootDetail(root));}
        void Group(DuplicateGroup group,int index)
        {
            text.Line(string.Format(app.L("Group {0}: {1} files · {2:N0} bytes each · {3:N0} logical repeat bytes"),index,group.Paths.Count,group.Length,(decimal)group.Length*Math.Max(0,group.Paths.Count-1)));
            text.Line(app.L("SHA-256")+": "+group.Sha256);foreach(string member in group.Paths)text.Line(FullPath(member));
        }
        if(selected is not null)Group(selected.Group,selected.Index);
        else
        {
            if(result.Duplicates.Groups.Count==0)text.Line(app.L("No duplicate groups in the compared files. Skipped or unexamined files may still contain duplicates."));
            for(int i=0;i<result.Duplicates.Groups.Count;i++)Group(result.Duplicates.Groups[i],i+1);
            text.Line(app.L("Skipped Items"));if(issues.Count==0)text.Line(app.L("No recorded skipped items."));foreach(var issue in issues)text.Line(issue.Path+" · "+issue.Reason);
        }
        return text.ToString();
    }
    private sealed class CopyText
    {
        private readonly StringBuilder value=new();
        internal void Line(string line)
        {
            if(line.Length>CopyLimit-Environment.NewLine.Length-value.Length)throw new UserError("This copy exceeds the complete-report limit. Select one smaller group or one file path.");
            value.Append(line).Append(Environment.NewLine);
        }
        public override string ToString()=>value.ToString();
    }
    private void AddTab(TabControl tabs,string title,StackPanel body)=>tabs.Items.Add(new TabItem{Header=app.L(title),Content=UI.Scroll(body),Padding=new Thickness(12,7,12,7)});
    private GridView Columns(params(string Title,string Property,double Width)[] columns){var view=new GridView();foreach(var c in columns)view.Columns.Add(new GridViewColumn{Header=app.L(c.Title),DisplayMemberBinding=new Binding(c.Property),Width=c.Width});return view;}
    private static ListView NativeList(double height){var list=new ListView{Height=height,SelectionMode=SelectionMode.Single};VirtualizingPanel.SetIsVirtualizing(list,true);VirtualizingPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(list,true);return list;}
    private sealed record RootRow(CommonDuplicatesDialog Owner,CommonDuplicateRootResult Result){public string Name=>Owner.app.L(CommonFolders.Name(Result.Choice.Kind));public string Status=>Owner.RootStatus(Result);public string Entries=>Result.Scan?.Entries.ToString(CultureInfo.CurrentCulture)??"—";}
    private sealed record GroupRow(CommonDuplicatesDialog Owner,DuplicateGroup Group,int Index){public string Label=>string.Format(Owner.app.L("Group {0}"),Index);public int Count=>Group.Paths.Count;public string Size=>Group.Length.ToString("N0",CultureInfo.CurrentCulture);public string First=>Group.Paths.Count==0?"":Owner.FullPath(Group.Paths[0]);}
    private sealed record PathRow(CommonDuplicatesDialog Owner,string Key){public string Path=>Owner.FullPath(Key);}
    private sealed record IssueRow(CommonDuplicatesDialog Owner,CommonFolderKind Kind,string Relative,string Key){public string Path=>Owner.DisplayPath(Kind,Relative);public string Reason=>Owner.app.L(Key);}
}
