using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

internal static class AppCacheReader
{
    internal static AppCacheResult Read(CancellationToken token)
    {
        string? Reported(AppCacheBase root)
        {
            var folder = root == AppCacheBase.Local ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.ApplicationData;
            try { return Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException) { return null; }
        }
        return AppCaches.Read(Reported, path => NativeFiles.MetadataDirectory.Open(path), token);
    }

    /// Names of all running processes, any user's (any case), or null when Windows would not
    /// list them; callers then treat every app as running.
    internal static IReadOnlySet<string>? RunningNames()
    {
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { return null; }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool complete = true;
        foreach (var process in processes)
        {
            try { names.Add(process.ProcessName); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { complete = false; }
            finally { process.Dispose(); }
        }
        return complete ? names : null;
    }

    internal static bool IsRunning(AppCacheApp app) => AppCaches.IsRunning(app, RunningNames());
}
