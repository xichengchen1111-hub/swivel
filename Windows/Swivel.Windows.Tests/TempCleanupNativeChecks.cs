using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Swivel.Core;
using Swivel.Windows;

internal static class TempCleanupNativeChecks
{
    public static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        string folder=Path.Combine(directory,"temp-cleanup");Directory.CreateDirectory(folder);
        DateTime old=DateTime.UtcNow.AddDays(-30);
        string oldFile=Path.Combine(folder,"old.tmp"),newFile=Path.Combine(folder,"new.tmp"),oldDir=Path.Combine(folder,"oldDir"),touchedDir=Path.Combine(folder,"touchedDir");
        string copied=Path.Combine(folder,"copied.exe");
        File.WriteAllText(oldFile,"old");Backdate(oldFile,old,false);File.WriteAllText(newFile,"new");
        File.WriteAllText(copied,"copied");File.SetLastWriteTimeUtc(copied,old); // like a copy or extraction: old modified date, new creation and change
        Directory.CreateDirectory(oldDir);string inner=Path.Combine(oldDir,"inner.tmp");File.WriteAllText(inner,"inner");Backdate(inner,old,false);Backdate(oldDir,old,true);
        Directory.CreateDirectory(touchedDir);string fresh=Path.Combine(touchedDir,"fresh.tmp");File.WriteAllText(fresh,"fresh");Backdate(touchedDir,old,true);
        FolderScanResult scan;using(var root=NativeFiles.MetadataDirectory.Open(folder))scan=FolderSizes.Scan(root,CancellationToken.None);
        var plan=TempCleanup.Plan(scan,DateTime.UtcNow);
        check(plan.Old.Select(o=>o.Name).OrderBy(n=>n,StringComparer.Ordinal).SequenceEqual(new[]{"old.tmp","oldDir"})&&plan.Recent==3,"Native directory records give creation, write and change times: only items untouched for 7 days, inside and out, are listed, and a file copied in today with an old modified date is not");
        var file=plan.Old.Single(o=>o.Name=="old.tmp");var dir=plan.Old.Single(o=>o.Name=="oldDir");
        reject(()=>NativeFiles.RecycleTempItem(oldFile,false,file.LastWrite+1,file.Bytes,plan.Cutoff),"Temp cleanup refuses a file whose modified time differs from the listing");
        reject(()=>NativeFiles.RecycleTempItem(oldFile,false,file.LastWrite,file.Bytes+1,plan.Cutoff),"Temp cleanup refuses a file whose size differs from the listing");
        string late=Path.Combine(oldDir,"late.tmp");File.WriteAllText(late,"late");Backdate(oldDir,old,true);
        reject(()=>NativeFiles.RecycleTempItem(oldDir,true,dir.LastWrite,dir.Bytes,plan.Cutoff),"Temp cleanup measures a folder again and refuses one with something written inside recently");
        File.Delete(late);Backdate(oldDir,old,true);
        check(File.Exists(oldFile)&&File.Exists(inner),"Refused temporary items stay in place");
        var app=Application.Current as App??new App();var dialog=new TempCleanupDialog(app,folder,scan,DateTime.UtcNow);
        var buttons=((DockPanel)dialog.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();var move=buttons.Single(b=>Equals(b.Content,app.L("Move Selected to Recycle Bin…")));
        var list=((StackPanel)((ScrollViewer)((DockPanel)dialog.Content).Children.OfType<ScrollViewer>().Single()).Content).Children.OfType<ListView>().Single();
        check(!move.IsEnabled&&list.Items.Count==2&&list.SelectedItems.Count==0&&buttons.Last().IsCancel,"Old Temporary Files lists the old items with nothing selected and Move disabled");
        list.SelectAll();check(move.IsEnabled,"Selecting listed items enables the move");check(dialog.CurrentReport.Contains(oldFile)&&!dialog.CurrentReport.Contains("new.tmp")&&!dialog.CurrentReport.Contains("copied.exe"),"Report lists old items only");dialog.Close();
        if(!RecycleBin.Available(oldFile)){Console.WriteLine("SKIP: no Recycle Bin on the test drive.");return;}
        string storedFile=NativeFiles.RecycleTempItem(oldFile,false,file.LastWrite,file.Bytes,plan.Cutoff),storedDir=NativeFiles.RecycleTempItem(oldDir,true,dir.LastWrite,dir.Bytes,plan.Cutoff);
        check(!File.Exists(oldFile)&&!Directory.Exists(oldDir)&&File.Exists(newFile)&&storedDir.Contains("$Recycle.Bin",StringComparison.OrdinalIgnoreCase),"Old temporary file and folder move to the Recycle Bin; recent items stay");
        NativeFiles.RestoreRecycled(oldFile,storedFile,false);NativeFiles.RestoreRecycled(oldDir,storedDir,true);
        check(File.ReadAllText(oldFile)=="old"&&File.ReadAllText(inner)=="inner","Undo restores the temporary file and folder");
    }

    // Sets creation, last access, last write and change time, as a file left alone for long would have.
    private static void Backdate(string path,DateTime when,bool directory)
    {
        using var handle=CreateFile(path,0x100,7,IntPtr.Zero,3,directory?0x02000000u:0u,IntPtr.Zero); // FILE_WRITE_ATTRIBUTES
        if(handle.IsInvalid)throw new IOException("Could not open "+path);
        long time=when.ToFileTimeUtc();var info=new byte[40];foreach(int offset in new[]{0,8,16,24})BitConverter.GetBytes(time).CopyTo(info,offset);
        if(!SetFileInformationByHandle(handle,0,info,(uint)info.Length))throw new IOException("Could not set times on "+path);
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,byte[] info,uint size);
}
