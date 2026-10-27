using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Swivel.Core;
using Swivel.Windows;

internal static class McpNativeChecks
{
    internal static void Run(string directory, Action<bool, string> check, Action<Action, string> reject)
    {
        // Windows-only: real files, the Recycle Bin and a test-only registry value.
        string root = Path.Combine(directory, "mcp"); Directory.CreateDirectory(root);
        string photo = Path.Combine(root, "photo.png");
        var bitmap = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)200, 4 * 3 * 4).ToArray(), 16);
        using (var file = File.Create(photo)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file); }
        string value = "AgentHistoryTest-" + Guid.NewGuid().ToString("N");
        try
        {
            var session = new McpSession(new McpHost(() => true, value), "test");
            JsonObject Call(string tool, JsonObject arguments)
                => (JsonObject)JsonNode.Parse(session.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments } }.ToJsonString())!)!["result"]!;
            var inspected = Call("inspect_files", new JsonObject { ["paths"] = new JsonArray(photo, Path.Combine(root, "missing.png"), root) });
            var files = (JsonArray)inspected["structuredContent"]!["files"]!;
            check(files[0]!["ok"]!.GetValue<bool>() && !files[1]!["ok"]!.GetValue<bool>() && !files[2]!["ok"]!.GetValue<bool>(), "Native MCP inspect accepts a file and refuses missing paths and folders");
            var plan = Call("plan_action", new JsonObject { ["action"] = "convert.bmp", ["paths"] = new JsonArray(photo) })["structuredContent"]!;
            var applied = Call("apply_plan", new JsonObject { ["plan_id"] = (string)plan["plan_id"]! });
            string output = (string)applied["structuredContent"]!["outputs"]![0]!;
            check(!applied["isError"]!.GetValue<bool>() && output == Path.Combine(root, "photo converted.bmp") && File.Exists(output), "Native MCP apply writes a new BMP beside the original: " + applied["structuredContent"]!.ToJsonString());
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Swivel\WindowsPreview")) check(key?.GetValue(value) is string json && json.Contains("convert.bmp"), "Native MCP history saved in its own registry value");
            var dry = Call("undo", new JsonObject { ["dry_run"] = true })["structuredContent"]!;
            check((string?)dry["results"]![0]!["result"] == "would move to the Recycle Bin" && File.Exists(output), "Native MCP undo dry run leaves the file");
            var undone = Call("undo", new JsonObject())["structuredContent"]!;
            check(undone["complete"]!.GetValue<bool>() && !File.Exists(output) && File.Exists(photo), "Native MCP undo moves the output to the Recycle Bin: " + undone.ToJsonString());

            // An output edited after apply is left alone.
            var plan2 = Call("plan_action", new JsonObject { ["action"] = "image.rotate", ["paths"] = new JsonArray(photo) })["structuredContent"]!;
            string rotated = (string)Call("apply_plan", new JsonObject { ["plan_id"] = (string)plan2["plan_id"]! })["structuredContent"]!["outputs"]![0]!;
            File.AppendAllText(rotated, "x");
            var refused = Call("undo", new JsonObject())["structuredContent"]!;
            check((string?)refused["results"]![0]!["result"] == "changed since apply; left alone" && File.Exists(rotated), "Native MCP undo leaves an edited output alone");

            // Read-only tools on real files: a generated QR PNG, a plain photo, a text document,
            // and OCR, which needs the MSIX package identity this test process does not have.
            var code = QrCode.Encode("Swivel MCP 读码", QrCorrection.Medium); int side = (code.Size + 8) * 8;
            string qr = Path.Combine(root, "code.png");
            using (var file = File.Create(qr)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null, code.Raster(), side))); encoder.Save(file); }
            var read = Call("read_codes", new JsonObject { ["paths"] = new JsonArray(qr, photo) })["structuredContent"]!;
            check((string?)read["codes"]![0]!["value"] == "Swivel MCP 读码" && (string?)read["codes"]![0]!["correction"] == "M" && ((JsonArray)read["codes"]!).Count == 1
                && (string?)read["not_read"]![0]!["path"] == photo, "Native MCP read_codes reads a generated QR PNG and reports a plain image as not read: " + read.ToJsonString());
            string notes = Path.Combine(root, "notes.txt"); File.WriteAllText(notes, "Line one\r\n第二行", new System.Text.UTF8Encoding(false));
            var text = (JsonArray)Call("extract_text", new JsonObject { ["paths"] = new JsonArray(notes, qr) })["structuredContent"]!["files"]!;
            check((string?)text[0]!["text"] == "Line one\r\n第二行" && (NativeIdentity.HasPackage ? text[1]!["ok"] is not null : (string?)text[1]!["error"] == McpSession.NoOcr),
                "Native MCP extract_text reads a text document, and OCR without the MSIX package explains why: " + text.ToJsonString());
            var ocrHost = new McpHost(() => true, value);
            check(NativeIdentity.HasPackage || ocrHost.TextLanguages.Count == 0, "Native MCP offers no OCR languages without the MSIX package");
            // DOCX, RTF and HTML written by Swivel's own writers come back exactly as the
            // document reader reads them.
            var formats = new[] { (TextExport.Docx, "docx"), (TextExport.Rtf, "rtf"), (TextExport.Html, "html") };
            var documents = new JsonArray();
            foreach (var (format, extension) in formats)
            {
                string document = Path.Combine(root, "notes." + extension);
                using (var file = File.Create(document)) DocumentText.Write(file, "Line one\n第二行 & <tag>", format, default);
                documents.Add(document);
            }
            var read3 = (JsonArray)Call("extract_text", new JsonObject { ["paths"] = documents, ["language"] = "xx-YY" })["structuredContent"]!["files"]!;
            for (int i = 0; i < formats.Length; i++)
            {
                string expected; using (var file = File.OpenRead((string)documents[i]!)) expected = DocumentText.Read(file, formats[i].Item2, default).Text;
                check(read3[i]!["ok"]!.GetValue<bool>() && (string?)read3[i]!["text"] == expected && expected.Contains("第二行 & <tag>"), "Native MCP extract_text reads " + formats[i].Item2 + " through the document reader: " + read3[i]!.ToJsonString());
            }
        }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Swivel\WindowsPreview", true);
            key?.DeleteValue(value, false);
        }

        // The real exe over pipes: a GUI-subsystem app still serves stdin/stdout and exits at end of input.
        string tests = AppContext.BaseDirectory;
        string[] candidates = { Path.Combine(tests, "Swivel.exe"), Path.GetFullPath(Path.Combine(tests, "..", "..", "..", "..", "Swivel.Windows", "bin", "Release", Path.GetFileName(tests.TrimEnd('\\')), "Swivel.exe")) };
        string? exe = candidates.FirstOrDefault(c => File.Exists(c) && File.Exists(Path.ChangeExtension(c, ".runtimeconfig.json")));
        if (exe is null) { check(false, "Native MCP test needs a runnable Swivel.exe: " + string.Join(" | ", candidates)); return; }
        var start = new ProcessStartInfo(exe, "--mcp") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(start)!;
        process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\"}}");
        process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        process.StandardInput.Close();
        string first = process.StandardOutput.ReadLine() ?? "", second = process.StandardOutput.ReadLine() ?? "";
        bool exited = process.WaitForExit(15000);
        if (!exited) process.Kill();
        check(first.Contains("\"protocolVersion\":\"2025-11-25\"") && second.Contains("\"apply_plan\"") && exited && process.ExitCode == 0, "Native Swivel.exe --mcp answers over pipes and exits at end of input: " + first.Length + "/" + second.Length + " exit " + (exited ? process.ExitCode : -1));
    }
}
