using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class ConfigurationNativeChecks
{
    internal static void Run(string directory,Action<bool,string> check,Action<Action,string> reject)
    {
        reject(()=>ConfigurationPathProbe.Read(null,new CancellationToken(true)),"Precancelled path check performs no metadata access");
        foreach(string path in new[]{@"\\server\share\file.exe",@"%PRIVATE%\file.exe",@"C:\..\file.exe"})check(ConfigurationPathProbe.Read(path,default)==ConfigurationPathState.Unknown,"Unsupported diagnostic path never probes a network, variable or traversal path");
        string file=Path.Combine(directory,"config-probe.exe");File.WriteAllText(file,"ordinary metadata fixture");
        check(ConfigurationPathProbe.Read(file,default)==ConfigurationPathState.Present&&ConfigurationPathProbe.Read(file+".missing",default)==ConfigurationPathState.Missing,"Attribute-only diagnostic distinguishes present and absent local files");
        check(File.ReadAllText(file)=="ordinary metadata fixture","Diagnostic probe leaves contents unchanged");
        IntPtr data=Marshal.AllocHGlobal(ServiceReader.ConfigBytes);
        try
        {
            Marshal.Copy(new byte[ServiceReader.ConfigBytes],0,data,ServiceReader.ConfigBytes);IntPtr text=IntPtr.Add(data,128);
            Marshal.Copy("Account\0".ToCharArray(),0,text,8);check(ServiceReader.ConfigText(data,text)=="Account","Bounded service config UTF-16 pointer is copied exactly");
            reject(()=>ServiceReader.ConfigText(data,IntPtr.Subtract(data,2)),"Service config pointer before buffer refused");
            reject(()=>ServiceReader.ConfigText(data,IntPtr.Add(data,ServiceReader.ConfigBytes)),"Service config pointer beyond buffer refused");
            Marshal.WriteInt16(data,ServiceReader.ConfigBytes-2,65);reject(()=>ServiceReader.ConfigText(data,IntPtr.Add(data,ServiceReader.ConfigBytes-2)),"Unterminated config value at buffer edge refused without over-read");
        }
        finally{Marshal.FreeHGlobal(data);}
        var imports=typeof(ServiceReader).GetMethods(BindingFlags.NonPublic|BindingFlags.Static).Select(m=>m.GetCustomAttribute<DllImportAttribute>()).Where(a=>a is not null).Select(a=>a!.EntryPoint).ToArray();
        check(imports.All(name=>new[]{"OpenServiceW","QueryServiceConfigW","QueryServiceConfig2W","OpenSCManagerW","EnumServicesStatusExW","CloseServiceHandle"}.Contains(name)),"SCM surface only opens, enumerates, queries and closes");
        var before=ServiceReader.Read(default);var after=ServiceReader.Read(default);
        var first=before.Services.Where(s=>s.Configuration is not null).ToDictionary(s=>s.Name,StringComparer.OrdinalIgnoreCase);int compared=0;
        foreach(var row in after.Services.Where(s=>s.Configuration is not null))
        {
            if(!first.TryGetValue(row.Name,out var original)||original.ConfigurationErrors.Count!=0||row.ConfigurationErrors.Count!=0)continue;
            check(JsonSerializer.Serialize(original.Configuration)==JsonSerializer.Serialize(row.Configuration),"Real SCM before/after readable configuration is unchanged");compared++;
        }
        check(before.Services.Count<=ServiceRecords.MaxRecords&&after.Services.Count<=ServiceRecords.MaxRecords,"Real SCM samples preserve record bounds even under access limits");
        var tasksBefore=ScheduledTaskReader.ReadAsync(default).GetAwaiter().GetResult();
        var tasksAfter=ScheduledTaskReader.ReadAsync(default).GetAwaiter().GetResult();
        var prior=tasksBefore.Tasks.Where(t=>t.Path is not null&&t.Configuration is not null).GroupBy(t=>t.Path!,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
        int tasksCompared=0;
        foreach(var row in tasksAfter.Tasks.Where(t=>t.Path is not null&&t.Configuration is not null))
        {
            if(!prior.TryGetValue(row.Path!,out var original))continue;
            check(original.Configuration!.Fingerprint==row.Configuration!.Fingerprint,"Real Task Scheduler before/after complete XML configuration is unchanged");tasksCompared++;
        }
        // No named service/task or enabled history is assumed on a clean runner. Partial inventories remain valid.
        Console.WriteLine($"F09 live unchanged configuration comparisons: {compared} services, {tasksCompared} tasks; omissions and external concurrent configuration changes remain possible.");
        var app=Application.Current as App??throw new InvalidOperationException("Validation App required.");string language=app.Prefs.Language;
        try
        {
            foreach(string choice in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                app.Prefs.SetLanguage(choice);
                var service=new ServiceRecord("Visible","Display",0x10,1,0){Configuration=new(2,"PRIVATE_ACCOUNT",@"C:\PRIVATE_PATH\a.exe",new[]{"PRIVATE_DEP"}){Description="PRIVATE_DESC",FailureCommand="PRIVATE_COMMAND",RebootMessage="PRIVATE_MESSAGE"}};
                var dialog=new ServiceDialog(app,new(new[]{service},Array.Empty<ServiceReadIssue>(),default));
                try
                {
                    var body=(StackPanel)((ScrollViewer)((DockPanel)dialog.Content).Children.OfType<ScrollViewer>().Single()).Content;
                    var reveal=body.Children.OfType<CheckBox>().Single();var tabs=body.Children.OfType<TabControl>().Single();var list=(ListView)((TabItem)tabs.Items[0]).Content;list.SelectedIndex=0;
                    var details=body.Children.OfType<TextBox>().Single();
                    check(!dialog.CurrentReport.Contains("PRIVATE")&&!details.Text.Contains("PRIVATE")&&!list.Items[0].ToString()!.Contains("PRIVATE"),"Service configuration hidden across report/details/accessibility by default");
                    reveal.IsChecked=true;check(dialog.CurrentReport.Contains("PRIVATE_COMMAND")&&details.Text.Contains("PRIVATE_ACCOUNT")&&list.Items[0].ToString()!.Contains("PRIVATE_PATH"),"Explicit service reveal updates every surface");
                    reveal.IsChecked=false;check(!dialog.CurrentReport.Contains("PRIVATE")&&!details.Text.Contains("PRIVATE"),"Hiding service configuration removes previously revealed secrets from copy/details");
                }
                finally{dialog.Close();}
                var config=TaskConfiguration.Parse("<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Triggers/><Principals><Principal><UserId>PRIVATE_PRINCIPAL</UserId></Principal></Principals><Actions><Exec><Command>C:\\PRIVATE_PATH\\a.exe</Command><Arguments>PRIVATE_ARGUMENTS</Arguments></Exec></Actions></Task>",default);
                var task=new ScheduledTaskRow(1,"Visible",@"\PRIVATE_NAMESPACE",3,true,0x80070002){Configuration=config,HistoricalFailureInstances=2};
                var taskDialog=new ScheduledTaskDialog(app,new(new[]{task},Array.Empty<ScheduledTaskFolder>(),Array.Empty<ScheduledTaskIssue>(),default,TimeSpan.Zero,0,1,1024,0,ScheduledTaskStopReason.Completed));
                try
                {
                    var body=(StackPanel)((ScrollViewer)((DockPanel)taskDialog.Content).Children.OfType<ScrollViewer>().Single()).Content;
                    var reveal=body.Children.OfType<CheckBox>().Single();var tabs=body.Children.OfType<TabControl>().Single();var list=(ListView)((TabItem)tabs.Items[0]).Content;list.SelectedIndex=0;
                    var details=body.Children.OfType<TextBox>().Single();
                    check(!taskDialog.CurrentReport.Contains("PRIVATE")&&!taskDialog.SelectedReport!.Contains("PRIVATE")&&!details.Text.Contains("PRIVATE")&&!list.Items[0].ToString()!.Contains("PRIVATE"),"Task secrets hidden across list/accessibility/details/full/selected copy including findings");
                    reveal.IsChecked=true;check(taskDialog.CurrentReport.Contains("PRIVATE_ARGUMENTS")&&taskDialog.SelectedReport!.Contains("PRIVATE_PRINCIPAL")&&details.Text.Contains("PRIVATE_PATH"),"Task explicit reveal covers arguments/principal/action paths");
                    reveal.IsChecked=false;check(!taskDialog.CurrentReport.Contains("PRIVATE")&&!taskDialog.SelectedReport!.Contains("PRIVATE")&&!details.Text.Contains("PRIVATE"),"Hiding task configuration refreshes retained selection and every copy");
                }
                finally{taskDialog.Close();}
            }
        }
        finally{app.Prefs.SetLanguage(language);}
    }
}
