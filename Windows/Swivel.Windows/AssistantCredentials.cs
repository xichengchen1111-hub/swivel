using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Swivel.Core;

namespace Swivel.Windows;

internal static class AssistantCredentials
{
    internal const string Failure="Windows Credential Manager could not read, save or remove this service key. No key is stored in Swivel settings.";
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    internal struct Credential
    {
        public uint Flags,Type;public IntPtr TargetName,Comment;public long LastWritten;public uint BlobSize;public IntPtr Blob;public uint Persist,AttributeCount;public IntPtr Attributes,TargetAlias,UserName;
    }
    internal static string? Read(AssistantSettings settings)
    {
        string target=settings.CredentialTarget;if(!CredRead(target,1,0,out var pointer)){if(Marshal.GetLastWin32Error()==1168)return null;throw new UserError(Failure);}
        Credential value=default;try
        {
            value=Marshal.PtrToStructure<Credential>(pointer);if(value.Type!=1||value.BlobSize is <1 or >2048||value.Blob==IntPtr.Zero)throw new UserError(Failure);
            byte[] bytes=new byte[value.BlobSize];try{Marshal.Copy(value.Blob,bytes,0,bytes.Length);if(Array.Exists(bytes,b=>b<33||b>126))throw new UserError(Failure);return Encoding.ASCII.GetString(bytes);}
            finally{CryptographicOperations.ZeroMemory(bytes);}
        }
        finally{try{if(value.Blob!=IntPtr.Zero&&value.BlobSize is >0 and <=2560)Marshal.Copy(new byte[value.BlobSize],0,value.Blob,(int)value.BlobSize);}finally{CredFree(pointer);}}
    }
    internal static void Save(AssistantSettings settings,string key)
    {
        string target=settings.CredentialTarget;AssistantSettings.ValidateKey(key);byte[] bytes=Encoding.ASCII.GetBytes(key);IntPtr blob=IntPtr.Zero,name=IntPtr.Zero,user=IntPtr.Zero;
        try
        {
            blob=Marshal.AllocHGlobal(bytes.Length);name=Marshal.StringToHGlobalUni(target);user=Marshal.StringToHGlobalUni("Swivel Assistant");
            Marshal.Copy(bytes,0,blob,bytes.Length);var value=new Credential{Type=1,TargetName=name,BlobSize=(uint)bytes.Length,Blob=blob,Persist=2,UserName=user};if(!CredWrite(ref value,0))throw new UserError(Failure);
        }
        finally{CryptographicOperations.ZeroMemory(bytes);try{if(blob!=IntPtr.Zero)Marshal.Copy(bytes,0,blob,bytes.Length);}finally{if(blob!=IntPtr.Zero)Marshal.FreeHGlobal(blob);if(name!=IntPtr.Zero)Marshal.FreeHGlobal(name);if(user!=IntPtr.Zero)Marshal.FreeHGlobal(user);}}
    }
    internal static void Delete(AssistantSettings settings){if(!CredDelete(settings.CredentialTarget,1,0)&&Marshal.GetLastWin32Error()!=1168)throw new UserError(Failure);}
    [DllImport("advapi32.dll",EntryPoint="CredReadW",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CredRead(string target,uint type,uint flags,out IntPtr value);
    [DllImport("advapi32.dll",EntryPoint="CredWriteW",ExactSpelling=true,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CredWrite(ref Credential value,uint flags);
    [DllImport("advapi32.dll",EntryPoint="CredDeleteW",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CredDelete(string target,uint type,uint flags);
    [DllImport("advapi32.dll",EntryPoint="CredFree",ExactSpelling=true)][DefaultDllImportSearchPaths(DllImportSearchPath.System32)]private static extern void CredFree(IntPtr value);
}
