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
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, uint length);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newLink, string existing, IntPtr security);

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

            // plan_action document conversions: a text file to DOCX and ODT, a DOCX to TXT, each read
            // back through the document reader; undo recycles them. A stale stamp makes nothing.
            string Read(string path) { using var file = File.OpenRead(path); return DocumentText.Read(file, Path.GetExtension(path).TrimStart('.'), default).Text; }
            var toDocx = Call("plan_action", new JsonObject { ["action"] = "document.docx", ["paths"] = new JsonArray(notes) })["structuredContent"]!;
            var docxMade = Call("apply_plan", new JsonObject { ["plan_id"] = (string?)toDocx["plan_id"] })["structuredContent"]!;
            string notesDocx = Path.Combine(root, "notes text.docx");
            check((int?)docxMade["completed"] == 1 && (string?)docxMade["outputs"]![0] == notesDocx && Read(notesDocx).TrimEnd('\n') == "Line one\n第二行" && toDocx["note"] is JsonValue,
                "Native MCP plan_action saves a text file's text as DOCX beside it: " + docxMade.ToJsonString());
            var toOdtTxt = Call("apply_plan", new JsonObject { ["plan_id"] = (string?)Call("plan_action", new JsonObject { ["action"] = "document.odt", ["paths"] = new JsonArray(notes) })["structuredContent"]!["plan_id"] })["structuredContent"]!;
            check((string?)toOdtTxt["outputs"]![0] == Path.Combine(root, "notes text.odt") && Read(Path.Combine(root, "notes text.odt")).TrimEnd('\n') == "Line one\n第二行", "Native MCP plan_action saves text as ODT");
            string sourceDocx = (string)documents[0]!;
            var docxToTxt = Call("apply_plan", new JsonObject { ["plan_id"] = (string?)Call("plan_action", new JsonObject { ["action"] = "document.txt", ["paths"] = new JsonArray(sourceDocx) })["structuredContent"]!["plan_id"] })["structuredContent"]!;
            check((string?)docxToTxt["outputs"]![0] == Path.Combine(root, "notes text.txt") && File.ReadAllText(Path.Combine(root, "notes text.txt")) == Read(sourceDocx), "Native MCP plan_action saves a DOCX's text as UTF-8 TXT: " + docxToTxt.ToJsonString());
            var wrongKind = Call("plan_action", new JsonObject { ["action"] = "document.txt", ["paths"] = new JsonArray(photo) });
            check(wrongKind["isError"]!.GetValue<bool>(), "Native MCP refuses a document action for an image");
            var stale = NativeFiles.InspectStamp(notes) with { WriteTime = 1 };
            int before = Directory.GetFiles(root).Length;
            string Refusal(Action run) { try { run(); return "ran"; } catch (Exception error) { return error.Message; } }
            check(Refusal(() => new McpHost(() => true, value).Run("document.rtf", notes, stale, default)) == McpSession.Changed, "Native MCP Run refuses a document whose held stamp no longer matches the plan");
            check(Refusal(() => new McpHost(() => true, value).Run("image.gray", photo, NativeFiles.InspectStamp(photo) with { Size = 1 }, default)) == McpSession.Changed, "Native MCP Run refuses an image whose held stamp no longer matches the plan");
            check(Refusal(() => new McpHost(() => true, value).Compress(photo, 100_000, NativeFiles.InspectStamp(photo) with { Size = 1 }, default)) == McpSession.Changed, "Native MCP Compress refuses an image whose held stamp no longer matches the plan");
            check(Directory.GetFiles(root).Length == before, "Native MCP a refused stale run makes no file");
            var docsBack = Call("undo", new JsonObject())["structuredContent"]!;
            check(docsBack["complete"]!.GetValue<bool>() && !File.Exists(Path.Combine(root, "notes text.txt")) && File.Exists(sourceDocx), "Native MCP undo recycles a document conversion: " + docsBack.ToJsonString());
            Call("undo", new JsonObject()); Call("undo", new JsonObject());
            check(!File.Exists(notesDocx) && !File.Exists(Path.Combine(root, "notes text.odt")) && File.Exists(notes), "Native MCP undo recycles the DOCX and ODT copies, the original kept");

            // plan_rename on real files: a chain onto a name another file gives up, a case-only
            // change, a name taken by a folder; apply, then undo puts every name back.
            string names = Path.Combine(root, "rename"); Directory.CreateDirectory(names);
            string r1 = Path.Combine(names, "r-001.txt"), r2 = Path.Combine(names, "r-002.txt"), lower = Path.Combine(names, "case.txt");
            File.WriteAllText(r1, "first"); File.WriteAllText(r2, "second"); File.WriteAllText(lower, "case");
            Directory.CreateDirectory(Path.Combine(names, "x r-001.txt"));
            var taken = Call("plan_rename", new JsonObject { ["paths"] = new JsonArray(r1, r2), ["prefix"] = "x " })["structuredContent"]!;
            check(!taken["can_apply"]!.GetValue<bool>() && taken["renames"]![0]!["issue"] is JsonValue && taken["renames"]![1]!["issue"] is null, "Native MCP plan_rename reports a name a folder already has: " + taken.ToJsonString());
            var chain = Call("plan_rename", new JsonObject { ["paths"] = new JsonArray(r1, r2, lower), ["find"] = "case", ["replace_with"] = "Case", ["base_name"] = "r", ["number"] = true, ["start"] = 2 })["structuredContent"]!;
            var chained = Call("apply_plan", new JsonObject { ["plan_id"] = (string)chain["plan_id"]! })["structuredContent"]!;
            string Name(string folder, string name) => Directory.GetFiles(folder, name).Select(Path.GetFileName).Single()!;
            check((int)chained["completed"]! == 3 && File.ReadAllText(Path.Combine(names, "r-002.txt")) == "first" && File.ReadAllText(Path.Combine(names, "r-003.txt")) == "second"
                && File.ReadAllText(Path.Combine(names, "r-004.txt")) == "case" && !File.Exists(r1) && !File.Exists(lower), "Native MCP apply_plan renames a chain on real files: " + chained.ToJsonString());
            var caseOnly = Call("plan_rename", new JsonObject { ["paths"] = new JsonArray(Path.Combine(names, "r-004.txt")), ["find"] = "r-004", ["replace_with"] = "R-004" })["structuredContent"]!;
            Call("apply_plan", new JsonObject { ["plan_id"] = (string)caseOnly["plan_id"]! });
            check(Name(names, "r-004.txt") == "R-004.txt", "Native MCP apply_plan makes a case-only change");
            var back = Call("undo", new JsonObject())["structuredContent"]!;
            check(back["complete"]!.GetValue<bool>() && Name(names, "r-004.txt") == "r-004.txt", "Native MCP undo reverts a case-only change: " + back.ToJsonString());
            back = Call("undo", new JsonObject())["structuredContent"]!;
            check(back["complete"]!.GetValue<bool>() && File.ReadAllText(r1) == "first" && File.ReadAllText(r2) == "second" && Name(names, "case.txt") == "case.txt" && !File.Exists(Path.Combine(names, "r-003.txt")),
                "Native MCP undo renames the chain back on real files: " + back.ToJsonString());

            // The path must spell the file's stored name: another case, or an 8.3 short name, is refused.
            var respelt = Call("plan_rename", new JsonObject { ["paths"] = new JsonArray(Path.Combine(names, "R-001.TXT")), ["prefix"] = "x" });
            check(respelt["isError"]!.GetValue<bool>() && ((string)respelt["structuredContent"]!["error"]!).Contains("named r-001.txt"), "Native MCP plan_rename refuses a path spelt in another case: " + respelt.ToJsonString());
            string longName = Path.Combine(names, "long name 2026.txt"); File.WriteAllText(longName, "long");
            var shortBuffer = new System.Text.StringBuilder(1024);
            string shortName = GetShortPathName(longName, shortBuffer, (uint)shortBuffer.Capacity) > 0 ? shortBuffer.ToString() : longName;
            if (Path.GetFileName(shortName) != Path.GetFileName(longName))
            {
                var viaShort = Call("plan_rename", new JsonObject { ["paths"] = new JsonArray(shortName), ["prefix"] = "x" });
                check(viaShort["isError"]!.GetValue<bool>() && ((string)viaShort["structuredContent"]!["error"]!).Contains("named long name 2026.txt"), "Native MCP plan_rename refuses an 8.3 short name: " + viaShort.ToJsonString());
            }
            // A read-only file can't be renamed (reported on its row) but doesn't block files around it that keep their names.
            string locked = Path.Combine(names, "locked.txt"); File.WriteAllText(locked, "locked"); File.SetAttributes(locked, FileAttributes.ReadOnly);
            var lockedRow = Call("plan_rename", new JsonObject { ["paths"] = new JsonArray(r1, locked), ["prefix"] = "y " })["structuredContent"]!;
            check(!lockedRow["can_apply"]!.GetValue<bool>() && ((string)lockedRow["renames"]![1]!["issue"]!).Contains("can't rename") && lockedRow["renames"]![0]!["issue"] is null, "Native MCP plan_rename reports a read-only file on its row: " + lockedRow.ToJsonString());
            var aroundLocked = Call("plan_rename", new JsonObject { ["paths"] = new JsonArray(r1, locked), ["find"] = "001", ["replace_with"] = "009" })["structuredContent"]!;
            var renamedAround = Call("apply_plan", new JsonObject { ["plan_id"] = (string)aroundLocked["plan_id"]! })["structuredContent"]!;
            check((int)renamedAround["total"]! == 1 && File.ReadAllText(Path.Combine(names, "r-009.txt")) == "first" && File.Exists(locked), "Native MCP apply_plan renames around a read-only file that keeps its name: " + renamedAround.ToJsonString());
            check(Call("undo", new JsonObject())["structuredContent"]!["complete"]!.GetValue<bool>() && File.ReadAllText(r1) == "first", "Native MCP undo of that rename");
            File.SetAttributes(locked, FileAttributes.Normal);

            // A swap on real files, a dry run of its undo, and the undo.
            var swap = Call("plan_rename", new JsonObject { ["paths"] = new JsonArray(r2, r1), ["base_name"] = "r", ["number"] = true })["structuredContent"]!;
            Call("apply_plan", new JsonObject { ["plan_id"] = (string)swap["plan_id"]! });
            check(File.ReadAllText(r1) == "second" && File.ReadAllText(r2) == "first", "Native MCP apply_plan swaps two names on real files");
            var dryBack = Call("undo", new JsonObject { ["dry_run"] = true })["structuredContent"]!;
            check(((JsonArray)dryBack["results"]!).All(r => (string?)r!["result"] == "would be renamed back") && File.ReadAllText(r1) == "second", "Native MCP undo dry run of a swap changes nothing: " + dryBack.ToJsonString());
            check(Call("undo", new JsonObject())["structuredContent"]!["complete"]!.GetValue<bool>() && File.ReadAllText(r1) == "first" && File.ReadAllText(r2) == "second", "Native MCP undo swaps the names back on real files");

            // After planning: a new name taken, or the file edited, refuses the apply without renaming anything.
            var late = Call("plan_rename", new JsonObject { ["paths"] = new JsonArray(r1), ["prefix"] = "z " })["structuredContent"]!;
            File.WriteAllText(Path.Combine(names, "z r-001.txt"), "planted");
            var occupied = Call("apply_plan", new JsonObject { ["plan_id"] = (string)late["plan_id"]! });
            check(occupied["isError"]!.GetValue<bool>() && File.ReadAllText(r1) == "first" && File.ReadAllText(Path.Combine(names, "z r-001.txt")) == "planted", "Native MCP apply_plan refuses a new name taken after planning: " + occupied.ToJsonString());
            File.Delete(Path.Combine(names, "z r-001.txt"));
            late = Call("plan_rename", new JsonObject { ["paths"] = new JsonArray(r1), ["prefix"] = "z " })["structuredContent"]!;
            File.AppendAllText(r1, "+");
            var edited = Call("apply_plan", new JsonObject { ["plan_id"] = (string)late["plan_id"]! });
            check(edited["isError"]!.GetValue<bool>() && File.Exists(r1) && !File.Exists(Path.Combine(names, "z r-001.txt")), "Native MCP apply_plan refuses a file edited after planning: " + edited.ToJsonString());

            // Undo refuses every file when one was edited, or when an old name is taken.
            var both = Call("plan_rename", new JsonObject { ["paths"] = new JsonArray(r1, r2), ["prefix"] = "e " })["structuredContent"]!;
            Call("apply_plan", new JsonObject { ["plan_id"] = (string)both["plan_id"]! });
            File.WriteAllText(r1, "new file at the old name");
            var blocked = Call("undo", new JsonObject())["structuredContent"]!;
            check((string?)blocked["results"]![0]!["result"] == "its old name is taken now; left alone" && File.Exists(Path.Combine(names, "e r-002.txt")), "Native MCP undo refuses when an old name is taken: " + blocked.ToJsonString());
            File.Delete(r1); File.AppendAllText(Path.Combine(names, "e r-002.txt"), "+");
            blocked = Call("undo", new JsonObject())["structuredContent"]!;
            check((string?)blocked["results"]![1]!["result"] == "changed since the rename; left alone" && File.Exists(Path.Combine(names, "e r-001.txt")), "Native MCP undo refuses every file when one was edited: " + blocked.ToJsonString());

            // plan_move on real files: two folders into a third, row issues, refusals, apply and undo.
            string moves = Path.Combine(root, "move"), in1 = Path.Combine(moves, "in1"), in2 = Path.Combine(moves, "in2"), dest = Path.Combine(moves, "out");
            foreach (string folder in new[] { in1, in2, dest }) Directory.CreateDirectory(folder);
            string ma = Path.Combine(in1, "a.txt"), mb = Path.Combine(in2, "b.txt"), mc = Path.Combine(in1, "c.txt"), here = Path.Combine(dest, "here.txt");
            File.WriteAllText(ma, "a"); File.WriteAllText(mb, "b"); File.WriteAllText(mc, "c"); File.WriteAllText(here, "here");
            JsonObject Move(params string[] paths) => (JsonObject)Call("plan_move", new JsonObject { ["paths"] = new JsonArray(paths.Select(x => (JsonNode)x).ToArray()), ["destination"] = dest })["structuredContent"]!;
            var nowhere = Call("plan_move", new JsonObject { ["paths"] = new JsonArray(ma), ["destination"] = Path.Combine(moves, "none") });
            var ontoFile = Call("plan_move", new JsonObject { ["paths"] = new JsonArray(ma), ["destination"] = here });
            check(nowhere["isError"]!.GetValue<bool>() && ontoFile["isError"]!.GetValue<bool>() && ((string)ontoFile["structuredContent"]!["error"]!).StartsWith("destination: ") && File.Exists(ma),
                "Native MCP plan_move refuses a missing folder and a file as the destination: " + nowhere.ToJsonString() + " " + ontoFile.ToJsonString());
            var moveRespelt = Call("plan_move", new JsonObject { ["paths"] = new JsonArray(Path.Combine(in1, "A.TXT")), ["destination"] = dest });
            check(moveRespelt["isError"]!.GetValue<bool>() && ((string)moveRespelt["structuredContent"]!["error"]!).Contains("named a.txt"), "Native MCP plan_move refuses a path spelt in another case: " + moveRespelt.ToJsonString());
            string longMove = Path.Combine(in1, "long move name 2026.txt"); File.WriteAllText(longMove, "long");
            string shortMove = GetShortPathName(longMove, shortBuffer, (uint)shortBuffer.Capacity) > 0 ? shortBuffer.ToString() : longMove;
            if (Path.GetFileName(shortMove) != Path.GetFileName(longMove))
            {
                var moveViaShort = Call("plan_move", new JsonObject { ["paths"] = new JsonArray(shortMove), ["destination"] = dest });
                check(moveViaShort["isError"]!.GetValue<bool>() && ((string)moveViaShort["structuredContent"]!["error"]!).Contains("named long move name 2026.txt"), "Native MCP plan_move refuses an 8.3 short name: " + moveViaShort.ToJsonString());
            }
            File.Delete(longMove);

            // Row issues: already there (also through the folder's 8.3 spelling), a name taken by a file or a folder, a read-only file.
            File.WriteAllText(Path.Combine(dest, "c.txt"), "taken"); Directory.CreateDirectory(Path.Combine(dest, "d.txt"));
            string md = Path.Combine(in2, "d.txt"), ro = Path.Combine(in2, "ro.txt"); File.WriteAllText(md, "d"); File.WriteAllText(ro, "ro"); File.SetAttributes(ro, FileAttributes.ReadOnly);
            var rowsMove = Move(ma, here, mc, md, ro);
            check(!rowsMove["can_apply"]!.GetValue<bool>() && rowsMove["plan_id"] is null && rowsMove["moves"]![0]!["issue"] is null && (string?)rowsMove["moves"]![1]!["issue"] == "Already in this folder."
                && ((string)rowsMove["moves"]![2]!["issue"]!).Contains("already in the destination") && ((string)rowsMove["moves"]![3]!["issue"]!).Contains("already in the destination")
                && ((string)rowsMove["moves"]![4]!["issue"]!).Contains("can't move") && File.Exists(ma), "Native MCP plan_move reports each problem on its row: " + rowsMove.ToJsonString());
            string shortDest = GetShortPathName(dest, shortBuffer, (uint)shortBuffer.Capacity) > 0 ? shortBuffer.ToString() : dest;
            var viaShortDest = (JsonObject)Call("plan_move", new JsonObject { ["paths"] = new JsonArray(here), ["destination"] = shortDest.ToUpperInvariant() == dest.ToUpperInvariant() ? dest : shortDest })["structuredContent"]!;
            check((string?)viaShortDest["moves"]![0]!["issue"] == "Already in this folder.", "Native MCP plan_move sees a file already in the folder however the folder is spelt: " + viaShortDest.ToJsonString());
            File.SetAttributes(ro, FileAttributes.Normal); File.Delete(ro); File.Delete(md); File.Delete(Path.Combine(dest, "c.txt")); Directory.Delete(Path.Combine(dest, "d.txt"));

            // Apply, a dry run of its undo, and the undo.
            var movePlan = Move(ma, mb);
            check(movePlan["can_apply"]!.GetValue<bool>() && (string?)movePlan["moves"]![1]!["to"] == Path.Combine(dest, "b.txt") && File.Exists(ma), "Native MCP plan_move lists the new paths and changes nothing: " + movePlan.ToJsonString());
            var moveApplied = Call("apply_plan", new JsonObject { ["plan_id"] = (string)movePlan["plan_id"]! })["structuredContent"]!;
            check((int)moveApplied["completed"]! == 2 && !File.Exists(ma) && !File.Exists(mb) && File.ReadAllText(Path.Combine(dest, "a.txt")) == "a" && File.ReadAllText(Path.Combine(dest, "b.txt")) == "b",
                "Native MCP apply_plan moves the files on real disks: " + moveApplied.ToJsonString());
            var dryMove = Call("undo", new JsonObject { ["dry_run"] = true })["structuredContent"]!;
            check(((JsonArray)dryMove["results"]!).All(r => (string?)r!["result"] == "would be moved back") && !File.Exists(ma), "Native MCP undo dry run of a move changes nothing: " + dryMove.ToJsonString());
            var movedBack = Call("undo", new JsonObject())["structuredContent"]!;
            check(movedBack["complete"]!.GetValue<bool>() && File.ReadAllText(ma) == "a" && File.ReadAllText(mb) == "b" && !File.Exists(Path.Combine(dest, "a.txt")), "Native MCP undo moves the files back: " + movedBack.ToJsonString());

            // After planning: a destination name taken, or a file edited, refuses the apply without moving anything.
            var lateMove = Move(ma, mb);
            File.WriteAllText(Path.Combine(dest, "b.txt"), "planted");
            var plantedMove = Call("apply_plan", new JsonObject { ["plan_id"] = (string)lateMove["plan_id"]! });
            check(plantedMove["isError"]!.GetValue<bool>() && File.Exists(ma) && File.Exists(mb) && !File.Exists(Path.Combine(dest, "a.txt")) && File.ReadAllText(Path.Combine(dest, "b.txt")) == "planted",
                "Native MCP apply_plan refuses a move onto a name taken after planning: " + plantedMove.ToJsonString());
            File.Delete(Path.Combine(dest, "b.txt"));
            lateMove = Move(ma, mb); File.AppendAllText(mb, "+");
            var editedMove = Call("apply_plan", new JsonObject { ["plan_id"] = (string)lateMove["plan_id"]! });
            check(editedMove["isError"]!.GetValue<bool>() && File.Exists(ma) && File.Exists(mb) && !File.Exists(Path.Combine(dest, "a.txt")), "Native MCP apply_plan refuses a file edited after planning: " + editedMove.ToJsonString());

            // Undo goes file by file: an old name taken, or a hard link left at it, keeps that file where it is.
            var three = Move(ma, mb, mc);
            Call("apply_plan", new JsonObject { ["plan_id"] = (string)three["plan_id"]! });
            File.WriteAllText(ma, "new file at the old name");
            check(CreateHardLink(mc, Path.Combine(dest, "c.txt"), IntPtr.Zero), "Native MCP test makes a hard link at an old path");
            var someBack = Call("undo", new JsonObject())["structuredContent"]!;
            check((string?)someBack["results"]![0]!["result"] == "its old name is taken now; left alone" && (string?)someBack["results"]![1]!["result"] == "moved back"
                && (string?)someBack["results"]![2]!["result"] == "its old name is taken now; left alone" && !someBack["complete"]!.GetValue<bool>()
                && File.ReadAllText(mb) == "b+" && File.ReadAllText(Path.Combine(dest, "a.txt")) == "a", "Native MCP undo moves back what it can, file by file: " + someBack.ToJsonString());
            File.Delete(ma); File.Delete(mc); File.AppendAllText(Path.Combine(dest, "a.txt"), "+");
            var restMove = Call("undo", new JsonObject())["structuredContent"]!;
            check((string?)restMove["results"]![0]!["result"] == "changed since the move; left alone" && (string?)restMove["results"]![1]!["result"] == "already undone"
                && (string?)restMove["results"]![2]!["result"] == "moved back" && File.ReadAllText(mc) == "c" && File.Exists(Path.Combine(dest, "a.txt")), "Native MCP undo leaves an edited file and moves the rest: " + restMove.ToJsonString());

            // A file with its archive bit cleared still moves (a rename may set it); a folder made at its old path takes the name.
            string me = Path.Combine(in1, "e.txt"); File.WriteAllText(me, "e"); File.SetAttributes(me, FileAttributes.Normal);
            var plainMoved = Call("apply_plan", new JsonObject { ["plan_id"] = (string)Move(me)["plan_id"]! })["structuredContent"]!;
            check((int?)plainMoved["completed"] == 1 && File.ReadAllText(Path.Combine(dest, "e.txt")) == "e" && !File.Exists(me), "Native MCP apply_plan moves a file whose archive bit is cleared: " + plainMoved.ToJsonString());
            Directory.CreateDirectory(me);
            var folderThere = Call("undo", new JsonObject())["structuredContent"]!;
            check((string?)folderThere["results"]![0]!["result"] == "its old name is taken now; left alone" && File.Exists(Path.Combine(dest, "e.txt")), "Native MCP undo of a move sees a folder now at the old path: " + folderThere.ToJsonString());
            Directory.Delete(me);
            var eBack = Call("undo", new JsonObject())["structuredContent"]!;
            check(eBack["complete"]!.GetValue<bool>() && File.ReadAllText(me) == "e", "Native MCP undo moves it back once the folder is gone: " + eBack.ToJsonString());

            // The destination spelt in capitals, or by its 8.3 short path where it has one: the same folder, by identity.
            foreach (string spelling in new[] { dest.ToUpperInvariant(), shortDest }.Where(x => x != dest).Distinct())
            {
                var spelt = (JsonObject)Call("plan_move", new JsonObject { ["paths"] = new JsonArray(me), ["destination"] = spelling })["structuredContent"]!;
                var speltMoved = Call("apply_plan", new JsonObject { ["plan_id"] = (string?)spelt["plan_id"] })["structuredContent"]!;
                check((int?)speltMoved["completed"] == 1 && Name(dest, "e.txt") == "e.txt" && !File.Exists(me), "Native MCP apply_plan moves into a destination spelt " + spelling + ": " + speltMoved.ToJsonString());
                var speltBack = Call("undo", new JsonObject())["structuredContent"]!;
                check(speltBack["complete"]!.GetValue<bool>() && File.ReadAllText(me) == "e", "Native MCP undo of a move into a destination spelt another way: " + speltBack.ToJsonString());
            }
            // Undo leaves a file whose old folder is gone, and says so.
            var orphan = Call("apply_plan", new JsonObject { ["plan_id"] = (string?)Move(me)["plan_id"] })["structuredContent"]!;
            Directory.Delete(in1, true);
            var noFolder = Call("undo", new JsonObject { ["dry_run"] = true })["structuredContent"]!;
            check((int?)orphan["completed"] == 1 && ((string)noFolder["results"]![0]!["result"]!).StartsWith("its old folder is unavailable") && File.Exists(Path.Combine(dest, "e.txt")),
                "Native MCP undo of a move says when the old folder is gone: " + noFolder.ToJsonString());

            // plan_compress on real files: a half-transparent photo kept whole on white, black and
            // white noise that must shrink, an unreachable target, and two generated videos.
            string squeeze = Path.Combine(root, "squeeze"); Directory.CreateDirectory(squeeze);
            string tinted = Path.Combine(squeeze, "tinted.png"), noise = Path.Combine(squeeze, "noise.png");
            File.Copy(photo, tinted);
            var dots = new byte[1600 * 1200 * 4]; var dice = new Random(7);
            for (int i = 0; i < dots.Length; i += 4) { byte level = dice.Next(2) == 0 ? (byte)0 : (byte)255; dots[i] = dots[i + 1] = dots[i + 2] = level; dots[i + 3] = 255; }
            using (var file = File.Create(noise)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1600, 1200, 96, 96, PixelFormats.Bgra32, null, dots, 1600 * 4))); encoder.Save(file); }
            BitmapSource Jpeg(string path) { using var stream = File.OpenRead(path); var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); return decoder.Frames[0]; }
            var squeezePlan = Call("plan_compress", new JsonObject { ["paths"] = new JsonArray(tinted, noise), ["target_bytes"] = 100_000 })["structuredContent"]!;
            var squeezed = Call("apply_plan", new JsonObject { ["plan_id"] = (string?)squeezePlan["plan_id"] })["structuredContent"]!;
            string tintedOut = Path.Combine(squeeze, "tinted compressed.jpg"), noiseOut = Path.Combine(squeeze, "noise compressed.jpg");
            var tintedJpeg = new FormatConvertedBitmap(Jpeg(tintedOut), PixelFormats.Bgra32, null, 0); var tintedPixels = new byte[4 * 3 * 4]; tintedJpeg.CopyPixels(tintedPixels, 16, 0);
            var noiseJpeg = Jpeg(noiseOut);
            Console.WriteLine($"MCP compress: noise 1600×1200 → {noiseJpeg.PixelWidth}×{noiseJpeg.PixelHeight}, {new FileInfo(noiseOut).Length} bytes");
            check((int?)squeezed["completed"] == 2 && tintedJpeg.PixelWidth == 4 && tintedJpeg.PixelHeight == 3 && tintedPixels[0] is >= 207 and <= 217 && new FileInfo(tintedOut).Length <= 100_000,
                "Native MCP plan_compress keeps a small image whole, half-transparent areas on white: " + squeezed.ToJsonString() + " " + tintedPixels[0]);
            check(new FileInfo(noiseOut).Length <= 100_000 && noiseJpeg.PixelWidth < 1600 && noiseJpeg.PixelWidth >= 320 && Math.Abs(noiseJpeg.PixelWidth * 3 - noiseJpeg.PixelHeight * 4) <= 4,
                "Native MCP plan_compress shrinks an image that can't fit at its size, keeping its shape");
            int squeezeFiles = Directory.GetFiles(squeeze).Length;
            var hopeless = Call("apply_plan", new JsonObject { ["plan_id"] = (string?)Call("plan_compress", new JsonObject { ["paths"] = new JsonArray(noise), ["target_bytes"] = 1000 })["structuredContent"]!["plan_id"] });
            check(hopeless["isError"]!.GetValue<bool>() && ((string)hopeless["structuredContent"]!["error"]!).Contains("can't be reached") && Directory.GetFiles(squeeze).Length == squeezeFiles,
                "Native MCP plan_compress refuses an unreachable target and saves nothing: " + hopeless["structuredContent"]!.ToJsonString());

            string quietClip = Path.Combine(squeeze, "quiet.mp4"), toneClip = Path.Combine(squeeze, "tone.mp4"), tone = Path.Combine(squeeze, "tone.wav");
            using (var wav = new BinaryWriter(File.Create(tone)))
            {
                const int rate = 44100, count = rate * 6;
                wav.Write("RIFF"u8); wav.Write(36 + count * 2); wav.Write("WAVEfmt "u8); wav.Write(16); wav.Write((short)1); wav.Write((short)1); wav.Write(rate); wav.Write(rate * 2); wav.Write((short)2); wav.Write((short)16);
                wav.Write("data"u8); wav.Write(count * 2);
                for (int i = 0; i < count; i++) wav.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000));
            }
            SubtitleBurnNativeChecks.MakeVideo(quietClip, 6, null); SubtitleBurnNativeChecks.MakeVideo(toneClip, 6, tone);
            check(((JsonArray)Call("inspect_files", new JsonObject { ["paths"] = new JsonArray(quietClip) })["structuredContent"]!["files"]![0]!["tools"]!).Any(t => (string?)t == "plan_compress"), "Native MCP inspect_files offers plan_compress for a video");
            // The smallest target follows the length Windows reports (about 122,518 bytes for 6 s).
            long smallest = VideoBudget.MinimumBytes(VideoCompress.ProbeAsync(quietClip, default).GetAwaiter().GetResult());
            var small = Call("plan_compress", new JsonObject { ["paths"] = new JsonArray(quietClip), ["target_bytes"] = 1000 });
            check(small["isError"]!.GetValue<bool>() && ((string)small["structuredContent"]!["error"]!).Contains("needs at least " + smallest.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " bytes") && smallest is > 120_000 and < 125_000,
                "Native MCP plan_compress names the smallest target for a video's length: " + small["structuredContent"]!.ToJsonString());
            var clipPlan = Call("plan_compress", new JsonObject { ["paths"] = new JsonArray(quietClip, toneClip), ["target_bytes"] = 300_000 })["structuredContent"]!;
            var clock = Stopwatch.StartNew();
            var clips = Call("apply_plan", new JsonObject { ["plan_id"] = (string?)clipPlan["plan_id"] })["structuredContent"]!;
            string quietOut = Path.Combine(squeeze, "quiet compressed.mp4"), toneOut = Path.Combine(squeeze, "tone compressed.mp4");
            Console.WriteLine($"MCP compress: two 6 s 640×360 videos in {clock.ElapsedMilliseconds} ms; {new FileInfo(quietClip).Length} → {(File.Exists(quietOut) ? new FileInfo(quietOut).Length : -1)} and {new FileInfo(toneClip).Length} → {(File.Exists(toneOut) ? new FileInfo(toneOut).Length : -1)} bytes");
            check((int?)clips["completed"] == 2 && new FileInfo(quietOut).Length <= 300_000 && new FileInfo(toneOut).Length <= 300_000, "Native MCP plan_compress makes each video at most the target: " + clipPlan.ToJsonString() + " " + clips.ToJsonString());
            var quietProbe = VideoCompress.ProbeAsync(quietOut, default).GetAwaiter().GetResult();
            var toneProbe = VideoCompress.ProbeAsync(toneOut, default).GetAwaiter().GetResult();
            check(quietProbe is { Width: 640, Height: 360, HasSound: false } && Math.Abs(quietProbe.Seconds - 6) < 0.3 && toneProbe is { HasSound: true } && Math.Abs(toneProbe.Seconds - 6) < 0.3,
                "Native MCP compressed videos keep their size, length and sound: " + quietProbe + " " + toneProbe);
            GC.Collect(); GC.WaitForPendingFinalizers(); // the probes' WinRT objects may hold the files until collected
            var clipsBack = Call("undo", new JsonObject())["structuredContent"]!;
            check(clipsBack["complete"]!.GetValue<bool>() && !File.Exists(quietOut) && !File.Exists(toneOut) && File.Exists(quietClip) && File.Exists(toneClip), "Native MCP undo of plan_compress recycles the videos, originals kept: " + clipsBack.ToJsonString());
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
