using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Swivel.Core;

namespace Swivel.Windows;

/// The Claude Desktop extension that ships beside Swivel.exe (Swivel.mcpb, built from Packaging/mcpb):
/// handed to the app registered for .mcpb files (normally Claude Desktop), as a double-click in Explorer
/// would, and that app shows its own install dialog. With no registered app Windows asks which app to use.
/// Swivel passes no arguments and runs nothing else.
internal static class ClaudeExtension
{
    public const string FileName = "Swivel.mcpb";
    public static string Bundle => Path.Combine(AppContext.BaseDirectory, FileName);

    public static void Open()
    {
        string bundle = Bundle;
        if (!File.Exists(bundle)) throw new UserError("This build has no Claude Desktop extension. Use Copy Agent Tools Setup instead.");
        try { using var process = Process.Start(new ProcessStartInfo(bundle) { UseShellExecute = true, WorkingDirectory = Environment.SystemDirectory }); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1155) // ERROR_NO_ASSOCIATION
        { throw new UserError("No app on this PC opens Claude Desktop extensions (.mcpb). Install Claude Desktop, or use Copy Agent Tools Setup."); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or FileNotFoundException)
        { throw new UserError("Windows could not open the Claude Desktop extension."); }
    }
}
