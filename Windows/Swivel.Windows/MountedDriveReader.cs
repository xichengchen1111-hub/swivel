using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Swivel.Core;

namespace Swivel.Windows;

internal static class MountedDriveReader
{
    // Isolated synchronous worker: error-mode changes never touch the UI thread or a pooled worker.
    // Cancellation joins the real calls and restoration; it does not abandon a blocked native call.
    internal static Task<MountedDriveSnapshot> ReadAsync(CancellationToken token)
    {
        if(token.IsCancellationRequested)return Task.FromCanceled<MountedDriveSnapshot>(token);
        var done=new TaskCompletionSource<MountedDriveSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try{done.SetResult(Read(token));}
            catch(OperationCanceledException)when(token.IsCancellationRequested){done.SetCanceled(token);}
            catch(Exception error){done.SetException(error);}
        }){IsBackground=true,Name="Swivel drive metadata"};thread.Start();return done.Task;
    }
    internal static MountedDriveSnapshot Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var drives=new List<MountedDrive>();var issues=new List<DriveIssue>();bool unknownBits=false;
        uint current=GetThreadErrorMode();token.ThrowIfCancellationRequested();
        if(!SetThreadErrorMode(current|1,out uint previous))
        {uint error=Error();token.ThrowIfCancellationRequested();issues.Add(new(DriveIssueStage.ErrorModeSetup,error));return new(drives,issues,false,DateTimeOffset.Now);}
        try
        {
            token.ThrowIfCancellationRequested();uint mask=GetLogicalDrives();uint enumerationError=mask==0?Error():0;token.ThrowIfCancellationRequested();
            if(mask==0)issues.Add(new(DriveIssueStage.Enumeration,enumerationError));unknownBits=(mask&~MountedDrives.LetterMask)!=0;
            foreach(char letter in MountedDrives.Letters(mask))
            {
                token.ThrowIfCancellationRequested();string root=letter+@":\";uint type=GetDriveType(root);token.ThrowIfCancellationRequested();
                string? fs=null;uint? flags=null,volumeError=null,spaceError=null;DriveSpace? space=null;
                if(MountedDrives.QueryMetadata(type))
                {
                    var name=new StringBuilder(65);
                    if(GetVolumeInformation(root,IntPtr.Zero,0,IntPtr.Zero,IntPtr.Zero,out uint declaredFlags,name,(uint)name.Capacity))
                    {fs=name.ToString();flags=declaredFlags;}else volumeError=Error();
                    token.ThrowIfCancellationRequested();
                    if(GetDiskFreeSpaceEx(root,out ulong available,out ulong total,out ulong free))space=new(available,total,free);else spaceError=Error();
                    token.ThrowIfCancellationRequested();
                }
                drives.Add(new(letter,type,fs,flags,space,volumeError,spaceError));
            }
        }
        finally
        {
            if(!SetThreadErrorMode(previous,out _))issues.Add(new(DriveIssueStage.ErrorModeRestore,Error()));
        }
        token.ThrowIfCancellationRequested();return new(drives,issues,unknownBits,DateTimeOffset.Now);
    }
    private static uint Error()=>unchecked((uint)Marshal.GetLastWin32Error());
    [DllImport("kernel32.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint GetThreadErrorMode();
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetThreadErrorMode(uint mode,out uint previous);
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint GetLogicalDrives();
    [DllImport("kernel32.dll",EntryPoint="GetDriveTypeW",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint GetDriveType(string root);
    [DllImport("kernel32.dll",EntryPoint="GetVolumeInformationW",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetVolumeInformation(string root,IntPtr label,uint labelLength,IntPtr serial,IntPtr componentLength,out uint flags,StringBuilder fileSystem,uint fileSystemLength);
    [DllImport("kernel32.dll",EntryPoint="GetDiskFreeSpaceExW",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetDiskFreeSpaceEx(string root,out ulong available,out ulong total,out ulong free);
}
