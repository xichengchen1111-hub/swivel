using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Swivel.Core;
using Swivel.Windows;

internal static class ProjectFolderNativeChecks
{
    public static void Run(string directory, Action<bool,string> check, Action<Action,string> reject)
    {
        string root = Path.Combine(directory,"project-folders"); Directory.CreateDirectory(root);
        string Make(string relative, byte[] bytes)
        {
            string path = Path.Combine(root,relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path,bytes); return path;
        }
        // Marker contents deliberately are not valid project syntax: this feature
        // identifies filename hints, never parses or executes project definitions.
        string dotnetMarker = Make(@"示例\Demo.csproj",new byte[] { 0xff, 0, 1 });
        string binary = Make(@"示例\bin\application.bin",new byte[120]);
        Make(@"示例\obj\cache.bin",new byte[30]);
        Make(@"python\requirements.txt",new byte[4]);
        Make(@"python\.venv\pyvenv.cfg",new byte[10]);
        Make(@"python\.venv\Lib\cache.bin",new byte[40]);
        Make(@"node\package.json",new byte[3]);
        Make(@"node\node_modules\pkg\package.json",new byte[5]);
        Make(@"node\node_modules\pkg\node_modules\nested.bin",new byte[20]);
        Make(@"cmake\CMakeLists.txt",new byte[7]);
        string cache = Make(@"cmake\build\CMakeCache.txt",new byte[11]);
        Make(@"build\unclassified.bin",new byte[19]);
        (FolderScanResult Scan, ProjectFolderResult Projects) result;
        using (var markerLock = new FileStream(dotnetMarker,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        using (var dataLock = new FileStream(binary,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            result = DoctorProjects.Run(root,default);
            check(result.Projects.Folders.Count == 5,"Native metadata-only inspection recognizes five non-overlapping candidates despite exclusive content locks");
            var bin = result.Projects.Folders.Single(x => x.RelativePath == @"示例\bin");
            check(bin.Bytes == 120 && bin.Files == 1 && !bin.Partial && bin.Evidence.Contains(@"示例\Demo.csproj"),"Candidate size and actual Unicode marker path are recorded without opening either file");
            check(result.Projects.Folders.Single(x => x.RelativePath == @"python\.venv").Bytes == 50,"Python hint requires the local pyvenv.cfg and includes its known bytes");
            check(result.Projects.Folders.Count(x => x.RelativePath.Contains("node_modules",StringComparison.Ordinal)) == 1,"Nested dependency folders are included in outer size rather than classified twice");
            check(result.Projects.Issues.Any(x => x.RelativePath == "build" && x.Reason == "Missing or unavailable project markers"),"Unmarked build folders remain visible as unclassified");
        }
        // A selected artifact folder cannot borrow a marker from its unselected parent.
        var selectedArtifact = DoctorProjects.Run(Path.GetDirectoryName(binary)!,default);
        check(selectedArtifact.Projects.Folders.Count == 0,"Selected root is not classified using an out-of-scope project marker");
        string gradle = Make(@"cmake\build.gradle",new byte[8]);
        var ambiguous = DoctorProjects.Run(root,default);
        check(!ambiguous.Projects.Folders.Any(x => x.RelativePath == @"cmake\build") && ambiguous.Projects.Issues.Any(x => x.RelativePath == @"cmake\build" && x.Reason == "Ambiguous project markers"),"CMake plus Gradle evidence is visibly ambiguous rather than choosing one type");
        File.Delete(gradle);
        File.SetAttributes(cache,FileAttributes.System);
        try
        {
            var unsafeMarker = DoctorProjects.Run(root,default);
            check(!unsafeMarker.Projects.Folders.Any(x => x.RelativePath == @"cmake\build") && unsafeMarker.Projects.Issues.Any(x => x.RelativePath == @"cmake\build\CMakeCache.txt"),"System-marked inner evidence is skipped and cannot classify CMake output");
        }
        finally { File.SetAttributes(cache,FileAttributes.Normal); }
        string outside = Path.Combine(directory,"outside-project.csproj"); File.WriteAllBytes(outside,new byte[2]);
        Make(@"linked\bin\application.bin",new byte[60]);
        string link = Path.Combine(root,@"linked\Demo.csproj"); bool made = false;
        try { File.CreateSymbolicLink(link,outside); made = true; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        { Console.WriteLine("SKIP: project marker symlink fixture requires Windows policy permission."); }
        if (made)
        {
            var links = DoctorProjects.Run(root,default);
            check(!links.Projects.Folders.Any(x => x.RelativePath == @"linked\bin") && links.Projects.Issues.Any(x => x.RelativePath == @"linked\Demo.csproj"),"Symlink project marker is not followed or used as positive evidence");
            File.Delete(link);
        }
        using (var metadata = NativeFiles.MetadataDirectory.Open(root))
        {
            var partial = FolderSizes.Scan(metadata,default,new(MaxEntries:1));
            var classified = ProjectFolders.Find(partial,default);
            check(partial.Root.Incomplete && classified.Issues.Any(x => x.Reason == "Scan entry limit"),"Metadata limit remains explicit in project results");
            reject(() => Directory.Move(root,root+" moved"),"Original metadata root lease remains pinned through pure classification");
        }
        reject(() => DoctorProjects.Run(root,new CancellationToken(true)),"Project pre-cancellation occurs before native path access");
        var app = Application.Current as App ?? new App();
        var dialog = new ProjectFoldersDialog(app,root,result.Scan,result.Projects);
        check(dialog.MinWidth == 540 && dialog.MinHeight == 480 && dialog.Content is System.Windows.Controls.DockPanel,"Project hints use a native responsive result window with fixed footer");
        check(dialog.CurrentReport.Contains(root) && dialog.CurrentReport.Contains(@"示例\Demo.csproj") && dialog.CurrentReport.Contains(@"python\.venv\pyvenv.cfg") && dialog.CurrentReport.Contains("build"),"Explicit report includes source scope, candidate evidence and unclassified reasons");
        dialog.Close();
        check(File.ReadAllBytes(dotnetMarker).SequenceEqual(new byte[] {0xff,0,1}) && new FileInfo(binary).Length == 120 && new FileInfo(cache).Length == 11,"Read-only project inspection preserves source bytes and metadata fixture sizes");
        Directory.Move(root,root+" released");check(Directory.Exists(root+" released"),"All project metadata handles released after result construction");

        // Recycle Bin move with recheck, refusal and Undo (Windows CI runs on a fixed drive with a Recycle Bin).
        string scanned = root+" released";
        var current = DoctorProjects.Run(scanned,default);
        var node = current.Projects.Folders.Single(x => x.RelativePath == @"node\node_modules");
        string nodePath = Path.Combine(scanned,node.RelativePath);
        string[] markers = node.Evidence.Select(x => Path.Combine(scanned,x)).ToArray();
        string package = Path.Combine(scanned,@"node\package.json");
        File.Move(package,package+".away");
        reject(() => NativeFiles.RecycleProjectFolder(nodePath,markers),"Recycle refuses a build folder whose project file is gone");
        File.Move(package+".away",package);
        check(Directory.Exists(nodePath),"A refused build folder stays in place");
        string venv = Path.Combine(scanned,@"python\.venv");
        File.SetAttributes(venv,File.GetAttributes(venv) | FileAttributes.System);
        try { reject(() => NativeFiles.RecycleProjectFolder(venv,new[] { Path.Combine(scanned,@"python\requirements.txt") }),"Recycle refuses a build folder that became a system item"); }
        finally { File.SetAttributes(venv,File.GetAttributes(venv) & ~FileAttributes.System); }
        if (!RecycleBin.Available(nodePath)) { Console.WriteLine("SKIP: no Recycle Bin on the test drive."); return; }
        string stored = NativeFiles.RecycleProjectFolder(nodePath,markers);
        check(!Directory.Exists(nodePath) && File.Exists(Path.Combine(stored,@"pkg\node_modules\nested.bin")) && stored.Contains("$Recycle.Bin",StringComparison.OrdinalIgnoreCase),"Recycle moves the whole build folder to the Recycle Bin and reports where: "+stored);
        check(File.Exists(package),"Recycle leaves the project file in place");
        Directory.CreateDirectory(nodePath);
        reject(() => NativeFiles.RestoreProjectFolder(nodePath,stored),"Undo refuses while something exists at the original place");
        check(Directory.Exists(stored),"A refused Undo leaves the folder in the Recycle Bin");
        Directory.Delete(nodePath);
        NativeFiles.RestoreProjectFolder(nodePath,stored);
        check(File.Exists(Path.Combine(nodePath,@"pkg\node_modules\nested.bin")) && File.ReadAllBytes(Path.Combine(nodePath,@"pkg\package.json")).Length == 5 && !Directory.Exists(stored),"Undo restores the build folder and its files from the Recycle Bin");
    }
}
