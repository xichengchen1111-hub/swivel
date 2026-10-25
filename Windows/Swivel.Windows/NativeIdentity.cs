using System;
using System.Runtime.InteropServices;

namespace Swivel.Windows;

internal static class NativeIdentity
{
    internal const string Required="This system feature requires an MSIX package identity. The portable ZIP does not provide it. It remains unavailable until a separately built, signed and installed Windows package has been validated.";
    internal static bool HasPackage
    {
        get{uint length=0;int status=GetCurrentPackageFullName(ref length,IntPtr.Zero);return status==122&&length is >0 and <=4096;}
    }
    internal static void Require(){if(!HasPackage)throw new Swivel.Core.UserError(Required);}
    [DllImport("kernel32.dll",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetCurrentPackageFullName(ref uint length,IntPtr name);
}
