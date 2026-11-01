using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class ConfigurationPathProbe
{
    // Attribute-only opens and pinned ancestors use the existing placeholder refusal.
    // Restrict unsolicited configuration probes to fixed disks before any path access.
    internal static ConfigurationPathState Read(string? path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(path is null)return ConfigurationPathState.Unknown;
        try{WindowsPathPolicy.Validate(path);}catch(UserError){return ConfigurationPathState.Unknown;}
        uint previous=GetThreadErrorMode();
        if(!SetThreadErrorMode(previous|1,out uint restore))return ConfigurationPathState.Unknown;
        var state=ConfigurationPathState.Unknown;
        try
        {
            token.ThrowIfCancellationRequested();
            if(GetDriveTypeW(path[..3])==3)
            {
                try{NativeFiles.ValidateInput(path);state=ConfigurationPathState.Present;}
                catch(Win32Exception error)when(error.NativeErrorCode is 2 or 3){state=ConfigurationPathState.Missing;}
                catch(Exception error)when(error is Win32Exception or UserError or System.IO.IOException or UnauthorizedAccessException){state=ConfigurationPathState.Unknown;}
            }
        }
        finally{if(!SetThreadErrorMode(restore,out _))state=ConfigurationPathState.Unknown;}
        token.ThrowIfCancellationRequested();return state;
    }
    [DllImport("kernel32.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint GetThreadErrorMode();
    [DllImport("kernel32.dll",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetThreadErrorMode(uint mode,out uint previous);
    [DllImport("kernel32.dll",ExactSpelling=true,CharSet=CharSet.Unicode)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern uint GetDriveTypeW(string root);
}
