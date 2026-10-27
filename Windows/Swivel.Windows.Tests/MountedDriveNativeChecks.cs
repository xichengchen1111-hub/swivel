using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class MountedDriveNativeChecks
{
    internal static void Run(Action<bool,string> check,Action<Action,string> reject)
    {
        var token=new CancellationToken(true);var cancelled=MountedDriveReader.ReadAsync(token);check(cancelled.IsCanceled,"Precancelled mounted-drive check starts no worker or metadata request");
        reject(()=>MountedDriveReader.Read(token),"Precancelled synchronous drive check changes no error mode");
        var snapshot=new MountedDriveSnapshot(new[]{new MountedDrive('C',3,"NTFS",0,new(0,100,1000),null,null),new MountedDrive('D',2,"FAT32",0x80000,null,null,21),new MountedDrive('N',4,null,null,null,null,null)},new[]{new DriveIssue(DriveIssueStage.Enumeration,5)},true,DateTimeOffset.UnixEpoch);
        var app=Application.Current as App??new App(validationHost:true);string previous=app.Prefs.Language;
        try
        {
            foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                app.Prefs.SetLanguage(language);var dialog=new MountedDriveDialog(app,snapshot);var root=(DockPanel)dialog.Content;
                check(root.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Count()==2,"Drive result offers only explicit Copy/Close, no eject/write/repair");
                check(dialog.CurrentReport.Contains(@"C:\")&&dialog.CurrentReport.Contains(@"N:\")&&dialog.CurrentReport.Contains(app.L(MountedDrives.Scope))&&dialog.CurrentReport.Contains(app.L(MountedDrives.Limits)),"Full drive report includes roots/partial states and exact limited-snapshot scope");
                check(dialog.CurrentReport.Contains(app.L("Read-only Not Declared (Write Access Untested)"))&&dialog.CurrentReport.Contains(app.L("Declared Read-only"))&&dialog.CurrentReport.Contains(app.L("Capacity Query")),"Read-only declaration and capacity failure stay distinct from write permission and filesystem success");dialog.Close();
            }
        }
        finally{app.Prefs.SetLanguage(previous);}
    }
}
