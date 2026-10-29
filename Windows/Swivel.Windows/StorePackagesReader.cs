using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Swivel.Core;
using Windows.ApplicationModel;
using PackageVersion = Swivel.Core.PackageVersion;
using Windows.Management.Deployment;

namespace Swivel.Windows;

// Read-only package properties for the current user only (empty SID). No package
// removal, registration, provisioning, Store queries, file access or network.
internal static class StorePackagesReader
{
    internal static StorePackagesSnapshot Read(CancellationToken token)=>StorePackages.Scan(new Source(),token);
    // Expected failures of the system package manager or of one property. Anything
    // else (a programming error) still surfaces.
    internal static bool Unavailable(Exception error)=>error is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
        or NotSupportedException or PlatformNotSupportedException or TypeLoadException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException;
    private sealed class Source:IStorePackageSource
    {
        public IStorePackageList? Open()
        {
            IEnumerator<Package>? packages=null;
            try{packages=new PackageManager().FindPackagesForUser(string.Empty).GetEnumerator();return new List(packages);}
            catch(Exception error)when(Unavailable(error)){packages?.Dispose();return null;}
        }
    }
    private sealed class List(IEnumerator<Package> packages):IStorePackageList
    {
        public PackageNext Next()
        {
            try{return packages.MoveNext()?new(PackageNextKind.Item,new Entry(packages.Current)):new(PackageNextKind.End);}
            catch(Exception error)when(Unavailable(error)){return new(PackageNextKind.Unavailable);}
        }
        public void Dispose()=>packages.Dispose();
    }
    private sealed class Entry(Package package):IStorePackageEntry
    {
        private static T? Try<T>(Func<T> read)where T:struct{try{return read();}catch(Exception error)when(Unavailable(error)){return null;}}
        public string? Text(PackageTextField field)
        {
            try{return field switch{PackageTextField.DisplayName=>package.DisplayName,PackageTextField.PublisherDisplayName=>package.PublisherDisplayName,PackageTextField.FamilyName=>package.Id.FamilyName,_=>null};}
            catch(Exception error)when(Unavailable(error)){return null;}
        }
        public PackageVersion? Version()=>Try(()=>{var v=package.Id.Version;return new PackageVersion(v.Major,v.Minor,v.Build,v.Revision);});
        public uint? Architecture()=>Try(()=>(uint)package.Id.Architecture);
        public bool? Flag(PackageFlag flag)=>Try(()=>flag switch{PackageFlag.Framework=>package.IsFramework,PackageFlag.Resource=>package.IsResourcePackage,PackageFlag.Bundle=>package.IsBundle,PackageFlag.Development=>package.IsDevelopmentMode,_=>throw new ArgumentOutOfRangeException(nameof(flag))});
        public DateTimeOffset? Installed()=>Try(()=>package.InstalledDate);
    }
}
