using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Swivel.Core;

namespace Swivel.Windows;

// Windows' own Authenticode verification (WinVerifyTrust, generic verify v2) of a held file
// handle: first an embedded signature, then a Windows catalog that lists the file's hash.
// No UI. Offline by default: revocation off and URL retrieval limited to the local cache.
// Online adds whole-chain revocation and may contact certificate servers.
internal static class AppTrustReader
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint UiNone = 2, RevokeNone = 0, RevokeWholeChain = 1, ChoiceFile = 1, ChoiceCatalog = 2, StateVerify = 1, StateClose = 2;
    private const uint RevocationCheckNone = 0x10, RevocationCheckChain = 0x40, CacheOnlyUrlRetrieval = 0x1000;

    internal static TrustFacts Verify(SafeFileHandle file, string path, bool online)
    {
        bool added = false;
        file.DangerousAddRef(ref added);
        try
        {
            var embedded = VerifyEmbedded(file.DangerousGetHandle(), path, online);
            if (Classify(embedded) != TrustVerdict.NotSigned) return embedded;
            return VerifyCatalog(file.DangerousGetHandle(), path, online) ?? embedded;
        }
        finally { if (added) file.DangerousRelease(); }
    }
    private static TrustVerdict Classify(TrustFacts facts) => AppCheck.Classify(facts, DateTime.UtcNow);

    private static TrustFacts VerifyEmbedded(IntPtr handle, string path, bool online)
    {
        IntPtr pathText = Marshal.StringToHGlobalUni(path), info = IntPtr.Zero;
        try
        {
            info = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            Marshal.StructureToPtr(new FileInfo { cbStruct = (uint)Marshal.SizeOf<FileInfo>(), pcwszFilePath = pathText, hFile = handle }, info, false);
            return Run(ChoiceFile, info, online, TrustSource.Embedded);
        }
        finally { if (info != IntPtr.Zero) Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(pathText); }
    }

    private static TrustFacts? VerifyCatalog(IntPtr handle, string path, bool online)
    {
        foreach (string? algorithm in new[] { "SHA256", null })
        {
            if (!CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
            IntPtr catalog = IntPtr.Zero;
            try
            {
                byte[]? hash = Hash(admin, handle);
                if (hash is null) continue;
                catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, (uint)hash.Length, 0, IntPtr.Zero);
                if (catalog == IntPtr.Zero) continue;
                var catalogInfo = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf<CatalogInfo>() };
                if (!CryptCATCatalogInfoFromContext(catalog, ref catalogInfo, 0)) continue;
                string tag = Convert.ToHexString(hash);
                IntPtr catalogPath = Marshal.StringToHGlobalUni(catalogInfo.wszCatalogFile), tagText = Marshal.StringToHGlobalUni(tag), member = Marshal.StringToHGlobalUni(path);
                IntPtr hashBytes = Marshal.AllocHGlobal(hash.Length), data = IntPtr.Zero;
                try
                {
                    Marshal.Copy(hash, 0, hashBytes, hash.Length);
                    data = Marshal.AllocHGlobal(Marshal.SizeOf<CatalogMember>());
                    Marshal.StructureToPtr(new CatalogMember
                    {
                        cbStruct = (uint)Marshal.SizeOf<CatalogMember>(), pcwszCatalogFilePath = catalogPath, pcwszMemberTag = tagText,
                        pcwszMemberFilePath = member, hMemberFile = handle, pbCalculatedFileHash = hashBytes, cbCalculatedFileHash = (uint)hash.Length, hCatAdmin = admin,
                    }, data, false);
                    return Run(ChoiceCatalog, data, online, TrustSource.Catalog);
                }
                finally
                {
                    if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
                    Marshal.FreeHGlobal(hashBytes); Marshal.FreeHGlobal(member); Marshal.FreeHGlobal(tagText); Marshal.FreeHGlobal(catalogPath);
                }
            }
            finally
            {
                if (catalog != IntPtr.Zero) CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                CryptCATAdminReleaseContext(admin, 0);
            }
        }
        return null;
    }

    private static byte[]? Hash(IntPtr admin, IntPtr handle)
    {
        uint size = 0;
        if (!SetFilePointerEx(handle, 0, IntPtr.Zero, 0)) return null;
        CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, null, 0);
        if (size is 0 or > 64) return null;
        var hash = new byte[size];
        if (!SetFilePointerEx(handle, 0, IntPtr.Zero, 0)) return null;
        return CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, hash, 0) && size == hash.Length ? hash : null;
    }

    private static TrustFacts Run(uint choice, IntPtr info, bool online, TrustSource source)
    {
        var action = GenericVerifyV2;
        var data = new TrustData
        {
            cbStruct = (uint)Marshal.SizeOf<TrustData>(), dwUIChoice = UiNone, fdwRevocationChecks = online ? RevokeWholeChain : RevokeNone,
            dwUnionChoice = choice, pInfo = info, dwStateAction = StateVerify,
            dwProvFlags = online ? RevocationCheckChain : RevocationCheckNone | CacheOnlyUrlRetrieval,
        };
        int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        try
        {
            string? subject = null, issuer = null; DateTime? timestamp = null, notAfter = null;
            if (data.hWVTStateData != IntPtr.Zero)
            {
                IntPtr provider = WTHelperProvDataFromStateData(data.hWVTStateData);
                IntPtr signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                if (signer != IntPtr.Zero)
                {
                    var record = Marshal.PtrToStructure<ProviderSigner>(signer);
                    if (record.csCounterSigners > 0)
                    {
                        long time = ((long)record.verifyHigh << 32) | record.verifyLow;
                        if (time > 0) timestamp = DateTime.FromFileTimeUtc(time);
                    }
                    if (record.csCertChain > 0 && record.pasCertChain != IntPtr.Zero)
                    {
                        IntPtr context = Marshal.ReadIntPtr(record.pasCertChain, IntPtr.Size);
                        if (context != IntPtr.Zero)
                        {
                            using var certificate = new X509Certificate2(context);
                            subject = Clean(certificate.GetNameInfo(X509NameType.SimpleName, false));
                            issuer = Clean(certificate.GetNameInfo(X509NameType.SimpleName, true));
                            notAfter = certificate.NotAfter.ToUniversalTime();
                        }
                    }
                }
            }
            return new(result, Classify(new(result, source, null, null, null, null, online)) == TrustVerdict.NotSigned && source == TrustSource.Embedded ? TrustSource.None : source, subject, issuer, timestamp, notAfter, online);
        }
        finally
        {
            data.dwStateAction = StateClose;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
    }
    private static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : DoctorFacts.VisibleBounded(name, 200);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo { public uint cbStruct; public IntPtr pcwszFilePath, hFile, pgKnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CatalogMember
    {
        public uint cbStruct, dwCatalogVersion; public IntPtr pcwszCatalogFilePath, pcwszMemberTag, pcwszMemberFilePath, hMemberFile, pbCalculatedFileHash;
        public uint cbCalculatedFileHash; public IntPtr pcCatalogContext, hCatAdmin;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pInfo; public uint dwStateAction; public IntPtr hWVTStateData, pwszURLReference; public uint dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderSigner
    {
        public uint cbStruct, verifyLow, verifyHigh, csCertChain; public IntPtr pasCertChain; public uint dwSignerType; public IntPtr psSigner;
        public uint dwError, csCounterSigners; public IntPtr pasCounterSigners, pChainContext;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo { public uint cbStruct; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile; }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string? hashAlgorithm, IntPtr strongHashPolicy, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, IntPtr file, ref uint size, byte[]? hash, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)] private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint size, uint flags, IntPtr previous);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFilePointerEx(IntPtr file, long distance, IntPtr newPosition, uint method);
}
