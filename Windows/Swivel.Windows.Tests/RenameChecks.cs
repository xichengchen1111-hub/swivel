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
        // A read-only file is renamed and stays read-only; undo puts its name back.
        string readonlyFile=Path.Combine(directory,"readonly-rename.txt"), readonlyNew=Path.Combine(directory,"ro-renamed.txt");
        File.WriteAllText(readonlyFile,"ro"); File.SetAttributes(readonlyFile,FileAttributes.ReadOnly);
        try
        {
            RenameOutcome ro;
            using (var session=RenameSession.Open(new[] { readonlyFile },CancellationToken.None)) ro=session.Execute(new[] { new RenamePair(readonlyFile,readonlyNew) },CancellationToken.None);
            check(ro.Error is null && File.Exists(readonlyNew) && !File.Exists(readonlyFile) && (File.GetAttributes(readonlyNew)&FileAttributes.ReadOnly)!=0 && File.ReadAllText(readonlyNew)=="ro","A read-only file is renamed and stays read-only: "+ro.Error?.Message);
            using (var undo=RenameSession.ForUndo(ro.Remaining,CancellationToken.None))
                check(undo.Execute(ro.Remaining.Select(x=>new RenamePair(x.Current,x.Original)).ToArray(),CancellationToken.None).Error is null && File.Exists(readonlyFile),"Undo restores the read-only file's name");
        }
        finally { foreach (string f in new[] { readonlyFile, readonlyNew }) if (File.Exists(f)) File.SetAttributes(f,FileAttributes.Normal); }
        // A hard link: only this name changes; the other name and the shared content stay.
        string hard=Path.Combine(directory,"hardlink-rename.txt"), hardNew=Path.Combine(directory,"hardlink-renamed.txt");
        if (!CreateHardLink(hard,readonlyFile,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        RenameOutcome linked;
        using (var session=RenameSession.Open(new[] { hard },CancellationToken.None)) linked=session.Execute(new[] { new RenamePair(hard,hardNew) },CancellationToken.None);
        check(linked.Error is null && File.Exists(hardNew) && !File.Exists(hard) && File.Exists(readonlyFile) && File.ReadAllText(hardNew)=="ro","A hard link is renamed; its other name stays: "+linked.Error?.Message);
        using (var undo=RenameSession.ForUndo(linked.Remaining,CancellationToken.None))
            check(undo.Execute(linked.Remaining.Select(x=>new RenamePair(x.Current,x.Original)).ToArray(),CancellationToken.None).Error is null && File.Exists(hard),"Undo restores the hard link's name");
        reject(()=>RenameSession.Open(new[] { hard, readonlyFile },CancellationToken.None).Dispose(),"Two names of one file in the same batch are refused");
        File.Delete(hard);
        // A file another app holds open without delete sharing cannot be renamed.
        using (var held=new FileStream(readonlyFile,FileMode.Open,FileAccess.Read,FileShare.Read))
            reject(()=>NativeFiles.OpenRename(readonlyFile).Dispose(),"A file held open by another app is refused");
        check(!Directory.GetFiles(directory,".swivel-rename-*").Any(),"Rename never leaves staging originals");
    }
    [DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool CreateHardLink(string newName,string existing,IntPtr security);
}
