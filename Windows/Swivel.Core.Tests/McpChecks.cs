using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Swivel.Core;

internal static class McpChecks
{
    private sealed class FakeHost : IMcpHost
    {
        public bool Enabled { get; set; } = true;
        public DateTime UtcNow { get; set; } = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        public readonly Dictionary<string, FileStamp> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Recycled = new();
        public string? History;
        public int Saves;
        public string? FailOn;
        private ulong next = 100;
        public FileStamp Add(string path) => Files[path] = new FileStamp(1, next++, 10, 1000, 2000);
        public FileStamp Inspect(string path) => Files.TryGetValue(Aliases.GetValueOrDefault(path, path), out var s) ? s : throw (path.EndsWith("denied.png") ? new UnauthorizedAccessException("Access is denied.") : new UserError("Choose a local regular file."));
        public readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase); // 8.3 short path → long path
        public readonly HashSet<string> Locked = new(StringComparer.OrdinalIgnoreCase); // read-only or hard-linked
        public string StoredName(string path)
        {
            string key = Files.Keys.First(k => string.Equals(k, Aliases.GetValueOrDefault(path, path), StringComparison.OrdinalIgnoreCase));
            return key[(key.LastIndexOf('\\') + 1)..];
        }
        public void CheckRename(string path) { if (Locked.Contains(path)) throw new UserError("Batch Rename does not accept read-only files, hard links or unavailable file identities."); }
        public string Run(string action, string path, CancellationToken token)
        {
            if (path == FailOn) throw Throw ?? new UserError("Could not read the image.");
            DuringRun?.Invoke();
            string output = System.IO.Path.ChangeExtension(path, null) + " converted." + ImageOutputInfo.Format(action);
            while (Files.ContainsKey(output)) output = output.Replace(" converted", " converted 2");
            Add(output); return output;
        }
        public readonly Dictionary<string, string> Texts = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Path, string? Language)> Recognized = new();
        public readonly List<string> Documents = new();
        public readonly List<string> Codes = new();
        public List<string> OcrLanguages = new() { "en-US", "zh-Hans-CN" };
        public int LanguageReads;
        public IReadOnlyList<string> TextLanguages { get { LanguageReads++; return OcrLanguages; } }
        public QrReadResult ReadCode(string path, CancellationToken token)
        {
            Codes.Add(path);
            return Texts.TryGetValue(path, out var text) ? new QrReadResult(text, 2, QrCorrection.Quartile, 3, 8) : throw new UserError(QrFlatRead.NoCode);
        }
        public string RecognizeText(string path, string? language, CancellationToken token) { Recognized.Add((path, language)); return Texts.TryGetValue(path, out var text) ? text : throw new UserError("No OCR language."); }
        public string ReadDocumentText(string path, CancellationToken token) { Documents.Add(path); return Texts.TryGetValue(path, out var text) ? text : throw new UserError("Not a valid document."); }
        public readonly HashSet<string> Taken = new(StringComparer.OrdinalIgnoreCase); // folders and other names
        public int NameChecks;
        public bool NameTaken(string path) { NameChecks++; return Files.ContainsKey(path) || Taken.Contains(path); }
        public enum RenameFailure { None, RolledBack, Stuck }
        public RenameFailure RenameFails;
        public readonly List<IReadOnlyList<RenamePair>> Renamed = new();
        public RenameOutcome Rename(IReadOnlyList<RenamePair> plan, IReadOnlyList<FileStamp> expected, CancellationToken token)
        {
            RenameRules.Validate(plan);
            for (int i = 0; i < plan.Count; i++)
                if (!Files.TryGetValue(plan[i].Source, out var s) || !s.SameObjectAndContent(expected[i])) throw new UserError("A file changed just now.");
            if (plan.Any(p => StoredName(p.Source) != p.Source[(p.Source.LastIndexOf('\\') + 1)..])) throw new UserError("A file's name changed just now.");
            if (plan.Any(p => Locked.Contains(p.Source))) throw new UserError("Batch Rename does not accept read-only files, hard links or unavailable file identities.");
            foreach (var pair in plan)
                if (pair.Changed && !plan.Any(p => p.Source.Equals(pair.Destination, StringComparison.OrdinalIgnoreCase)) && (Files.ContainsKey(pair.Destination) || Taken.Contains(pair.Destination)))
                    throw new UserError("A destination name is already occupied. Nothing was overwritten.");
            Renamed.Add(plan);
            var stamps = plan.Select(p => Files[p.Source]).ToArray();
            var error = new System.IO.IOException("The disk is busy.");
            if (RenameFails == RenameFailure.RolledBack) return new RenameOutcome(plan.Select((p, i) => new RenameLocation(p.Source, p.Source, stamps[i])).ToArray(), error, false);
            foreach (var pair in plan) Files.Remove(pair.Source);
            var at = plan.Select(p => p.Destination).ToArray();
            if (RenameFails == RenameFailure.Stuck)
            {
                // The first file's rollback failed at its staging name; the others went back.
                at = plan.Select(p => p.Source).ToArray();
                at[0] = plan[0].Source[..(plan[0].Source.LastIndexOf('\\') + 1)] + ".swivel-rename-stuck";
            }
            for (int i = 0; i < plan.Count; i++) Files[at[i]] = stamps[i];
            return new RenameOutcome(plan.Select((p, i) => new RenameLocation(p.Source, at[i], stamps[i])).ToArray(), RenameFails == RenameFailure.Stuck ? error : null, false);
        }
        public FileStamp? Stamp(string path) => Files.TryGetValue(path, out var s) ? s : null;
        public void Recycle(string path, FileStamp expected)
        {
            if (!Files[path].SameObjectAndContent(expected)) throw new UserError("The file changed.");
            Files.Remove(path); Recycled.Add(path);
        }
        public int Locks, Held;
        public IDisposable LockHistory() { Locks++; Held++; return new Release(this); }
        private sealed class Release(FakeHost host) : IDisposable { public void Dispose() => host.Held--; }
        public Action? DuringRun;
        public Exception? Throw;
        public string? LoadHistory() => History;
        public bool SaveFails;
        public void SaveHistory(string json) { if (SaveFails) throw new System.IO.IOException("The registry is full."); History = json; Saves++; }
    }

    private static JsonObject Rpc(McpSession session, string json) => (JsonObject)JsonNode.Parse(session.Handle(json)!)!;
    private static JsonObject Call(McpSession session, string tool, JsonObject arguments, int id = 1)
        => Rpc(session, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments } }.ToJsonString());
    private static JsonObject Body(JsonObject reply) => (JsonObject)reply["result"]!["structuredContent"]!;
    private static bool IsError(JsonObject reply) => reply["result"]!["isError"]!.GetValue<bool>();
    private static JsonArray Paths(params string[] paths) => new(paths.Select(p => (JsonNode)p).ToArray());

    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var host = new FakeHost(); var session = new McpSession(host, "0.86.0");
        var init = Rpc(session, "{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
        check((string?)init["id"] == "a" && (string?)init["result"]!["protocolVersion"] == "2025-06-18" && init["result"]!["capabilities"]!["tools"] is JsonObject, "MCP initialize echoes a supported version and string id");
        check((string?)Rpc(session, "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"1999-01-01\"}}")["result"]!["protocolVersion"] == "2025-11-25", "MCP initialize answers an unknown version with the latest it serves");
        check(session.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}") is null, "MCP notifications get no reply");
        check(Rpc(session, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}")["result"] is JsonObject, "MCP ping");
        check((int)Rpc(session, "{not json")["error"]!["code"]! == -32700, "MCP parse error");
        check((int)Rpc(session, "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]")["error"]!["code"]! == -32600, "MCP batches refused");
        check((int)Rpc(session, "{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"ping\"}")["error"]!["code"]! == -32600 && (int)Rpc(session, "{\"jsonrpc\":\"2.0\",\"id\":{},\"method\":\"ping\"}")["error"]!["code"]! == -32600, "MCP invalid requests refused");
        check((int)Rpc(session, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"resources/list\"}")["error"]!["code"]! == -32601, "MCP unknown method");
        check((int)Rpc(session, new string(' ', McpSession.MaxLineChars + 1))["error"]!["code"]! == -32600, "MCP oversized message refused");
        var tools = ((JsonArray)Rpc(session, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}")["result"]!["tools"]!).Select(t => (string)t!["name"]!).ToArray();
        check(tools.SequenceEqual(new[] { "list_actions", "inspect_files", "extract_text", "read_codes", "plan_action", "plan_rename", "apply_plan", "undo", "history" }), "MCP tool list");
        var listed = (JsonArray)Rpc(session, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}")["result"]!["tools"]!;
        check(listed.Where(t => (string)t!["name"]! is "extract_text" or "read_codes" or "plan_rename").All(t => t!["annotations"]!["readOnlyHint"]!.GetValue<bool>() && !t!["annotations"]!["destructiveHint"]!.GetValue<bool>()),
            "MCP extract_text and read_codes are marked read-only");
        check((int)Call(session, "format_disk", new JsonObject())["error"]!["code"]! == -32602, "MCP unknown tool is an invalid-params error");

        host.Enabled = false;
        check(IsError(Call(session, "list_actions", new JsonObject())) && ((string)Body(Call(session, "list_actions", new JsonObject()))["error"]!).Contains("Agent Tools is off"), "MCP tools refuse while Agent Tools is off");
        host.Enabled = true;
        check(((JsonArray)Body(Call(session, "list_actions", new JsonObject()))["actions"]!).Count == McpSession.Actions.Count, "MCP list_actions");

        string photo = @"C:\Users\me\Pictures\photo.jpg", doc = @"C:\Users\me\notes.txt";
        host.Add(photo); host.Add(doc);
        var inspected = (JsonArray)Body(Call(session, "inspect_files", new JsonObject { ["paths"] = Paths(photo, doc, @"C:\missing.png") }))["files"]!;
        check(inspected[0]!["ok"]!.GetValue<bool>() && ((JsonArray)inspected[0]!["actions"]!).Count == McpSession.Actions.Count && ((JsonArray)inspected[1]!["actions"]!).Count == 0 && !inspected[2]!["ok"]!.GetValue<bool>(), "MCP inspect_files reports each path");
        check(((JsonArray)inspected[0]!["tools"]!).Select(t => (string)t!).SequenceEqual(new[] { "extract_text", "read_codes", "plan_rename" }) && ((JsonArray)inspected[1]!["tools"]!).Select(t => (string)t!).SequenceEqual(new[] { "extract_text", "plan_rename" }),
            "MCP inspect_files names the read-only tools for images and documents");
        host.OcrLanguages = new(); host.LanguageReads = 0;
        var noOcr = (JsonArray)Body(Call(session, "inspect_files", new JsonObject { ["paths"] = Paths(photo, doc, @"C:\Users\me\b.png") }))["files"]!;
        check(((JsonArray)noOcr[0]!["tools"]!).Select(t => (string)t!).SequenceEqual(new[] { "read_codes", "plan_rename" }) && ((JsonArray)noOcr[1]!["tools"]!).Select(t => (string)t!).SequenceEqual(new[] { "extract_text", "plan_rename" }) && host.LanguageReads == 1,
            "MCP inspect_files offers extract_text for images only where Windows OCR can run, asking once");
        host.OcrLanguages = new() { "en-US", "zh-Hans-CN" };

        check(IsError(Call(session, "inspect_files", new JsonObject { ["paths"] = Paths(photo, photo.ToUpperInvariant()) })), "MCP repeated paths refused");
        check(IsError(Call(session, "inspect_files", new JsonObject { ["paths"] = new JsonArray() })) && IsError(Call(session, "inspect_files", new JsonObject { ["paths"] = new JsonArray(1) })), "MCP empty or non-string paths refused");
        check(IsError(Call(session, "plan_action", new JsonObject { ["action"] = "file.delete", ["paths"] = Paths(photo) })), "MCP unknown action refused");
        check(IsError(Call(session, "plan_action", new JsonObject { ["action"] = "convert.png", ["paths"] = Paths(doc) })), "MCP non-image refused at planning");
        int savesBefore = host.Saves;
        var plan = Body(Call(session, "plan_action", new JsonObject { ["action"] = "convert.png", ["paths"] = Paths(photo) }));
        check(plan["plan_id"]!.ToString().Length == 32 && ((string)plan["inputs"]![0]!["output"]!).StartsWith(@"C:\Users\me\Pictures\photo converted.png", StringComparison.Ordinal) && host.Saves == savesBefore && host.Files.Count == 2, "MCP plan writes nothing and predicts the output");

        // Single use, and refused once the input changed.
        var applied = Body(Call(session, "apply_plan", new JsonObject { ["plan_id"] = (string)plan["plan_id"]! }));
        string output = (string)applied["outputs"]![0]!;
        check(output == @"C:\Users\me\Pictures\photo converted.png" && (int)applied["completed"]! == 1 && host.Files.ContainsKey(output), "MCP apply creates the output");
        check(IsError(Call(session, "apply_plan", new JsonObject { ["plan_id"] = (string)plan["plan_id"]! })), "MCP plans are single use");
        var plan2 = Body(Call(session, "plan_action", new JsonObject { ["action"] = "convert.png", ["paths"] = Paths(photo) }));
        host.Files[photo] = host.Files[photo] with { WriteTime = 5 };
        check(IsError(Call(session, "apply_plan", new JsonObject { ["plan_id"] = (string)plan2["plan_id"]! })) && host.Files.Count == 3, "MCP apply refused when an input changed, nothing written");
        host.Files[photo] = host.Files[photo] with { CreationTime = 99 };
        var plan3 = Body(Call(session, "plan_action", new JsonObject { ["action"] = "convert.png", ["paths"] = Paths(photo) }));
        host.UtcNow += McpSession.PlanLifetime;
        check(IsError(Call(session, "apply_plan", new JsonObject { ["plan_id"] = (string)plan3["plan_id"]! })) && host.Files.Count == 3, "MCP plans expire after an hour");

        // Partial failure keeps and logs what was created.
        string second = @"C:\Users\me\Pictures\b.png", bad = @"C:\Users\me\Pictures\bad.png";
        host.Add(second); host.Add(bad); host.FailOn = bad;
        var plan4 = Body(Call(session, "plan_action", new JsonObject { ["action"] = "image.gray", ["paths"] = Paths(second, bad) }));
        var partial = Call(session, "apply_plan", new JsonObject { ["plan_id"] = (string)plan4["plan_id"]! });
        check(IsError(partial) && (int)Body(partial)["completed"]! == 1 && Body(partial)["entry_id"] is JsonValue, "MCP partial apply reports and logs completed outputs");
        host.FailOn = null;

        var history = (JsonArray)Body(Call(session, "history", new JsonObject()))["entries"]!;
        check(history.Count == 2 && (string?)history[0]!["action"] == "image.gray" && (string?)history[1]!["action"] == "convert.png", "MCP history newest first");

        // Undo: dry run changes nothing, then recycles the latest apply's unchanged outputs.
        var dry = Body(Call(session, "undo", new JsonObject { ["dry_run"] = true }));
        check((string?)dry["results"]![0]!["result"] == "would move to the Recycle Bin" && host.Recycled.Count == 0, "MCP undo dry run");
        var undone = Body(Call(session, "undo", new JsonObject()));
        check(undone["complete"]!.GetValue<bool>() && host.Recycled.Count == 1 && !host.Files.ContainsKey(host.Recycled[0]), "MCP undo recycles the latest apply");
        // The next undo reaches the earlier apply; a changed output is left alone.
        host.Files[output] = host.Files[output] with { Size = 99 };
        var refused = Body(Call(session, "undo", new JsonObject()));
        check((string?)refused["results"]![0]!["result"] == "changed since apply; left alone" && !refused["complete"]!.GetValue<bool>() && host.Files.ContainsKey(output), "MCP undo leaves a changed output alone");
        host.Files.Remove(output);
        check((string?)Body(Call(session, "undo", new JsonObject()))["results"]![0]!["result"] == "missing; left as is", "MCP undo reports a missing output");
        check(IsError(Call(session, "undo", new JsonObject { ["entry_id"] = "nope" })), "MCP undo of an unknown entry refused");

        // A damaged history is never overwritten and blocks writes.
        host.History = "{broken"; int saves = host.Saves;
        var plan5 = Body(Call(session, "plan_action", new JsonObject { ["action"] = "convert.bmp", ["paths"] = Paths(photo) }));
        int files = host.Files.Count;
        check(IsError(Call(session, "apply_plan", new JsonObject { ["plan_id"] = (string)plan5["plan_id"]! })) && host.Files.Count == files && host.History == "{broken" && host.Saves == saves, "MCP refuses to apply with an unreadable history and keeps it");
        check(IsError(Call(session, "undo", new JsonObject())) && host.History == "{broken", "MCP undo refuses with an unreadable history");
        host.History = "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"a\",\"outputs\":[{\"path\":\"p\"}]}]}";
        check(IsError(Call(session, "history", new JsonObject())), "MCP history with missing identity fields is unreadable");

        // The log keeps the newest 200 applies.
        host.History = null;
        for (int i = 0; i < McpSession.MaxHistory + 5; i++)
        {
            string path = @"C:\h\" + i + ".png"; host.Add(path);
            var p = Body(Call(session, "plan_action", new JsonObject { ["action"] = "image.flip", ["paths"] = Paths(path) }));
            Call(session, "apply_plan", new JsonObject { ["plan_id"] = (string)p["plan_id"]! });
        }
        var full = (JsonArray)Body(Call(session, "history", new JsonObject { ["limit"] = 500 }))["entries"]!;
        check(full.Count == McpSession.MaxHistory && ((string)full[0]!["outputs"]![0]!["path"]!).Contains(@"\204 ", StringComparison.Ordinal), "MCP history keeps the newest 200");

        // Review fixes: any per-file failure is a tool error, never a crash.
        check((int)Rpc(session, "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}")["error"]!["code"]! == -32600, "MCP null request id refused");
        check(session.Handle("{\"jsonrpc\":\"2.0\",\"id\":5,\"result\":{}}") is null, "MCP client responses get no reply");
        string denied = @"C:\x\denied.png";
        var deniedFiles = (JsonArray)Body(Call(session, "inspect_files", new JsonObject { ["paths"] = Paths(denied) }))["files"]!;
        check(!deniedFiles[0]!["ok"]!.GetValue<bool>() && ((string)deniedFiles[0]!["error"]!).Contains("denied"), "MCP inspect reports a non-UserError failure per file");
        check(IsError(Call(session, "plan_action", new JsonObject { ["action"] = "convert.png", ["paths"] = Paths(denied) })), "MCP plan reports a non-UserError failure");
        var lone = Rpc(session, "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"inspect_files\",\"arguments\":{\"paths\":[\"C:\\\\a\\ud800.png\"]}}}");
        check(lone["result"] is JsonObject && IsError(lone) || lone["error"] is JsonObject, "MCP lone surrogate path answered, not a crash");
        host.History = null;
        string c1 = @"C:\c\one.png", c2 = @"C:\c\two.png"; host.Add(c1); host.Add(c2);
        host.FailOn = c2; host.Throw = new NotSupportedException("No imaging component suitable.");
        var plan6 = Body(Call(session, "plan_action", new JsonObject { ["action"] = "image.gray", ["paths"] = Paths(c1, c2) }));
        var crashed = Call(session, "apply_plan", new JsonObject { ["plan_id"] = (string)plan6["plan_id"]! });
        check(IsError(crashed) && (int)Body(crashed)["completed"]! == 1 && Body(crashed)["entry_id"] is JsonValue && host.History is not null && host.History.Contains("one converted", StringComparison.Ordinal), "MCP decoder exception mid-apply keeps and logs earlier outputs");
        host.FailOn = null; host.Throw = null;
        // Another server's entry written during this apply is kept, and the log is locked.
        host.History = null;
        string d1 = @"C:\d\one.png", d2 = @"C:\d\two.png"; host.Add(d1); host.Add(d2);
        var other = new McpSession(host, "0.86.0");
        var planA = Body(Call(session, "plan_action", new JsonObject { ["action"] = "convert.png", ["paths"] = Paths(d1) }));
        var planB = Body(Call(other, "plan_action", new JsonObject { ["action"] = "convert.png", ["paths"] = Paths(d2) }));
        host.DuringRun = () => { host.DuringRun = null; Call(other, "apply_plan", new JsonObject { ["plan_id"] = (string)planB["plan_id"]! }); };
        Call(session, "apply_plan", new JsonObject { ["plan_id"] = (string)planA["plan_id"]! });
        check(((JsonArray)Body(Call(session, "history", new JsonObject()))["entries"]!).Count == 2 && host.Held == 0 && host.Locks > 0, "MCP concurrent applies both logged; lock released");
        // Failing to save the log is reported with the kept outputs.
        string e1 = @"C:\e\one.png"; host.Add(e1);
        var planE = Body(Call(session, "plan_action", new JsonObject { ["action"] = "convert.png", ["paths"] = Paths(e1) }));
        string? saved = host.History;
        // Tampered value kinds are unreadable, not a crash.
        foreach (string tampered in new[]
        {
            "{\"version\":1,\"entries\":[{\"id\":5,\"action\":\"a\",\"outputs\":[]}]}",
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"a\",\"outputs\":[{\"path\":7,\"volume\":1,\"index\":1,\"size\":1,\"write\":1,\"creation\":1}]}]}",
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"a\",\"outputs\":[{\"path\":\"p\",\"volume\":1,\"index\":1,\"size\":1,\"write\":1,\"creation\":1,\"undone\":\"no\"}]}]}",
        })
        {
            host.History = tampered;
            check(IsError(Call(session, "history", new JsonObject())) && IsError(Call(session, "undo", new JsonObject())) && host.History == tampered, "MCP tampered history kinds refused and kept");
        }
        host.History = saved; host.SaveFails = true;
        var unlogged = Call(session, "apply_plan", new JsonObject { ["plan_id"] = (string)planE["plan_id"]! });
        check(IsError(unlogged) && Body(unlogged)["entry_id"] is null && ((JsonArray)Body(unlogged)["outputs"]!).Count == 1 && ((string)Body(unlogged)["error"]!).Contains("could not be logged") && host.History == saved && host.Held == 0, "MCP a failed history save is reported with the kept output");
        host.SaveFails = false;

        var reader = new FakeHost(); var reading = new McpSession(reader, "0.105.0");
        reader.Add(doc);
        // extract_text: images through OCR (with the language asked for), documents through the
        // document reader; per-file failures and text limits are reported, never cut.
        string scan = @"C:\Users\me\Pictures\scan.png", report = @"C:\Users\me\report.docx", sheet = @"C:\Users\me\sheet.xlsx", noText = @"C:\Users\me\Pictures\blank.png";
        reader.Add(scan); reader.Add(report); reader.Add(sheet); reader.Add(noText);
        reader.Texts[scan] = "Hello 你好"; reader.Texts[doc] = "notes"; reader.Texts[report] = "Quarterly report";
        var extracted = Body(Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(scan, doc, report, noText, @"C:\gone.png"), ["language"] = "zh-hans-cn" }));
        var texts = (JsonArray)extracted["files"]!;
        check((string?)texts[0]!["text"] == "Hello 你好" && (string?)texts[0]!["kind"] == "image" && (string?)texts[1]!["text"] == "notes" && (string?)texts[2]!["kind"] == "document" && (string?)texts[2]!["text"] == "Quarterly report"
            && !texts[3]!["ok"]!.GetValue<bool>() && (string?)texts[3]!["error"] == "No OCR language." && !texts[4]!["ok"]!.GetValue<bool>(),
            "MCP extract_text reads images and documents and reports each failure per file: " + extracted.ToJsonString());
        check(reader.Recognized.SequenceEqual(new[] { (scan, (string?)"zh-Hans-CN"), (noText, (string?)"zh-Hans-CN") }) && reader.Documents.SequenceEqual(new[] { doc, report }),
            "MCP extract_text sends images to OCR with the language as Windows spells it and documents to the document reader, after checking each path");
        check(reader.LanguageReads == 1, "MCP extract_text reads the installed OCR languages once per call");
        Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(scan) });
        check(reader.Recognized[^1] == (scan, null), "MCP extract_text without a language uses the user's language settings");
        check(IsError(Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(scan, sheet) })) && reader.Recognized.Count == 3, "MCP extract_text refuses a file that is neither an image nor a supported document before reading anything");
        foreach (JsonNode language in new JsonNode[] { "", "e", "en US", "en_US", "en-US;rm", new string('a', 65), 5, true })
        {
            check(IsError(Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(scan), ["language"] = language.DeepClone() })), "MCP extract_text refuses a malformed language: " + language.ToJsonString());
            check(IsError(Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(doc), ["language"] = language })), "MCP extract_text refuses a malformed language with documents only: " + language.ToJsonString());
        }
        int recognized = reader.Recognized.Count, documents = reader.Documents.Count;
        var missing = Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(doc, scan), ["language"] = "fr-FR" });
        check(IsError(missing) && ((string)Body(missing)["error"]!).Contains("Installed: en-US, zh-Hans-CN") && reader.Recognized.Count == recognized && reader.Documents.Count == documents,
            "MCP extract_text refuses an OCR language that is not installed, naming the installed ones, before reading anything");
        var docsOnly = (JsonArray)Body(Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(doc), ["language"] = "fr-FR" }))["files"]!;
        check(docsOnly[0]!["ok"]!.GetValue<bool>() && (string?)docsOnly[0]!["text"] == "notes", "MCP extract_text ignores a well-formed language for documents only");
        reader.OcrLanguages = new();
        var withoutOcr = (JsonArray)Body(Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(scan, doc), ["language"] = "en-US" }))["files"]!;
        check((string?)withoutOcr[0]!["error"] == McpSession.NoOcr && withoutOcr[1]!["ok"]!.GetValue<bool>() && reader.Recognized.Count == recognized,
            "MCP extract_text says Windows OCR is unavailable without decoding the image, and still reads documents");
        reader.OcrLanguages = new() { "en-US", "zh-Hans-CN" };
        string big = @"C:\Users\me\big.txt", big2 = @"C:\Users\me\big2.txt"; reader.Add(big); reader.Add(big2);
        reader.Texts[big] = new string('a', McpSession.MaxFileText + 1);
        var tooLong = (JsonArray)Body(Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(big) }))["files"]!;
        check(!tooLong[0]!["ok"]!.GetValue<bool>() && tooLong[0]!["text"] is null, "MCP extract_text refuses a file's text over the limit instead of cutting it");
        int fit = McpSession.MaxReplyText / McpSession.MaxFileText;
        var budget = Enumerable.Range(0, fit + 1).Select(i => @"C:\Users\me\part" + i + ".txt").ToArray();
        foreach (string part in budget) { reader.Add(part); reader.Texts[part] = new string('b', McpSession.MaxFileText - 5); } // leaves room for "notes"
        var parts = (JsonArray)Body(Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(budget.Append(doc).ToArray()) }))["files"]!;
        check(parts.Take(fit).All(f => f!["ok"]!.GetValue<bool>()) && !parts[fit]!["ok"]!.GetValue<bool>() && parts[fit]!["text"] is null && parts[fit + 1]!["ok"]!.GetValue<bool>() && (string?)parts[fit + 1]!["text"] == "notes",
            "MCP extract_text refuses text past the reply budget file by file, and still returns a smaller file after it");
        reader.Texts[big2] = "a\ud800b";
        check(!((JsonArray)Body(Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(big2) }))["files"]!)[0]!["ok"]!.GetValue<bool>(), "MCP extract_text refuses text that is not valid Unicode");

        // read_codes: one strict flat QR per image; anything else is reported as not read.
        string qr = @"C:\Users\me\Pictures\qr.png", odd = @"C:\Users\me\Pictures\odd.png"; reader.Add(qr); reader.Add(odd);
        reader.Texts.Remove(scan); reader.Texts[qr] = "https://example.com/\u0007"; reader.Texts[odd] = "x\udc00";
        var read = Body(Call(reading, "read_codes", new JsonObject { ["paths"] = Paths(qr, scan, odd, @"C:\gone.png") }));
        var codes = (JsonArray)read["codes"]!; var unread = (JsonArray)read["not_read"]!;
        check(codes.Count == 1 && (string?)codes[0]!["path"] == qr && (string?)codes[0]!["value"] == "https://example.com/\u0007" && (string?)codes[0]!["type"] == "QR" && (int)codes[0]!["version"]! == 2 && (string?)codes[0]!["correction"] == "Q",
            "MCP read_codes returns the code's exact text, version and correction level: " + read.ToJsonString());
        check(unread.Select(u => (string)u!["path"]!).SequenceEqual(new[] { scan, odd, @"C:\gone.png" }) && (string?)unread[0]!["error"] == QrFlatRead.NoCode && read["note"] is JsonValue,
            "MCP read_codes lists images without a readable code, invalid text or a bad path, with why");
        check(reader.Codes.SequenceEqual(new[] { qr, scan, odd }), "MCP read_codes reads each image only after checking its path");
        check(IsError(Call(reading, "read_codes", new JsonObject { ["paths"] = Paths(qr, doc) })) && reader.Codes.Count == 3, "MCP read_codes takes images only, refusing before reading any");
        reader.Enabled = false;
        check(IsError(Call(reading, "read_codes", new JsonObject { ["paths"] = Paths(qr) })) && IsError(Call(reading, "extract_text", new JsonObject { ["paths"] = Paths(doc) })), "MCP read tools refuse while Agent Tools is off");
        reader.Enabled = true;

        // plan_rename → apply_plan → undo, on its own host.
        var namer = new FakeHost(); var naming = new McpSession(namer, "0.106.0");
        string ra = @"C:\w\a.txt", rb = @"C:\w\b.png", rc = @"C:\w\photo.jpg";
        namer.Add(ra); namer.Add(rb); namer.Add(rc);
        JsonObject Rename(JsonObject arguments) => Call(naming, "plan_rename", arguments);
        foreach (var (badRename, why) in new (JsonObject, string)[]
        {
            (new JsonObject { ["paths"] = Paths(ra) }, "no rule"),
            (new JsonObject { ["paths"] = Paths(ra), ["find"] = "", ["start"] = 5 }, "only start"),
            (new JsonObject { ["paths"] = Paths(ra), ["base_name"] = 5 }, "a number as text"),
            (new JsonObject { ["paths"] = Paths(ra), ["number"] = "yes" }, "number as text"),
            (new JsonObject { ["paths"] = Paths(ra), ["number"] = true, ["start"] = -1 }, "a negative start"),
            (new JsonObject { ["paths"] = Paths(ra), ["number"] = true, ["start"] = 1.5 }, "a fractional start"),
            (new JsonObject { ["paths"] = Paths(ra), ["number"] = true, ["digits"] = 9 }, "nine digits"),
            (new JsonObject { ["paths"] = Paths(ra, rb), ["number"] = true, ["start"] = 99_999_999 }, "numbers past 99999999"),
            (new JsonObject { ["paths"] = Paths(ra), ["prefix"] = new string('p', 121) }, "text over 120 characters"),
            (new JsonObject { ["paths"] = Paths(ra, @"C:\w\gone.txt"), ["prefix"] = "x" }, "a missing file"),
            (new JsonObject { ["paths"] = Paths(ra), ["find"] = "a", ["replace"] = "b" }, "an unknown argument (a misspelt replace_with)"),
            (new JsonObject { ["paths"] = Paths(ra), ["prefix"] = "x", ["digits"] = 2 }, "digits without number"),
            (new JsonObject { ["paths"] = Paths(ra), ["prefix"] = @"sub\" }, "a backslash in added text"),
            (new JsonObject { ["paths"] = Paths(ra), ["base_name"] = "a/b" }, "a slash in added text"),
            (new JsonObject { ["paths"] = Paths(@"C:\w\A.TXT"), ["prefix"] = "x" }, "a path spelt in another case than the file's name"),
        })
            check(IsError(Rename(badRename)) && namer.NameChecks == 0, "MCP plan_rename refuses " + why + " before checking any name");
        foreach (string hidden in new[] { "\u202E", "\u2028", "\u0085", "\u200B", "\u00A0", "\u3000", "\u3164", "\u2800", "\uFE0F", "\U000E0020" })
        {
            var hiddenRefused = Rename(new JsonObject { ["paths"] = Paths(ra), ["prefix"] = "x" + hidden });
            check(IsError(hiddenRefused) && ((string)Body(hiddenRefused)["error"]!).Contains("U+" + char.ConvertToUtf32(hidden, 0).ToString("X4")) && namer.NameChecks == 0,
                "MCP plan_rename refuses invisible, blank-looking or text-direction characters in added text: U+" + char.ConvertToUtf32(hidden, 0).ToString("X4"));
        }
        check(!IsError(Rename(new JsonObject { ["paths"] = Paths(ra), ["find"] = "\u00A0", ["replace_with"] = "_" })), "MCP plan_rename accepts such characters in find, which only matches");
        namer.Aliases[@"C:\w\PHOTO~1.JPG"] = rc;
        var shortName = Rename(new JsonObject { ["paths"] = Paths(@"C:\w\PHOTO~1.JPG"), ["prefix"] = "x" });
        check(IsError(shortName) && ((string)Body(shortName)["error"]!).Contains("named photo.jpg"), "MCP plan_rename refuses an 8.3 short name, naming the file's own name: " + Body(shortName).ToJsonString());
        namer.Aliases.Clear(); namer.NameChecks = 0;

        var loneRename = Rpc(naming, "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"plan_rename\",\"arguments\":{\"paths\":[\"C:\\\\w\\\\a.txt\"],\"prefix\":\"x\\ud800\"}}}");
        check((loneRename["error"] is JsonObject || IsError(loneRename)) && namer.NameChecks == 0, "MCP plan_rename refuses a lone surrogate before checking any name: " + loneRename.ToJsonString());
        var planned = Body(Rename(new JsonObject { ["paths"] = Paths(ra, rb), ["prefix"] = "2026 ", ["suffix"] = " v", ["number"] = true, ["start"] = 7, ["digits"] = 2 }));
        var plannedRows = (JsonArray)planned["renames"]!;
        check(planned["can_apply"]!.GetValue<bool>() && planned["plan_id"] is JsonValue && (string?)planned["action"] == "rename"
            && (string?)plannedRows[0]!["to"] == @"C:\w\2026 a v-07.txt" && (string?)plannedRows[1]!["to"] == @"C:\w\2026 b v-08.png" && plannedRows.All(r => r!["issue"] is null)
            && namer.Files.ContainsKey(ra) && !namer.Files.ContainsKey(@"C:\w\2026 a v-07.txt") && namer.NameChecks == 2,
            "MCP plan_rename lists each new name, keeps extensions, numbers in path order and changes nothing: " + planned.ToJsonString());
        var renamed = Body(Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)planned["plan_id"]! }));
        check((string?)renamed["action"] == "rename" && renamed["entry_id"] is JsonValue && (int)renamed["completed"]! == 2 && (int)renamed["total"]! == 2
            && !namer.Files.ContainsKey(ra) && namer.Files.ContainsKey(@"C:\w\2026 a v-07.txt") && namer.Files.ContainsKey(@"C:\w\2026 b v-08.png"),
            "MCP apply_plan renames the planned files: " + renamed.ToJsonString());
        check(IsError(Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)planned["plan_id"]! })), "MCP a rename plan is single use");
        var logged = (JsonObject)JsonNode.Parse(namer.History!)!["entries"]![0]!;
        check(logged["outputs"] is JsonArray { Count: 0 } && ((JsonArray)logged["renames"]!).Count == 2 && (string?)logged["renames"]![0]!["from"] == ra,
            "MCP a rename is logged with an empty outputs list, so older versions can still read the log");
        var listedRename = (JsonObject)Body(Call(naming, "history", new JsonObject()))["entries"]![0]!;
        check((string?)listedRename["action"] == "rename" && (string?)listedRename["renames"]![1]!["path"] == @"C:\w\2026 b v-08.png" && !listedRename["renames"]![1]!["undone"]!.GetValue<bool>(),
            "MCP history lists the renamed files");

        // Undo puts every file back together, or none.
        string newA = @"C:\w\2026 a v-07.txt", newB = @"C:\w\2026 b v-08.png";
        var dryUndo = Body(Call(naming, "undo", new JsonObject { ["dry_run"] = true }));
        check(((JsonArray)dryUndo["results"]!).All(r => (string?)r!["result"] == "would be renamed back") && namer.Files.ContainsKey(newA) && namer.Renamed.Count == 1, "MCP undo dry_run of a rename changes nothing");
        var editedB = namer.Files[newB]; namer.Files[newB] = editedB with { Size = 99 };
        var blockedUndo = Body(Call(naming, "undo", new JsonObject()));
        check((string?)blockedUndo["results"]![1]!["result"] == "changed since the rename; left alone" && ((string)blockedUndo["results"]![0]!["result"]!).Contains("waiting") && blockedUndo["note"] is JsonValue
            && !blockedUndo["complete"]!.GetValue<bool>() && namer.Files.ContainsKey(newA) && namer.Renamed.Count == 1, "MCP undo of a rename refuses every file when one changed");
        namer.Files[newB] = editedB; namer.Taken.Add(ra);
        var takenUndo = Body(Call(naming, "undo", new JsonObject()));
        check((string?)takenUndo["results"]![0]!["result"] == "its old name is taken now; left alone" && namer.Files.ContainsKey(newA) && namer.Renamed.Count == 1, "MCP undo of a rename refuses when an old name is taken");
        namer.Taken.Remove(ra);
        var undoneRename = Body(Call(naming, "undo", new JsonObject()));
        check(undoneRename["complete"]!.GetValue<bool>() && ((JsonArray)undoneRename["results"]!).All(r => (string?)r!["result"] == "renamed back") && namer.Files.ContainsKey(ra) && namer.Files.ContainsKey(rb) && !namer.Files.ContainsKey(newA),
            "MCP undo renames every file back: " + undoneRename.ToJsonString());
        check(IsError(Call(naming, "undo", new JsonObject())) && (string?)Body(Call(naming, "undo", new JsonObject { ["entry_id"] = (string)renamed["entry_id"]! }))["results"]![0]!["result"] == "already undone",
            "MCP an undone rename is not undone twice");

        // Problems are reported per row, with no plan.
        namer.Taken.Add(@"C:\w\x a.txt");
        var problemRows = (JsonArray)Body(Rename(new JsonObject { ["paths"] = Paths(ra, rb), ["prefix"] = "x " }))["renames"]!;
        check(((string)problemRows[0]!["issue"]!).Contains("already exists") && problemRows[1]!["issue"] is null, "MCP plan_rename reports a name that is taken");
        var same = Body(Rename(new JsonObject { ["paths"] = Paths(ra, @"C:\w\b.txt"), ["base_name"] = "same" }));
        namer.Add(@"C:\w\b.txt");
        same = Body(Rename(new JsonObject { ["paths"] = Paths(ra, @"C:\w\b.txt"), ["base_name"] = "same" }));
        check(!same["can_apply"]!.GetValue<bool>() && same["plan_id"] is null && ((JsonArray)same["renames"]!).All(r => ((string)r!["issue"]!).Contains("same name")), "MCP plan_rename reports two files getting one name, ignoring case");
        check(((JsonArray)Body(Rename(new JsonObject { ["paths"] = Paths(ra), ["suffix"] = "?" }))["renames"]!)[0]!["issue"] is JsonValue, "MCP plan_rename refuses a name Windows cannot use");
        var nothing = Body(Rename(new JsonObject { ["paths"] = Paths(ra, rb), ["find"] = "zzz", ["replace_with"] = "y" }));
        check(!nothing["can_apply"]!.GetValue<bool>() && ((JsonArray)nothing["renames"]!).All(r => r!["unchanged"]!.GetValue<bool>()), "MCP plan_rename says when no name changes");
        var emptied = Body(Rename(new JsonObject { ["paths"] = Paths(ra), ["find"] = "a" }));
        check(!emptied["can_apply"]!.GetValue<bool>() && ((string)emptied["renames"]![0]!["issue"]!).Contains("before its extension"), "MCP plan_rename refuses a rule that leaves nothing before the extension");
        check(Body(Rename(new JsonObject { ["paths"] = Paths(ra), ["prefix"] = "x", ["number"] = true, ["start"] = 1.0 }))["can_apply"]!.GetValue<bool>(), "MCP plan_rename takes 1.0 as a whole number");
        namer.Locked.Add(rb);
        var lockedRow = Body(Rename(new JsonObject { ["paths"] = Paths(ra, rb), ["prefix"] = "x" }));
        check(!lockedRow["can_apply"]!.GetValue<bool>() && ((string)lockedRow["renames"]![1]!["issue"]!).Contains("can't rename") && lockedRow["renames"]![0]!["issue"] is null,
            "MCP plan_rename reports a file Swivel can't rename (read-only, hard link, other file system) on its row");
        // A file that keeps its name is neither held nor checked, even when it couldn't be renamed.
        string oddName = "C:\\w\\odd\u2028.txt"; namer.Add(oddName); namer.Locked.Add(oddName);
        var keeps = Body(Rename(new JsonObject { ["paths"] = Paths(ra, rb, oddName), ["find"] = "a", ["replace_with"] = "z" }));
        check(keeps["can_apply"]!.GetValue<bool>() && keeps["renames"]![1]!["unchanged"]!.GetValue<bool>() && keeps["renames"]![2]!["unchanged"]!.GetValue<bool>(), "MCP plan_rename ignores files that keep their names: " + keeps.ToJsonString());
        int renameCalls = namer.Renamed.Count;
        var kept = Body(Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)keeps["plan_id"]! }));
        check((int)kept["total"]! == 1 && namer.Renamed.Count == renameCalls + 1 && namer.Renamed[^1].Count == 1 && namer.Files.ContainsKey(@"C:\w\z.txt"), "MCP apply_plan renames only the files whose names change");
        check(Body(Call(naming, "undo", new JsonObject()))["complete"]!.GetValue<bool>() && namer.Files.ContainsKey(ra), "MCP undo of a rename with files that kept their names");
        namer.Locked.Clear(); namer.Files.Remove(oddName);

        // Chains, case changes, a file changed or a name taken after planning, and failed steps.
        string f1 = @"C:\w\f-001.txt", f2 = @"C:\w\f-002.txt"; namer.Add(f1); namer.Add(f2);
        var chain = Body(Rename(new JsonObject { ["paths"] = Paths(f1, f2, rc), ["find"] = "photo", ["replace_with"] = "Photo", ["base_name"] = "f", ["number"] = true, ["start"] = 2 }));
        check(chain["can_apply"]!.GetValue<bool>(), "MCP plan_rename allows a chain onto a name another selected file gives up: " + chain.ToJsonString());
        var caseOnly = Body(Rename(new JsonObject { ["paths"] = Paths(rc), ["find"] = "photo", ["replace_with"] = "Photo" }));
        check(caseOnly["can_apply"]!.GetValue<bool>() && (string?)caseOnly["renames"]![0]!["to"] == @"C:\w\Photo.jpg" && caseOnly["renames"]![0]!["unchanged"] is null, "MCP plan_rename allows a case-only change");
        var chained = Body(Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)chain["plan_id"]! }));
        check((int)chained["completed"]! == 3 && namer.Files.ContainsKey(@"C:\w\f-003.txt") && namer.Files.ContainsKey(@"C:\w\f-004.jpg") && !namer.Files.ContainsKey(f1), "MCP apply_plan renames a chain");
        check(IsError(Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)caseOnly["plan_id"]! })), "MCP a rename plan whose file was renamed since is refused");
        check(Body(Call(naming, "undo", new JsonObject()))["complete"]!.GetValue<bool>() && namer.Files.ContainsKey(f1) && namer.Files.ContainsKey(f2) && namer.Files.ContainsKey(rc), "MCP undo renames a chain back");

        var later = Body(Rename(new JsonObject { ["paths"] = Paths(ra), ["prefix"] = "later " }));
        namer.Files[ra] = namer.Files[ra] with { WriteTime = 5 };
        int histories = namer.Saves;
        check(IsError(Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)later["plan_id"]! })) && namer.Files.ContainsKey(ra) && namer.Saves == histories, "MCP a rename is refused when its file changed since planning");
        later = Body(Rename(new JsonObject { ["paths"] = Paths(ra), ["prefix"] = "later " }));
        namer.Taken.Add(@"C:\w\later a.txt");
        var occupied = Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)later["plan_id"]! });
        check(IsError(occupied) && ((string)Body(occupied)["error"]!).Contains("Nothing was renamed") && namer.Files.ContainsKey(ra) && namer.Saves == histories, "MCP a rename is refused when a new name was taken after planning");
        namer.Taken.Remove(@"C:\w\later a.txt");
        later = Body(Rename(new JsonObject { ["paths"] = Paths(ra, rb), ["prefix"] = "later " }));
        namer.RenameFails = FakeHost.RenameFailure.RolledBack;
        var rolledBack = Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)later["plan_id"]! });
        check(IsError(rolledBack) && ((string)Body(rolledBack)["error"]!).Contains("Every name was restored") && namer.Files.ContainsKey(ra) && namer.Saves == histories, "MCP a failed rename that rolled back logs nothing");
        later = Body(Rename(new JsonObject { ["paths"] = Paths(ra, rb), ["prefix"] = "later " }));
        namer.RenameFails = FakeHost.RenameFailure.Stuck;
        var stuck = Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)later["plan_id"]! });
        string stuckAt = @"C:\w\.swivel-rename-stuck";
        check(IsError(stuck) && Body(stuck)["entry_id"] is JsonValue && (string?)Body(stuck)["renames"]![0]!["to"] == stuckAt && ((JsonArray)Body(stuck)["renames"]!).Count == 1 && (int)Body(stuck)["completed"]! == 0,
            "MCP a rename whose rollback failed lists and logs where the file is: " + Body(stuck).ToJsonString());
        namer.RenameFails = FakeHost.RenameFailure.None;
        check(Body(Call(naming, "undo", new JsonObject()))["complete"]!.GetValue<bool>() && namer.Files.ContainsKey(ra) && !namer.Files.ContainsKey(stuckAt), "MCP undo brings a stuck file back to its name");

        // A swap, and undoing it.
        FileStamp s1 = namer.Files[f1], s2 = namer.Files[f2];
        var swap = Body(Rename(new JsonObject { ["paths"] = Paths(f2, f1), ["base_name"] = "f", ["number"] = true }));
        Body(Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)swap["plan_id"]! }));
        check(namer.Files[f1] == s2 && namer.Files[f2] == s1, "MCP apply_plan swaps two names");
        check(Body(Call(naming, "undo", new JsonObject()))["complete"]!.GetValue<bool>() && namer.Files[f1] == s1 && namer.Files[f2] == s2, "MCP undo swaps them back");

        // The file's name changed case after planning: the plan was built from the old spelling.
        later = Body(Rename(new JsonObject { ["paths"] = Paths(ra), ["prefix"] = "late " }));
        var held = namer.Files[ra]; namer.Files.Remove(ra); namer.Files[@"C:\w\A.txt"] = held; histories = namer.Saves;
        var respelt = Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)later["plan_id"]! });
        check(IsError(respelt) && ((string)Body(respelt)["error"]!).Contains("Nothing was renamed") && namer.Files.ContainsKey(@"C:\w\A.txt") && namer.Saves == histories, "MCP a rename is refused when the file's name changed case after planning");
        namer.Files.Remove(@"C:\w\A.txt"); namer.Files[ra] = held;

        // Undo that fails: refused by the host, stopped half way, or unable to save the log.
        string ua = @"C:\w\u a.txt", ub = @"C:\w\u b.png";
        Body(Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)Body(Rename(new JsonObject { ["paths"] = Paths(ra, rb), ["prefix"] = "u " }))["plan_id"]! }));
        namer.Locked.Add(ua); histories = namer.Saves;
        var hostRefused = Body(Call(naming, "undo", new JsonObject()));
        check(!hostRefused["complete"]!.GetValue<bool>() && ((JsonArray)hostRefused["results"]!).All(r => ((string)r!["result"]!).StartsWith("not renamed back: ")) && hostRefused["note"] is JsonValue
            && namer.Files.ContainsKey(ua) && namer.Saves == histories, "MCP undo of a rename that the host refuses changes nothing and stays pending: " + hostRefused.ToJsonString());
        namer.Locked.Clear(); namer.RenameFails = FakeHost.RenameFailure.Stuck;
        var halfUndo = Body(Call(naming, "undo", new JsonObject()));
        check(!halfUndo["complete"]!.GetValue<bool>() && (string?)halfUndo["results"]![0]!["path"] == stuckAt && ((string)halfUndo["note"]!).Contains("stopped") && namer.Files.ContainsKey(stuckAt) && namer.Files.ContainsKey(ub),
            "MCP an undo that stops half way says where each file is and keeps them logged: " + halfUndo.ToJsonString());
        namer.RenameFails = FakeHost.RenameFailure.None; namer.SaveFails = true;
        var unsaved = Call(naming, "undo", new JsonObject());
        check(IsError(unsaved) && ((string)Body(unsaved)["error"]!).Contains("history could not be updated") && namer.Files.ContainsKey(ra) && namer.Files.ContainsKey(rb),
            "MCP an undo whose log can't be saved says so, with the files back: " + Body(unsaved).ToJsonString());
        namer.SaveFails = false;
        var backAlready = Body(Call(naming, "undo", new JsonObject()));
        check(backAlready["complete"]!.GetValue<bool>() && ((JsonArray)backAlready["results"]!).All(r => (string?)r!["result"] == "already back at its old name") && namer.Files.ContainsKey(ra),
            "MCP a later undo marks files already back at their old names as done: " + backAlready.ToJsonString());
        // The same for a chain (each old name now holds the file that had it) and a case-only change.
        foreach (var (moved, why) in new (JsonObject, string)[]
        {
            (new JsonObject { ["paths"] = Paths(f1, f2), ["base_name"] = "f", ["number"] = true, ["start"] = 2 }, "a chain"),
            (new JsonObject { ["paths"] = Paths(rc), ["find"] = "photo", ["replace_with"] = "Photo" }, "a case-only change"),
        })
        {
            Body(Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)Body(Rename(moved))["plan_id"]! }));
            namer.SaveFails = true;
            bool failed = IsError(Call(naming, "undo", new JsonObject()));
            namer.SaveFails = false;
            var recovered = Body(Call(naming, "undo", new JsonObject()));
            check(failed && recovered["complete"]!.GetValue<bool>() && ((JsonArray)recovered["results"]!).All(r => (string?)r!["result"] == "already back at its old name")
                && namer.Files[f1] == s1 && namer.Files[f2] == s2 && namer.StoredName(rc) == "photo.jpg",
                "MCP a later undo recognizes " + why + " already back after an unsaved undo: " + recovered.ToJsonString());
        }
        // A hard link to the renamed file under its old name is not "back": the old name counts as taken.
        Body(Call(naming, "apply_plan", new JsonObject { ["plan_id"] = (string)Body(Rename(new JsonObject { ["paths"] = Paths(ra), ["prefix"] = "h " }))["plan_id"]! }));
        namer.Files[ra] = namer.Files[@"C:\w\h a.txt"];
        var linked = Body(Call(naming, "undo", new JsonObject()));
        check(!linked["complete"]!.GetValue<bool>() && (string?)linked["results"]![0]!["result"] == "its old name is taken now; left alone", "MCP undo doesn't take a hard link under the old name for the file being back: " + linked.ToJsonString());
        namer.Files.Remove(ra);
        check(Body(Call(naming, "undo", new JsonObject()))["complete"]!.GetValue<bool>() && namer.Files.ContainsKey(ra), "MCP undo renames it back once the link is gone");
        check(IsError(Call(naming, "undo", new JsonObject())), "MCP nothing is left to undo after that");
        var blank = Body(Rename(new JsonObject { ["paths"] = Paths(ra), ["base_name"] = "   " }));
        check(!blank["can_apply"]!.GetValue<bool>() && ((string)blank["renames"]![0]!["issue"]!).Contains("before its extension"), "MCP plan_rename refuses a name of only spaces before the extension");

        string? goodLog = namer.History;
        foreach (string tampered in new[]
        {
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"rename\",\"outputs\":[{\"path\":\"p\",\"volume\":1,\"index\":1,\"size\":1,\"write\":1,\"creation\":1}],\"renames\":[{\"path\":\"q\",\"from\":\"r\",\"volume\":1,\"index\":2,\"size\":1,\"write\":1,\"creation\":1}]}]}",
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"convert.png\",\"outputs\":[],\"renames\":[{\"path\":\"q\",\"from\":\"r\",\"volume\":1,\"index\":2,\"size\":1,\"write\":1,\"creation\":1}]}]}",
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"rename\",\"outputs\":[],\"renames\":5}]}",
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"rename\",\"outputs\":[],\"renames\":[{\"path\":\"p\",\"volume\":1,\"index\":1,\"size\":1,\"write\":1,\"creation\":1}]}]}",
        })
        {
            namer.History = tampered;
            check(IsError(Call(naming, "history", new JsonObject())) && IsError(Call(naming, "undo", new JsonObject())) && namer.History == tampered, "MCP a damaged rename log is refused and kept");
        }
        namer.History = goodLog; namer.Enabled = false;
        check(IsError(Rename(new JsonObject { ["paths"] = Paths(ra), ["prefix"] = "x" })), "MCP plan_rename refuses while Agent Tools is off");
        namer.Enabled = true;

        // Plans are bounded.
        string any = @"C:\h\0.png";
        for (int i = 0; i < McpSession.MaxPlans; i++) Call(session, "plan_action", new JsonObject { ["action"] = "image.rotate", ["paths"] = Paths(any) });
        check(IsError(Call(session, "plan_action", new JsonObject { ["action"] = "image.rotate", ["paths"] = Paths(any) })), "MCP unused plans are bounded");

        check(McpSetup.Text(@"C:\Users\O'Brien\Swivel\Swivel.exe").Contains("command = \"C:\\\\Users\\\\O'Brien\\\\Swivel\\\\Swivel.exe\"", StringComparison.Ordinal), "MCP Codex setup keeps an apostrophe in the path");
        string setup = McpSetup.Text(@"C:\Apps\Swivel\Swivel.exe");
        check(setup.Contains("claude mcp add --scope user swivel-windows -- \"C:\\Apps\\Swivel\\Swivel.exe\" --mcp", StringComparison.Ordinal)
            && setup.Contains("\"command\":\"C:\\\\Apps\\\\Swivel\\\\Swivel.exe\",\"args\":[\"--mcp\"]", StringComparison.Ordinal), "MCP setup text for Claude Code and Claude Desktop");
    }
}
