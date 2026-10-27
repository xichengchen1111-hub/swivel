using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;
using Swivel.Windows;

internal static class RenameChecks
{
    internal static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        if (new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS") { Console.WriteLine("SKIP: Batch Rename native cases require NTFS; other filesystems are explicitly unsupported."); return; }
        string a=Path.Combine(directory,"rename-a.txt"), b=Path.Combine(directory,"rename-b.txt");
        File.WriteAllText(a,"original A"); File.WriteAllText(b,"original B");
        var paths = new[] { a,b }; var rules = new RenameRules(Prefix:"new-");
        var plan = rules.Build(paths); RenameOutcome result;
        using (var session = RenameSession.Open(paths,CancellationToken.None))
        {
            reject(()=>File.WriteAllText(a,"changed"),"Rename lease blocks source writes");
            reject(()=>File.Move(a,a+".other"),"Rename lease blocks external replacement");
            result=session.Execute(plan,CancellationToken.None);
            check(result.Error is null && result.Remaining.Count==2,"Native rename successful");
        }
        check(File.ReadAllText(plan[0].Destination)=="original A" && File.ReadAllText(plan[1].Destination)=="original B","Native rename preserves contents");
        using (var undo=RenameSession.ForUndo(result.Remaining,CancellationToken.None))
        {
            var restored=undo.Execute(result.Remaining.Select(x=>new RenamePair(x.Current,x.Original)).ToArray(),CancellationToken.None);
            check(restored.Error is null && File.Exists(a) && File.Exists(b),"Native identity-checked undo restores names");
        }
        File.WriteAllText(plan[1].Destination,"unrelated target");
        using (var session=RenameSession.Open(paths,CancellationToken.None))
            reject(()=>session.Execute(plan,CancellationToken.None),"All destinations preflight before first rename");
        check(File.Exists(a) && File.Exists(b) && !File.Exists(plan[0].Destination) && File.ReadAllText(plan[1].Destination)=="unrelated target","Conflict never changes first source or unrelated target");
        File.Delete(plan[1].Destination);
        var swap = new[] { new RenamePair(a,b), new RenamePair(b,a) };
        using (var session=RenameSession.Open(paths,CancellationToken.None)) result=session.Execute(swap,CancellationToken.None);
        check(result.Error is null && File.ReadAllText(a)=="original B" && File.ReadAllText(b)=="original A","Native swap preserves both streams");
        using (var undo=RenameSession.ForUndo(result.Remaining,CancellationToken.None))
            check(undo.Execute(result.Remaining.Select(x=>new RenamePair(x.Current,x.Original)).ToArray(),CancellationToken.None).Error is null,"Native swap undo uses identities");
        string upper=Path.Combine(directory,"RENAME-A.txt");
        using (var session=RenameSession.Open(new[] { a },CancellationToken.None)) result=session.Execute(new[] { new RenamePair(a,upper) },CancellationToken.None);
        check(result.Error is null && Directory.GetFiles(directory).Contains(upper,StringComparer.Ordinal),"Native case-only rename changes stored spelling");
        using (var undo=RenameSession.ForUndo(result.Remaining,CancellationToken.None))
            check(undo.Execute(new[] { new RenamePair(upper,a) },CancellationToken.None).Error is null,"Native case-only undo restores spelling");
        using (var session=RenameSession.Open(paths,CancellationToken.None))
        {
            using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
            reject(()=>session.Execute(plan,cancelled.Token),"Cancelled native batch refused");
        }
        using (var session=RenameSession.Open(paths,CancellationToken.None)) result=session.Execute(plan,CancellationToken.None);
        File.AppendAllText(plan[0].Destination,"edited");
        reject(()=>RenameSession.ForUndo(result.Remaining,CancellationToken.None).Dispose(),"Undo refuses edited contents by identity/size/time before changes");
        check(File.Exists(plan[0].Destination) && File.Exists(plan[1].Destination),"Undo mismatch leaves whole batch in place");
        File.Delete(plan[0].Destination); File.WriteAllText(plan[0].Destination,"replacement");
        reject(()=>RenameSession.ForUndo(result.Remaining,CancellationToken.None).Dispose(),"Undo refuses replaced file");
        string readonlyFile=Path.Combine(directory,"readonly-rename.txt"); File.WriteAllText(readonlyFile,""); File.SetAttributes(readonlyFile,FileAttributes.ReadOnly);
        try { reject(()=>NativeFiles.OpenRename(readonlyFile).Dispose(),"Read-only rename refused"); }
        finally { File.SetAttributes(readonlyFile,FileAttributes.Normal); }
        string hard=Path.Combine(directory,"hardlink-rename.txt");
        if (!CreateHardLink(hard,readonlyFile,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        reject(()=>NativeFiles.OpenRename(hard).Dispose(),"Hard link rename refused"); File.Delete(hard);
        check(!Directory.GetFiles(directory,".swivel-rename-*").Any(),"Rename never leaves staging originals");
    }
    [DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool CreateHardLink(string newName,string existing,IntPtr security);
}
