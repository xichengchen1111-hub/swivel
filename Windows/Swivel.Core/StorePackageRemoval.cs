using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Swivel.Core;

/// Why a listed package is not offered for Uninstall…; None means it is offered.
public enum RemovalBlock { None,ListIncomplete,Identity,Framework,Resource,Bundle,System,Status,Swivel,Protected,Dependents }
/// A listed package compared with a fresh package list just before removal.
public enum RemovalRecheck { Ready,Gone,Changed,Blocked }
/// What a fresh package list shows after the system removal returned.
public enum RemovalOutcome { Removed,StillListed,Unknown }

/// Uninstall… for one current-user Store or MSIX package: which listed packages are offered,
/// the recheck against a fresh list just before removal, and what is offered afterwards (only
/// the folder Windows keeps for the package family under Local app data). Pure rules: the
/// native side reads packages, calls the system package manager and moves folders.
public static class StorePackageRemoval
{
    public const int MaxDependencies=64;
    public const string Confirm="Uninstall this package for your Windows account?";
    public const string Warning="Windows removes the app and the data it keeps for this app for your account. This cannot be undone from Swivel: to get the app back, install it again from the Microsoft Store or from where it came from. Only your account is changed: other Windows users and copies provisioned for new users are not. Swivel does not ask for administrator rights and does not close the app: if it starts again before the removal finishes, Windows may close it.";
    public const string Notice="Uninstall… removes one selected app package for your Windows account with the system package manager, after a confirmation. Frameworks, resource packages, bundles, system-signed packages, packages whose status is not OK, packages other listed packages depend on, Store and App Installer, Edge, Windows Security, Windows' own packages and Swivel itself are never offered, and nothing is offered while the list is incomplete. Just before removal the package list is read again: if the package's full name, version or kind changed, or a process of the app that Swivel can query is running, nothing is removed. Removal cannot be undone from Swivel.";
    public const string DataNotice="After an uninstall, the folder Windows keeps for that package family under Local app data (Packages) is offered only when no package of that family is installed for your account any more and the folder is still there; Windows usually removes it itself. It can be moved to the Recycle Bin after a confirmation, only while the family is still not installed, and Undo Move puts it back while this window is open unless the app was installed again.";
    public const string Running="The app is running, or Swivel could not list the running programs, so nothing was removed. Close the app, including any copy in the background, and try again.";
    public const string ReinstalledData="A package of this family is installed for your account again, or Swivel could not check, so the folder was left alone.";
    public const string ReinstalledRestore="A package of this family is installed for your account again, or Swivel could not check, so the folder was not put back.";
    public const string DataNotRecycled="Windows did not move it to the Recycle Bin. If Windows removed part of it, that part cannot be restored with Undo.";

    // Packages whose removal would take away the Store, app installation, the browser or
    // Windows Security; Windows' own packages are matched by prefix.
    private static readonly HashSet<string> ProtectedNames=new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsStore","Microsoft.StorePurchaseApp","Microsoft.DesktopAppInstaller","Microsoft.MicrosoftEdge","Microsoft.MicrosoftEdge.Stable","Microsoft.MicrosoftEdgeDevToolsClient",
        "Microsoft.SecHealthUI","Microsoft.Services.Store.Engagement","Microsoft.WindowsAppRuntime",
    };
    private static readonly string[] ProtectedPrefixes={"Microsoft.Windows.","MicrosoftWindows.","Windows.","Microsoft.WindowsAppRuntime.","Microsoft.VCLibs.","Microsoft.UI.Xaml.","Microsoft.NET.",
        // Windows App SDK runtime main/singleton/DDLM packages and winget sources: apps rely on
        // them without declaring a package dependency.
        "Microsoft.WinAppRuntime.","MicrosoftCorporationII.WinAppRuntime.","Microsoft.Winget."};

    /// A dependency list as read: every entry a valid package full name, at most 64; else null.
    public static IReadOnlyList<string>? Dependencies(IReadOnlyList<string>? raw)
    {
        if(raw is null||raw.Count>MaxDependencies)return null;
        var list=new List<string>(raw.Count);
        foreach(string? name in raw){if(name is null||!ValidFullName(name))return null;list.Add(name);}
        return list.AsReadOnly();
    }

    // Package name: 3–50 of A–Z, a–z, 0–9, '.', '-'. Publisher ID: 13 Crockford base32 characters.
    private static bool ValidName(string name)=>name.Length is >=3 and <=50&&name.All(c=>c is >='A' and <='Z' or >='a' and <='z' or >='0' and <='9' or '.' or '-')&&name[0]!='.'&&name[^1]!='.';
    private static bool ValidPublisherId(string id)=>id.Length==13&&id.All(c=>c is >='0' and <='9' or >='a' and <='h' or 'j' or 'k' or 'm' or 'n' or >='p' and <='t' or >='v' and <='z');
    /// "Name_PublisherId": safe as one folder name.
    public static bool ValidFamily(string? family)
    {
        if(family is null)return false;int at=family.LastIndexOf('_');
        return at>0&&family.IndexOf('_')==at&&ValidName(family[..at])&&ValidPublisherId(family[(at+1)..]);
    }
    /// "Name_Version_Architecture_ResourceId_PublisherId"; the resource ID may be empty.
    public static bool ValidFullName(string? full)
    {
        if(full is null||full.Length>127)return false;var parts=full.Split('_');
        if(parts.Length!=5||!ValidName(parts[0])||!ValidPublisherId(parts[4]))return false;
        var version=parts[1].Split('.');
        if(version.Length!=4||version.Any(v=>v.Length is <1 or >5||!v.All(char.IsAsciiDigit)||!ushort.TryParse(v,NumberStyles.None,CultureInfo.InvariantCulture,out _)))return false;
        return parts[2] is "x86" or "x64" or "arm" or "arm64" or "neutral" or "x86a64"&&parts[3].All(c=>c is >='A' and <='Z' or >='a' and <='z' or >='0' and <='9' or '.' or '-')&&parts[3].Length<=30;
    }
    private static string? Text(PackageText? text)=>text is {State:PackageValueState.Present}?text.Value:null;
    /// The package name part of a family name.
    public static string? PackageName(string? family)=>ValidFamily(family)?family![..family!.IndexOf('_')]:null;
    /// Full name and family read, both valid, describing the same package and the listed version.
    public static bool Consistent(StorePackageRecord row)
    {
        string? full=Text(row.FullName),family=Text(row.Family);
        if(!ValidFullName(full)||!ValidFamily(family)||row.Version is not PackageVersion version)return false;
        var parts=full!.Split('_');
        return string.Equals(parts[0]+"_"+parts[4],family,StringComparison.Ordinal)&&parts[1]==version.ToString();
    }

    /// Whether `row` of `snapshot` is offered for Uninstall…. `ownFamily` is Swivel's own package
    /// family when it runs packaged.
    public static RemovalBlock Check(StorePackageRecord row,StorePackagesSnapshot snapshot,string? ownFamily)
    {
        ArgumentNullException.ThrowIfNull(row);ArgumentNullException.ThrowIfNull(snapshot);
        if(!Consistent(row)||row.TimeLimited)return RemovalBlock.Identity;
        if(row.Framework!=false)return RemovalBlock.Framework;
        if(row.Resource!=false)return RemovalBlock.Resource;
        if(row.Bundle!=false)return RemovalBlock.Bundle;
        if(row.Signature is not (0 or 1 or 2 or 3))return RemovalBlock.System;
        if(row.StatusOk!=true)return RemovalBlock.Status;
        string family=Text(row.Family)!,full=Text(row.FullName)!,name=PackageName(family)!;
        if(ownFamily is not null&&string.Equals(family,ownFamily,StringComparison.OrdinalIgnoreCase))return RemovalBlock.Swivel;
        if(ProtectedNames.Contains(name)||ProtectedPrefixes.Any(p=>name.StartsWith(p,StringComparison.OrdinalIgnoreCase)))return RemovalBlock.Protected;
        if(snapshot.Issues!=PackageListIssue.None||snapshot.Records.Any(x=>x.TimeLimited||x.DependsOn is null))return RemovalBlock.ListIncomplete;
        if(snapshot.Records.Count(x=>string.Equals(Text(x.FullName),full,StringComparison.OrdinalIgnoreCase))!=1)return RemovalBlock.ListIncomplete;
        foreach(var other in snapshot.Records)
        {
            // A bundle lists the packages it contains; only packages that run count as dependents.
            if(ReferenceEquals(other,row)||other.Bundle==true)continue;
            if(other.DependsOn!.Any(d=>string.Equals(d,full,StringComparison.OrdinalIgnoreCase)||string.Equals(FamilyOf(d),family,StringComparison.OrdinalIgnoreCase)))return RemovalBlock.Dependents;
        }
        return RemovalBlock.None;
    }
    private static string FamilyOf(string full){var parts=full.Split('_');return parts[0]+"_"+parts[^1];}

    /// Just before removal: the listed package against a fresh list. Ready only when exactly the
    /// same full name is listed, with the same name, publisher, version and kind, and still offered.
    public static (RemovalRecheck State,RemovalBlock Block) Recheck(StorePackageRecord listed,StorePackagesSnapshot now,string? ownFamily)
    {
        ArgumentNullException.ThrowIfNull(listed);ArgumentNullException.ThrowIfNull(now);
        string? full=Text(listed.FullName),family=Text(listed.Family);
        if(full is null||family is null)return(RemovalRecheck.Blocked,RemovalBlock.Identity);
        var same=now.Records.Where(x=>string.Equals(Text(x.FullName),full,StringComparison.Ordinal)).ToArray();
        if(same.Length==0)
        {
            if(now.Issues!=PackageListIssue.None||now.Records.Any(x=>x.TimeLimited||x.FullName is not {State:PackageValueState.Present}))return(RemovalRecheck.Blocked,RemovalBlock.ListIncomplete);
            return now.Records.Any(x=>string.Equals(Text(x.Family),family,StringComparison.OrdinalIgnoreCase))?(RemovalRecheck.Changed,RemovalBlock.None):(RemovalRecheck.Gone,RemovalBlock.None);
        }
        if(same.Length>1)return(RemovalRecheck.Blocked,RemovalBlock.ListIncomplete);
        var row=same[0];
        if(row.Name!=listed.Name||row.Publisher!=listed.Publisher||row.Version!=listed.Version||row.Architecture!=listed.Architecture||!string.Equals(Text(row.Family),family,StringComparison.Ordinal)
            ||row.Framework!=listed.Framework||row.Resource!=listed.Resource||row.Bundle!=listed.Bundle||row.Development!=listed.Development||row.Signature!=listed.Signature)
            return(RemovalRecheck.Changed,RemovalBlock.None);
        var block=Check(row,now,ownFamily);
        return block==RemovalBlock.None?(RemovalRecheck.Ready,RemovalBlock.None):(RemovalRecheck.Blocked,block);
    }

    /// After the system removal returned: whether a fresh list still has the full name.
    public static RemovalOutcome Outcome(string fullName,StorePackagesSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(after);
        if(after.Records.Any(x=>string.Equals(Text(x.FullName),fullName,StringComparison.OrdinalIgnoreCase)))return RemovalOutcome.StillListed;
        return after.Issues!=PackageListIssue.None||after.Records.Any(x=>x.TimeLimited||x.FullName is not {State:PackageValueState.Present})?RemovalOutcome.Unknown:RemovalOutcome.Removed;
    }
    /// No package of `family` in a complete fresh list.
    public static bool FamilyGone(string family,StorePackagesSnapshot after)=>
        after.Issues==PackageListIssue.None&&after.Records.All(x=>!x.TimeLimited&&x.Family.State==PackageValueState.Present)
        &&!after.Records.Any(x=>string.Equals(Text(x.Family),family,StringComparison.OrdinalIgnoreCase));

    /// The folder Windows keeps for a package family for this user, from the reported Local app
    /// data path (a plain local drive path), or null.
    public static string? DataFolder(string? localAppData,string? family)
    {
        if(!ValidFamily(family)||localAppData is null)return null;
        string root=localAppData.TrimEnd('\\');
        if(root.Length<3||!char.IsAsciiLetter(root[0])||root[1]!=':'||root[2]!='\\'||root.Contains('/')||root.Contains("\\\\")||root.Split('\\').Any(p=>p is "." or ".."))return null;
        string path=root+@"\Packages\"+family;
        try{return WindowsPathPolicy.Validate(path);}catch(UserError){return null;}
    }

    public static string Reason(RemovalBlock block,Func<string,string> l)=>l(block switch
    {
        RemovalBlock.None=>"Can be uninstalled for your account after a confirmation.",
        RemovalBlock.ListIncomplete=>"Not offered: the package list is incomplete, so packages that depend on it cannot be ruled out.",
        RemovalBlock.Identity=>"Not offered: its full name, family name or version could not be read consistently.",
        RemovalBlock.Framework=>"Not offered: a framework package (or its kind is unknown). Apps use it; Windows removes it when no app needs it.",
        RemovalBlock.Resource=>"Not offered: a resource package (or its kind is unknown). It belongs to its app.",
        RemovalBlock.Bundle=>"Not offered: a bundle (or its kind is unknown). Uninstall the app package listed for it instead.",
        RemovalBlock.System=>"Not offered: a system-signed package, or its signature kind is unknown.",
        RemovalBlock.Status=>"Not offered: Windows does not report its status as OK (it may be updating, damaged or not available).",
        RemovalBlock.Swivel=>"Not offered: this is Swivel itself.",
        RemovalBlock.Protected=>"Not offered: Windows' own packages, the Store, App Installer, Edge and Windows Security stay installed.",
        RemovalBlock.Dependents=>"Not offered: another listed package depends on it.",
        _=>throw new ArgumentOutOfRangeException(nameof(block)),
    });
    public static string RecheckText(RemovalRecheck state,RemovalBlock block,Func<string,string> l)=>state switch
    {
        RemovalRecheck.Ready=>l("The package is unchanged."),
        RemovalRecheck.Gone=>l("The package is no longer installed for your account. Nothing was removed."),
        RemovalRecheck.Changed=>l("The package changed since the list was read (for example it was updated). Nothing was removed. Read the package list again."),
        RemovalRecheck.Blocked=>l("Nothing was removed.")+" "+Reason(block,l),
        _=>throw new ArgumentOutOfRangeException(nameof(state)),
    };
    public static string OutcomeText(RemovalOutcome outcome,Func<string,string> l)=>l(outcome switch
    {
        RemovalOutcome.Removed=>"The package is no longer in your account's package list.",
        RemovalOutcome.StillListed=>"The package is still in your account's package list.",
        RemovalOutcome.Unknown=>"The package list read afterwards is incomplete, so whether the package was removed is not known.",
        _=>throw new ArgumentOutOfRangeException(nameof(outcome)),
    });
    /// System error text is shown escaped and at most 1,024 UTF-16 units.
    public static string Error(int hresult,string? text,Func<string,string> l)
    {
        string code="0x"+hresult.ToString("X8",CultureInfo.InvariantCulture);
        var checkedText=StorePackages.Validate(text);
        return string.Format(l("Windows reported an error: {0}"),code)+(checkedText.State==PackageValueState.Present?" · "+AppMemory.Visible(checkedText.Value!):"");
    }
    public static string Confirmation(StorePackageRecord row,Func<string,string> l)=>string.Join("\n",
        l(Confirm),"",l("Reported Package Name")+": "+StorePackages.Value(row.Name,l),l("Reported Publisher")+": "+StorePackages.Value(row.Publisher,l),
        l("Package Version")+": "+StorePackages.Version(row.Version,l),l("Package Full Name")+": "+StorePackages.Value(row.FullName??PackageText.Unknown(PackageValueState.Unavailable),l),"",l(Warning));
    public static string Progress(uint percent,Func<string,string> l)=>string.Format(l("Uninstalling… {0}%"),Math.Min(percent,100).ToString(CultureInfo.InvariantCulture));
}
