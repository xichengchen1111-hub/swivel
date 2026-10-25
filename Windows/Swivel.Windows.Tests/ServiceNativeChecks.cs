using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class ServiceNativeChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        reject(()=>ServiceReader.Read(new CancellationToken(true)),"Precancelled service read performs no local SCM operation");var native=typeof(ServiceReader).GetNestedType("NativeRecord",BindingFlags.NonPublic)!;
        check(IntPtr.Size==8&&Marshal.SizeOf(native)==56&&Marshal.OffsetOf(native,"Name").ToInt32()==0&&Marshal.OffsetOf(native,"Display").ToInt32()==8&&Marshal.OffsetOf(native,"Type").ToInt32()==16&&Marshal.OffsetOf(native,"ProcessId").ToInt32()==44&&Marshal.OffsetOf(native,"Flags").ToInt32()==48,"x64/ARM64 ENUM_SERVICE_STATUS_PROCESSW two pointers and nineDWORD alignment");
        var app=Application.Current as App??new App(validationHost:true);string previous=app.Prefs.Language;
        try
        {
            foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                app.Prefs.SetLanguage(language);var snapshot=new ServiceSnapshot(new[]{new ServiceRecord("Private\r\n\u202e","😀\ud800",0x130,uint.MaxValue,uint.MaxValue)},new[]{new ServiceReadIssue(ServiceIssuePhase.Open,0),new ServiceReadIssue(ServiceIssuePhase.BufferLimit,234,NeededBytes:uint.MaxValue),new ServiceReadIssue(ServiceIssuePhase.InvalidRecord,Record:4096),new ServiceReadIssue(ServiceIssuePhase.Close,6)},DateTimeOffset.UnixEpoch);var dialog=new ServiceDialog(app,snapshot);var root=(DockPanel)dialog.Content;
                check(root.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Count()==2,"Service snapshot only Copy/Close, no stop/start/disable/delete/reconfigure");
                check(dialog.CurrentReport.Contains("Private[U+000D][U+000A][U+202E]")&&dialog.CurrentReport.Contains("😀[U+D800]")&&dialog.CurrentReport.Contains("4294967295")&&dialog.CurrentReport.Contains("0x00000130"),"Full raw state/type/PID and visibly escaped names copied without command interpretation");
                check(dialog.CurrentReport.Contains(app.L("Windows Error Code")+" 0")&&dialog.CurrentReport.Contains(app.L("Service Manager Handle Cleanup Failed"))&&dialog.CurrentReport.Contains(app.L(ServiceRecords.Limits))&&dialog.CurrentReport.Contains(app.L(ServiceRecords.Scope)),"Partial/cleanup/error-zero and all omissions/privacy limits retained");dialog.Close();
                var empty=new ServiceDialog(app,new(Array.Empty<ServiceRecord>(),Array.Empty<ServiceReadIssue>(),default));check(empty.CurrentReport.Contains(app.L(ServiceRecords.Limits))&&empty.CurrentReport.Contains(app.L("Reported Service Rows")+": 0"),"Empty result still includes permissions omission and incomplete inventory limits");empty.Close();
            }
        }
        finally{app.Prefs.SetLanguage(previous);}
    }
}
