using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Swivel.Core;

namespace Swivel.Windows;

public sealed class AppLaunch : IDisposable
{
    private readonly NativeFiles.Input executable;
    public AppRoute Route { get; }
    public IReadOnlyList<string> Files { get; }
    private AppLaunch(AppRoute route, NativeFiles.Input executable, string[] files) { Route = route; this.executable = executable; Files = Array.AsReadOnly(files); }
    public static void ValidateExecutable(NativeFiles.Input input)
    {
        var stream = input.Stream; stream.Position = 0;
        Span<byte> header = stackalloc byte[64];
        if (stream.Length < 88) throw new UserError("This file is not a supported Windows executable.");
        stream.ReadExactly(header);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
        if (header[0] != 'M' || header[1] != 'Z' || offset < 64 || offset > stream.Length - 24) throw new UserError("This file is not a supported Windows executable.");
        stream.Position = offset; stream.ReadExactly(header[..24]);
        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]); ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[22..]);
        if (!header[..4].SequenceEqual(new byte[] { 80,69,0,0 }) || machine is not 0x14c and not 0x8664 and not 0xaa64 and not 0xa641 || (flags & 2) == 0 || (flags & 0x2000) != 0)
            throw new UserError("This file is not a supported Windows executable.");
        stream.Position = 0;
    }
    public static AppLaunch Open(AppRoute route, IReadOnlyList<string> files, CancellationToken token)
    {
        route.Validate(); token.ThrowIfCancellationRequested();
        if (files.Count is < 1 or > 100) throw new UserError("Choose up to 100 files at a time.");
        var executable = NativeFiles.OpenRead(route.Executable);
        try
        {
            ValidateExecutable(executable);
            foreach (string file in files) { token.ThrowIfCancellationRequested(); NativeFiles.ValidateInput(file); }
            return new(route,executable,files.ToArray());
        }
        catch { executable.Dispose(); throw; }
    }
    public static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> files, bool copyToClipboard = false)
    {
        WindowsPathPolicy.Validate(executable);
        if (files.Count is < 1 or > 100) throw new UserError("Choose up to 100 files at a time.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory };
        foreach (string file in files) { WindowsPathPolicy.Validate(file); if (!copyToClipboard) info.ArgumentList.Add(file); }
        // CreateProcess's UTF-16 command line (including terminating NUL) is bounded.
        if (!copyToClipboard && executable.Length + files.Sum(x => x.Length + 3) > 30000) throw new UserError("The selected paths are too long to pass to one application. Choose fewer files.");
        return info;
    }
    public void ValidateFiles(CancellationToken token)
    {
        foreach (string file in Files) { token.ThrowIfCancellationRequested(); NativeFiles.ValidateInput(file); }
    }
    public void Launch(CancellationToken token)
    {
        ValidateFiles(token);
        var info = StartInfo(Route.Executable,Files,Route.CopyToClipboard); token.ThrowIfCancellationRequested();
        // Only the executable is held through process creation. The recipient must be able
        // to open/edit the selected files; it owns all subsequent path reads and writes.
        using var process = Process.Start(info) ?? throw new UserError("Windows could not start this application.");
    }
    public void Dispose() => executable.Dispose();
}
