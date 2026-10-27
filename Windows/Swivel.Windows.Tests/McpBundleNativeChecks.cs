using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Swivel.Windows;

/// The Claude Desktop extension's launcher (Packaging/mcpb/index.js), run by Node.js as Claude Desktop
/// runs it: it starts only Swivel.exe --mcp and passes the conversation through, and refuses anything
/// else with a message. Needs node.exe on PATH (GitHub's Windows runners have it).
internal static class McpBundleNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check)
    {
        check(ClaudeExtension.Bundle == Path.Combine(AppContext.BaseDirectory, "Swivel.mcpb"), "Native MCPB is looked for beside Swivel.exe");
        string? node = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim().Trim('"'), "node.exe")).FirstOrDefault(File.Exists);
        if (node is null) { Console.WriteLine("SKIP: node.exe is not on PATH; the Claude Desktop extension launcher was not run."); return; }
        var windows = new DirectoryInfo(AppContext.BaseDirectory);
        while (windows is not null && !File.Exists(Path.Combine(windows.FullName, "Packaging", "mcpb", "index.js"))) windows = windows.Parent;
        if (windows is null) { check(false, "Native MCPB launcher found in the repository"); return; }
        string launcher = Path.Combine(windows.FullName, "Packaging", "mcpb", "index.js");
        string swivel = Path.Combine(AppContext.BaseDirectory, "Swivel.exe");
        string apps = Path.Combine(directory, "mcpb-localappdata"); Directory.CreateDirectory(apps);

        (int Code, string Output, string Error) Launch(string? chosen, string input)
        {
            var start = new ProcessStartInfo(node)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add(launcher);
            if (chosen is null) start.Environment.Remove("SWIVEL_EXE"); else start.Environment["SWIVEL_EXE"] = chosen;
            start.Environment["LOCALAPPDATA"] = apps; // no MSIX alias in there
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { process.StandardInput.Write(input); process.StandardInput.Close(); } catch (IOException) { } // a refusal may exit first
            if (!process.WaitForExit(60_000)) { process.Kill(true); return (-1, "", "timed out"); }
            process.WaitForExit();
            return (process.ExitCode, output.Result, error.Result);
        }

        check(File.Exists(swivel), "Native MCPB test has the built Swivel.exe beside it");
        var stopwatch = Stopwatch.StartNew();
        var served = Launch(swivel, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}\n"
            + "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n");
        var replies = served.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(windows.FullName, "Packaging", "mcpb", "manifest.json")))!;
        check(served.Code == 0 && replies.Length == 2 && (int?)replies[0]["id"] == 1 && (string?)replies[0]["result"]!["protocolVersion"] == "2025-06-18"
            && ((JsonArray)replies[1]["result"]!["tools"]!).Select(t => (string)t!["name"]!).SequenceEqual(((JsonArray)manifest["tools"]!).Select(t => (string)t!["name"]!)),
            "Native MCPB launcher starts the chosen Swivel.exe --mcp, passes initialize and tools/list through and exits when input ends: code " + served.Code + ", " + served.Error.Trim());
        Console.WriteLine($"MCPB launcher round trip through Node.js and Swivel.exe: {stopwatch.ElapsedMilliseconds} ms");

        const string mustBeSwivel = "The Swivel.exe setting must be the full path of Swivel.exe on a local drive";
        foreach (string wrong in new[] { Path.Combine(Environment.SystemDirectory, "notepad.exe"), "Swivel.exe", @"\\localhost\c$\Swivel.exe", swivel + ".bak" })
        {
            var refused = Launch(wrong, "");
            check(refused.Code == 1 && refused.Output.Length == 0 && refused.Error.StartsWith(mustBeSwivel, StringComparison.Ordinal), "Native MCPB launcher refuses " + wrong + ": " + refused.Error.Trim());
        }
        string alias = Path.Combine(apps, "Microsoft", "WindowsApps", "swivel-preview.exe");
        foreach (string? unset in new[] { null, "", "${user_config.swivel_exe}" })
        {
            var missing = Launch(unset, "");
            check(missing.Code == 1 && missing.Error.StartsWith("Swivel was not found at " + alias + ". Install the Swivel MSIX package", StringComparison.Ordinal),
                "Native MCPB launcher looks for the MSIX alias when no Swivel.exe is chosen (" + (unset ?? "unset") + "): " + missing.Error.Trim());
        }
        string absent = Path.Combine(apps, "nowhere", "Swivel.exe");
        var gone = Launch(absent, "");
        check(gone.Code == 1 && gone.Error.StartsWith("Swivel was not found at " + absent, StringComparison.Ordinal), "Native MCPB launcher reports a chosen Swivel.exe that is missing");
    }
}
