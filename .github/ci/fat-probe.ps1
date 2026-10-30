$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System; using System.IO; using System.Text; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class P {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
  [DllImport("kernel32.dll", SetLastError=true)] static extern SafeFileHandle ReOpenFile(SafeFileHandle h, uint a, uint s, uint f);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle h, byte[] info);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int k, byte[] b, uint n);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetFileInformationByHandle(SafeFileHandle h, int k, IntPtr b, uint n);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, char[] b, uint n, uint f);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool GetVolumeInformationW(string r, StringBuilder v, int vn, out uint serial, out uint max, out uint flags, StringBuilder fs, int fn);
  static SafeFileHandle Open(string p, uint a, uint s, uint f) { var h = CreateFileW(p, a, s, IntPtr.Zero, 3, f, IntPtr.Zero); if (h.IsInvalid) throw new Exception("open " + p + " " + Marshal.GetLastWin32Error()); return h; }
  static string Id(SafeFileHandle h) {
    var b = new byte[52]; string r = GetFileInformationByHandle(h, b) ? ("vol=" + BitConverter.ToUInt32(b,28).ToString("x") + " idx=" + BitConverter.ToUInt32(b,44).ToString("x") + ":" + BitConverter.ToUInt32(b,48).ToString("x") + " links=" + BitConverter.ToUInt32(b,40)) : ("bhfi err " + Marshal.GetLastWin32Error());
    var x = new byte[24]; r += GetFileInformationByHandleEx(h, 18, x, 24) ? (" fileid=" + BitConverter.ToUInt64(x,0).ToString("x") + "/" + BitConverter.ToUInt64(x,16).ToString("x") + BitConverter.ToUInt64(x,8).ToString("x")) : (" fileid err " + Marshal.GetLastWin32Error());
    var c = new char[1024]; uint n = GetFinalPathNameByHandleW(h, c, 1024, 0); r += " final=" + (n == 0 ? ("err " + Marshal.GetLastWin32Error()) : new string(c, 0, (int)n));
    var bb = new byte[40]; if (GetFileInformationByHandleEx(h, 0, bb, 40)) r += " ctime=" + BitConverter.ToInt64(bb,0) + " wtime=" + BitConverter.ToInt64(bb,16) + " chtime=" + BitConverter.ToInt64(bb,24);
    return r;
  }
  static string Rename(SafeFileHandle h, string dest) {
    byte[] name = Encoding.Unicode.GetBytes(dest); int lo = IntPtr.Size*2, no = lo+4; int size = Math.Max(no+name.Length+2, 24);
    IntPtr buf = Marshal.AllocHGlobal(size); try { Marshal.Copy(new byte[size],0,buf,size); Marshal.WriteInt32(buf,lo,name.Length); Marshal.Copy(name,0,buf+no,name.Length);
      return SetFileInformationByHandle(h, 3, buf, (uint)size) ? "ok" : ("err " + Marshal.GetLastWin32Error()); } finally { Marshal.FreeHGlobal(buf); }
  }
  public static void Run(string root) {
    var fs = new StringBuilder(64); uint serial, max, flags; GetVolumeInformationW(root, null, 0, out serial, out max, out flags, fs, 64);
    Console.WriteLine("== " + root + " fs=" + fs + " serial=" + serial.ToString("x") + " max=" + max + " flags=" + flags.ToString("x"));
    string a = Path.Combine(root, "pa"), b = Path.Combine(root, "pb"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
    string f = Path.Combine(a, "f.txt"); File.WriteAllText(f, "hello");
    using (var da = Open(a, 0x80|0x20, 3, 0x02200000)) Console.WriteLine("dir a: " + Id(da));
    using (var h = Open(f, 0x10000|0x80, 1, 0x00200000)) {
      Console.WriteLine("start : " + Id(h));
      using (var fresh = Open(f, 0x80, 7, 0x00200000)) Console.WriteLine("fresh : " + Id(fresh));
      Console.WriteLine("rename long: " + Rename(h, Path.Combine(a, "a-very-long-file-name-that-needs-several-entries.txt")));
      Console.WriteLine("after : " + Id(h));
      using (var fresh = Open(Path.Combine(a, "a-very-long-file-name-that-needs-several-entries.txt"), 0x80, 7, 0x00200000)) Console.WriteLine("fresh : " + Id(fresh));
      Console.WriteLine("rename case: " + Rename(h, Path.Combine(a, "A-VERY-long-file-name-that-needs-several-entries.txt")));
      Console.WriteLine("after : " + Id(h));
      Console.WriteLine("move  : " + Rename(h, Path.Combine(b, "f.txt")));
      Console.WriteLine("after : " + Id(h));
      var r1 = ReOpenFile(h, 0x80, 7, 0); Console.WriteLine("reopen attr: " + (r1.IsInvalid ? ("err " + Marshal.GetLastWin32Error()) : Id(r1)));
      h.Dispose();
      if (!r1.IsInvalid) {
        var r2 = ReOpenFile(r1, 0x10000|0x80, 1, 0); Console.WriteLine("reopen delete from attr: " + (r2.IsInvalid ? ("err " + Marshal.GetLastWin32Error()) : Id(r2))); if (!r2.IsInvalid) r2.Dispose();
        try { File.Move(Path.Combine(b, "f.txt"), Path.Combine(a, "moved-away.txt")); Console.WriteLine("external move while attr held: ok; held=" + Id(r1)); } catch (Exception e) { Console.WriteLine("external move while attr held: " + e.Message); }
        try { File.Delete(Path.Combine(a, "moved-away.txt")); Console.WriteLine("delete while attr held: ok; held=" + Id(r1)); } catch (Exception e) { Console.WriteLine("delete: " + e.Message); }
        try { File.WriteAllText(Path.Combine(a, "moved-away.txt"), "new"); Console.WriteLine("recreate: ok"); } catch (Exception e) { Console.WriteLine("recreate: " + e.Message); }
        var r3 = ReOpenFile(r1, 0x10000|0x80, 1, 0); Console.WriteLine("reopen after delete: " + (r3.IsInvalid ? ("err " + Marshal.GetLastWin32Error()) : Id(r3))); if (!r3.IsInvalid) r3.Dispose();
        r1.Dispose();
        try { using (var nw = Open(Path.Combine(a, "moved-away.txt"), 0x80, 7, 0x00200000)) Console.WriteLine("new file: " + Id(nw)); } catch (Exception e) { Console.WriteLine(e.Message); }
      }
    }
    // folder move identity
    string c = Path.Combine(a, "sub"); Directory.CreateDirectory(c);
    using (var dh = Open(c, 0x10000|0x80, 1, 0x02200000)) { Console.WriteLine("folder: " + Id(dh)); Console.WriteLine("folder move: " + Rename(dh, Path.Combine(b, "sub-moved-with-a-long-name"))); Console.WriteLine("folder after: " + Id(dh)); }
    try { File.WriteAllText(Path.Combine(b, "s.txt:Zone.Identifier"), "x"); Console.WriteLine("ADS write: ok"); } catch (Exception e) { Console.WriteLine("ADS write: " + e.GetType().Name + " " + e.Message); }
    try { using (var big = new FileStream(Path.Combine(b, "big.bin"), FileMode.CreateNew)) { big.SetLength(4294967295L); Console.WriteLine("SetLength 4GiB-1: ok"); big.SetLength(4294967296L); Console.WriteLine("SetLength 4GiB: ok"); } } catch (Exception e) { Console.WriteLine("SetLength: " + e.GetType().Name + " " + e.Message + " hr=" + e.HResult.ToString("x")); }
  }
}
'@
foreach ($r in @($env:SWIVEL_TEST_EXFAT, $env:SWIVEL_TEST_FAT32, 'C:\swivelprobe\')) { if (-not (Test-Path $r)) { New-Item -ItemType Directory $r | Out-Null }; try { [P]::Run($r) } catch { Write-Host "probe failed: $_" } }
