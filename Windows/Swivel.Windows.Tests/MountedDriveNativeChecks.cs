using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        var actual=MountedDriveReader.ReadAsync(default).GetAwaiter().GetResult();
        char system=char.ToUpperInvariant(Environment.GetFolderPath(Environment.SpecialFolder.Windows)[0]);
        check(actual.Drives.Any(d=>d.Letter==system)&&actual.Drives.Where(d=>d.Letter==system).All(d=>!DriveEject.Eligible(d)),"Real mounted system drive is listed and never eligible for eject");
        string? windowsVolume=DriveDeviceReader.WindowsVolume();
        var systemDisk=windowsVolume is null?null:DriveDeviceReader.ResolveDisk(windowsVolume,default);
        check(systemDisk is {Number.Type:7,Instance.Length:>0}&&!systemDisk.Instance.StartsWith(@"STORAGE\Volume",StringComparison.OrdinalIgnoreCase),"Real Windows volume resolves through its single extent and storage device number to a disk interface instance");
        check(actual.Drives.Single(d=>d.Letter==system).Protected==true,"Real Windows volume stays explicitly protected after disk interface resolution");
        var eligible=new MountedDrive('E',3,"NTFS",0,null,null,null){Label="USB label",Hardware=new(7,false,"Vendor","Product",true,null),VolumeName="volume",Identity=new("volume","instance","ancestor",1,1),Protected=false};
        var cleared=DriveDeviceReader.Read(eligible with{Type=4},default);
        check(cleared.Identity is null&&cleared.VolumeName is null&&cleared.Protected is null&&cleared.Hardware is null,"Every device reread clears cached eligibility before early returns");
        var fake=new Fake(eligible);int confirmations=0;
        Task<bool> Confirm(MountedDrive drive){confirmations++;check(drive.Root==eligible.Root&&drive.Label==eligible.Label,"Eject confirmation names drive and label");return Task.FromResult(true);}
        check(DriveEject.RunAsync(eligible,fake,_=>Task.FromResult(false),default).GetAwaiter().GetResult().Status==DriveEjectStatus.Cancelled&&fake.Requests==0,"Fake: declining confirmation cannot invoke eject");
        var veto=DriveEject.RunAsync(eligible,fake,Confirm,default).GetAwaiter().GetResult();check(veto.Status==DriveEjectStatus.Vetoed&&veto.Veto==3&&veto.VetoName=="TestApp"&&fake.Requests==1&&confirmations==1,"Fake: confirmed request reports program veto and name");
        fake.Current=eligible with{Identity=eligible.Identity! with{Disk=2}};
        check(DriveEject.RunAsync(eligible,fake,Confirm,default).GetAwaiter().GetResult().Status==DriveEjectStatus.Changed&&fake.Requests==1,"Fake: changed device identity never reaches eject");
        var snapshot=new MountedDriveSnapshot(new[]{new MountedDrive('C',3,"NTFS",0,new(0,100,1000),null,null),new MountedDrive('D',2,"FAT32",0x80000,null,null,21){Label="Bad\n\u202Elabel",Hardware=new(7,true,"Bad\nVendor","Product",false,19)},new MountedDrive('N',4,null,null,null,null,null)},new[]{new DriveIssue(DriveIssueStage.Enumeration,5)},true,DateTimeOffset.UnixEpoch);
        var app=Application.Current as App??new App(validationHost:true);string previous=app.Prefs.Language;
        try
        {
            foreach(string language in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                app.Prefs.SetLanguage(language);var dialog=new MountedDriveDialog(app,snapshot);var root=(DockPanel)dialog.Content;
                check(root.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Count()==2,"Drive footer keeps Copy/Close; per-drive eject is separate");
                check(dialog.CurrentReport.Contains(@"C:\")&&dialog.CurrentReport.Contains(@"N:\")&&dialog.CurrentReport.Contains(app.L(MountedDrives.Scope))&&dialog.CurrentReport.Contains(app.L(MountedDrives.Limits)),"Full drive report includes roots/partial states and exact limited-snapshot scope");
                check(dialog.CurrentReport.Contains(app.L("Read-only Not Declared (Write Access Untested)"))&&dialog.CurrentReport.Contains(app.L("Declared Read-only"))&&dialog.CurrentReport.Contains(app.L("Capacity Query")),"Read-only declaration and capacity failure stay distinct from write permission and filesystem success");
                check(dialog.CurrentReport.Contains("[U+000A]")&&dialog.CurrentReport.Contains("[U+202E]")&&dialog.CurrentReport.Contains("USB"),"Device labels/vendor text is sanitized and the bus is plainly named");dialog.Close();dialog.FinishAsync().GetAwaiter().GetResult();
            }
        }
        finally{app.Prefs.SetLanguage(previous);}
    }
    private sealed class Fake(MountedDrive drive):IDriveEjectSource
    {
        internal MountedDrive Current=drive;internal int Requests;
        public Task<MountedDriveSnapshot> ReadAsync(CancellationToken token)=>Task.FromResult(new MountedDriveSnapshot(new[]{Current},Array.Empty<DriveIssue>(),false,default));
        public Task<DriveEjectReply> RequestAsync(MountedDrive d,CancellationToken token){Requests++;return Task.FromResult(new DriveEjectReply(false,3,"TestApp"));}
    }
}
