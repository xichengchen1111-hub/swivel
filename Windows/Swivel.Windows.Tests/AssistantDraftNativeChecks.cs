using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class AssistantDraftNativeChecks
{
    public static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"assistant-drafts");Directory.CreateDirectory(folder);
        string first=Path.Combine(folder,"a+%23 & file.txt"),second=Path.Combine(folder,"中 🦜.txt");
        File.WriteAllText(first,"PRIVATE_FILE_CONTENT_FIRST");File.WriteAllText(second,"PRIVATE_FILE_CONTENT_SECOND");
        var paths=new[]{first,second};
        using(var session=AssistantDraftSession.Open(paths,"en",default))
        {
            var held=(List<NativeFiles.Input>)typeof(AssistantDraftSession).GetField("held",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(session)!;
            check(held.Count==2&&held.All(i=>i.Stream.Position==0),"Draft preparation pins references without reading a content byte");
            reject(()=>File.Move(first,first+" moved"),"Held draft file cannot be replaced");
            reject(()=>Directory.Move(folder,folder+" moved"),"All draft parent paths remain pinned through review");
            foreach(DraftTarget target in Enum.GetValues<DraftTarget>())
            {
                var plan=session.Review(target,"en",default);session.Validate(plan,default);
                check(!plan.Text.Contains("PRIVATE_FILE_CONTENT")&&plan.Paths.SequenceEqual(paths),"Review contains only complete path references, not source contents");
                if(plan.Address is not null)
                {
                    var info=AssistantDraftSession.StartInfo(plan);
                    check(info.FileName==plan.Address&&info.UseShellExecute&&info.Verb=="open"&&info.Arguments==""&&info.ArgumentList.Count==0&&info.WorkingDirectory==Environment.SystemDirectory,"Closed immutable plan produces exactly one protocol URI, no cmd or interpolated args");
                }
                else reject(()=>AssistantDraftSession.StartInfo(plan),"Long temporary paths keep full references without a launchable URI");
                reject(()=>session.Launch(plan,new CancellationToken(true)),"Pre-cancel prevents actual protocol launch");
            }
            reject(()=>session.Validate(AssistantDraft.Build(DraftTarget.Codex,new[]{first},"en",default),default),"Plan cannot be swapped for a different held-file selection");
            var app=Application.Current as App??new App();var dialog=new AssistantDraftDialog(app,session,default);
            var boxes=Children(dialog).OfType<TextBox>().ToArray();var prompt=boxes.Single(b=>AutomationProperties.GetName(b)==app.L("Complete Draft"));
            check(dialog.Content is DockPanel&&boxes.Length==3&&boxes.All(b=>b.IsReadOnly)&&prompt.Text==dialog.Plan.Text&&!prompt.Text.Contains("PRIVATE_FILE_CONTENT"),"Native scrollable preview retains complete exact draft and readonly fields");
            var targetBox=Children(dialog).OfType<ComboBox>().Single();targetBox.SelectedIndex=1;
            check(dialog.Plan.Target==DraftTarget.ClaudeCode&&prompt.Text==dialog.Plan.Text&&(dialog.Plan.Address is null||dialog.Plan.Address.StartsWith("claude://code/new?q=",StringComparison.Ordinal)),"Provider switch updates reviewed immutable URI and full prompt together");
            var open=Children(dialog).OfType<Button>().Single(b=>Equals(b.Content,app.L("Open Draft")));check(open.IsEnabled==(dialog.Plan.Address is not null)&&Children(dialog).OfType<Button>().Any(b=>b.IsCancel),"Explicit Open reflects the current localized URI length; Close remains in the native fixed footer");
            check(held.All(i=>i.Stream.Position==0),"Provider changes and UI inspection still perform no content read");dialog.Close();
        }
        using(var disposed=AssistantDraftSession.Open(paths,"en",default))
        {var plan=disposed.Review(DraftTarget.Codex,"en",default);disposed.Dispose();reject(()=>disposed.Validate(plan,default),"Disposed session cannot copy/send stale plan");reject(()=>disposed.Launch(plan,default),"Disposed session fails before process creation");}
        reject(()=>AssistantDraftSession.Open(paths,"en",new CancellationToken(true)),"Pre-cancel precedes native source access");
        reject(()=>AssistantDraftSession.Open(new[]{first,Path.Combine(folder,"missing.txt")},"en",default),"Late preparation failure releases earlier held inputs");
        File.Move(first,first+" released");File.Move(first+" released",first);
        check(File.ReadAllText(first)=="PRIVATE_FILE_CONTENT_FIRST"&&File.ReadAllText(second)=="PRIVATE_FILE_CONTENT_SECOND","Release and review preserve both source file contents");
        var many=Enumerable.Range(0,30).Select(i=>Path.Combine(folder,$"{i:00}"+new string('a',35)+".txt")).ToArray();foreach(string p in many)File.WriteAllText(p,"private");
        using(var session=AssistantDraftSession.Open(many,"en",default))
        {
            var app=Application.Current as App??new App();var dialog=new AssistantDraftDialog(app,session,default);var plan=dialog.Plan;
            check(plan.Address is null&&plan.Paths.Count==30&&plan.Text.Split('\n').Count(l=>l.StartsWith('"'))==30,"Long URI does not drop complete path references or the copyable draft");
            var buttons=Children(dialog).OfType<Button>().ToArray();check(!buttons.Single(b=>Equals(b.Content,app.L("Open Draft"))).IsEnabled&&buttons.Single(b=>Equals(b.Content,app.L("Copy Draft"))).IsEnabled,"Only Open disabled for long URI, full Copy Draft retained");
            reject(()=>AssistantDraftSession.StartInfo(plan),"Overlong link cannot produce launch info");dialog.Close();
        }
        check(!Directory.GetFiles(folder,".swivel-*.tmp").Any(),"Draft workflow never creates output/staging files");
        Directory.Move(folder,folder+" released");check(Directory.Exists(folder+" released"),"Success/failure/disposed/cancel sessions release every input and ancestor");
        // Never click Copy/Open here: no clipboard write or real target app launch.
    }
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var item in Children(child))yield return item;}
}
