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
        public readonly Dictionary<string, VideoProbe> Videos = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Path, long Bytes)> Compressed = new();
        public VideoProbe ProbeVideo(string path, CancellationToken token) => Videos.TryGetValue(path, out var video) ? video : throw new UserError("Windows could not open this video.");
        public string Compress(string path, long maxBytes, FileStamp expected, CancellationToken token)
        {
            if (path == FailOn) throw Throw ?? new UserError("The target size can't be reached.");
            DuringRun?.Invoke();
            if (!Files[path].SameObjectAndContent(expected)) throw new UserError("The file changed since planning. No file was made for it; plan again.");
            Compressed.Add((path, maxBytes));
            string output = System.IO.Path.ChangeExtension(path, null) + " compressed." + (VideoBudget.IsVideo(path) ? "mp4" : "jpg");
            for (int n = 2; Files.ContainsKey(output); n++) output = System.IO.Path.ChangeExtension(path, null) + " compressed " + n + "." + (VideoBudget.IsVideo(path) ? "mp4" : "jpg");
            Files[output] = new FileStamp(1, next++, (ulong)Math.Min(maxBytes, 5000), 1000, 2000); return output;
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
        public bool NameTaken(string path) { NameChecks++; string full = Resolve(path); return Files.ContainsKey(full) || Taken.Contains(full); }
        // A file's 8.3 alias, or a folder spelt by its alias: as on disk, both reach the long path.
        private string Resolve(string path)
        {
            if (Aliases.TryGetValue(path, out var file)) return file;
            int slash = path.LastIndexOf('\\');
            return slash > 0 && Aliases.TryGetValue(path[..slash], out var folder) ? folder + path[slash..] : path;
        }
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
        public readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> OtherDrive = new(StringComparer.OrdinalIgnoreCase); // files on another drive
        public readonly Dictionary<string, ulong> Replaced = new(StringComparer.OrdinalIgnoreCase); // a folder deleted and made again at its path
        private MoveIdentity FolderIdentity(string folder) => new(1, (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(folder), Replaced.GetValueOrDefault(folder));
        public MoveIdentity CheckFolder(string folder)
        {
            folder = Aliases.GetValueOrDefault(folder, folder);
            if (!Folders.Contains(folder)) throw new UserError("Choose an existing local folder.");
            return FolderIdentity(folder);
        }
        public bool CheckMove(string path, string folder)
        {
            if (Locked.Contains(path)) throw new UserError("Move Files does not accept read-only files or hard links.");
            if (OtherDrive.Contains(path)) throw new UserError("Move Files currently requires source and destination on the same NTFS volume. Nothing was copied or deleted.");
            return !string.Equals(MoveRules.Parent(path), Aliases.GetValueOrDefault(folder, folder), StringComparison.OrdinalIgnoreCase);
        }
        public RenameFailure MoveFails;
        public readonly List<IReadOnlyList<RenamePair>> Moved = new();
        public RenameOutcome Move(IReadOnlyList<RenamePair> plan, IReadOnlyList<FileStamp> expected, MoveIdentity? folder, CancellationToken token)
        {
            if (!Folders.Contains(MoveRules.Parent(plan[0].Destination)) || plan.Any(p => MoveRules.Parent(p.Destination) != MoveRules.Parent(plan[0].Destination))) throw new UserError("A source or destination folder changed.");
            if (folder is not null && FolderIdentity(MoveRules.Parent(plan[0].Destination)) != folder) throw new McpToolError("The destination folder was replaced since planning.");
            for (int i = 0; i < plan.Count; i++)
                if (!Files.TryGetValue(plan[i].Source, out var s) || !s.SameObjectAndContent(expected[i])) throw new UserError("A file changed just now.");
            if (plan.Any(p => StoredName(p.Source) != MoveRules.FileName(p.Source))) throw new UserError("A file's name changed just now.");
            foreach (var pair in plan) CheckMove(pair.Source, MoveRules.Parent(pair.Destination));
            if (plan.Any(p => Files.ContainsKey(p.Destination) || Taken.Contains(p.Destination))) throw new UserError("A destination name is already occupied. Nothing was overwritten.");
            Moved.Add(plan);
            var stamps = plan.Select(p => Files[p.Source]).ToArray();
            var error = new System.IO.IOException("The disk is busy.");
            if (MoveFails == RenameFailure.RolledBack) return new RenameOutcome(plan.Select((p, i) => new RenameLocation(p.Source, p.Source, stamps[i])).ToArray(), error, false);
            // Stuck: the first file's move back failed; the others went back.
            var at = MoveFails == RenameFailure.Stuck ? plan.Select((p, i) => i == 0 ? p.Destination : p.Source).ToArray() : plan.Select(p => p.Destination).ToArray();
            foreach (var pair in plan) Files.Remove(pair.Source);
            for (int i = 0; i < plan.Count; i++) Files[at[i]] = stamps[i];
            return new RenameOutcome(plan.Select((p, i) => new RenameLocation(p.Source, at[i], stamps[i])).ToArray(), MoveFails == RenameFailure.Stuck ? error : null, false);
        }
        public FileStamp? Stamp(string path) => Files.TryGetValue(Resolve(path), out var s) ? s : null;
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
        check(tools.SequenceEqual(new[] { "list_actions", "inspect_files", "extract_text", "read_codes", "plan_action", "plan_compress", "plan_rename", "plan_move", "apply_plan", "undo", "history" }), "MCP tool list");
        var listed = (JsonArray)Rpc(session, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}")["result"]!["tools"]!;
        check(listed.Where(t => (string)t!["name"]! is "extract_text" or "read_codes" or "plan_compress" or "plan_rename" or "plan_move").All(t => t!["annotations"]!["readOnlyHint"]!.GetValue<bool>() && !t!["annotations"]!["destructiveHint"]!.GetValue<bool>()),
            "MCP extract_text, read_codes and the plan tools are marked read-only");
        check((int)Call(session, "format_disk", new JsonObject())["error"]!["code"]! == -32602, "MCP unknown tool is an invalid-params error");

        host.Enabled = false;
        check(IsError(Call(session, "list_actions", new JsonObject())) && ((string)Body(Call(session, "list_actions", new JsonObject()))["error"]!).Contains("Agent Tools is off"), "MCP tools refuse while Agent Tools is off");
        host.Enabled = true;
        check(((JsonArray)Body(Call(session, "list_actions", new JsonObject()))["actions"]!).Count == McpSession.Actions.Count, "MCP list_actions");

        string photo = @"C:\Users\me\Pictures\photo.jpg", doc = @"C:\Users\me\notes.txt";
        host.Add(photo); host.Add(doc);
        var inspected = (JsonArray)Body(Call(session, "inspect_files", new JsonObject { ["paths"] = Paths(photo, doc, @"C:\missing.png") }))["files"]!;
        check(inspected[0]!["ok"]!.GetValue<bool>() && ((JsonArray)inspected[0]!["actions"]!).Count == McpSession.Actions.Count && ((JsonArray)inspected[1]!["actions"]!).Count == 0 && !inspected[2]!["ok"]!.GetValue<bool>(), "MCP inspect_files reports each path");
        check(((JsonArray)inspected[0]!["tools"]!).Select(t => (string)t!).SequenceEqual(new[] { "extract_text", "read_codes", "plan_compress", "plan_rename", "plan_move" }) && ((JsonArray)inspected[1]!["tools"]!).Select(t => (string)t!).SequenceEqual(new[] { "extract_text", "plan_rename", "plan_move" }),
            "MCP inspect_files names the read-only tools for images and documents");
        host.OcrLanguages = new(); host.LanguageReads = 0;
        var noOcr = (JsonArray)Body(Call(session, "inspect_files", new JsonObject { ["paths"] = Paths(photo, doc, @"C:\Users\me\b.png") }))["files"]!;
        check(((JsonArray)noOcr[0]!["tools"]!).Select(t => (string)t!).SequenceEqual(new[] { "read_codes", "plan_compress", "plan_rename", "plan_move" }) && ((JsonArray)noOcr[1]!["tools"]!).Select(t => (string)t!).SequenceEqual(new[] { "extract_text", "plan_rename", "plan_move" }) && host.LanguageReads == 1,
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

        // plan_move → apply_plan → undo, on its own host.
        var mover = new FakeHost(); var moving = new McpSession(mover, "0.107.0");
        string inbox = @"C:\m\in", outbox = @"C:\m\out", elsewhere = @"C:\m\elsewhere";
        foreach (string folder in new[] { inbox, outbox, elsewhere }) mover.Folders.Add(folder);
        string ma = inbox + @"\a.txt", mb = elsewhere + @"\b.png", mc = inbox + @"\c.txt";
        foreach (string file in new[] { ma, mb, mc }) mover.Add(file);
        JsonObject Move(JsonObject arguments) => Call(moving, "plan_move", arguments);
        foreach (var (badMove, why) in new (JsonObject, string)[]
        {
            (new JsonObject { ["paths"] = Paths(ma) }, "a missing destination"),
            (new JsonObject { ["paths"] = Paths(ma), ["destination"] = 5 }, "a destination that is not text"),
            (new JsonObject { ["paths"] = Paths(ma), ["destination"] = @"C:\m\nope" }, "a folder that doesn't exist"),
            (new JsonObject { ["paths"] = Paths(ma), ["destination"] = "out" }, "a relative destination"),
            (new JsonObject { ["paths"] = Paths(ma), ["destination"] = outbox, ["to"] = outbox }, "an unknown argument"),
            (new JsonObject { ["paths"] = Paths(@"C:\m\in\A.TXT"), ["destination"] = outbox }, "a path spelt in another case than the file's name"),
            (new JsonObject { ["paths"] = Paths(ma, @"C:\m\in\gone.txt"), ["destination"] = outbox }, "a missing file"),
            (new JsonObject { ["paths"] = Paths(ma, ma), ["destination"] = outbox }, "a path given twice"),
        })
            check(IsError(Move(badMove)) && mover.NameChecks == 0, "MCP plan_move refuses " + why + " before checking any name");
        var nope = Body(Move(new JsonObject { ["paths"] = Paths(ma), ["destination"] = @"C:\m\nope" }));
        check(((string)nope["error"]!).StartsWith("destination: "), "MCP plan_move names the destination in its error: " + nope.ToJsonString());
        mover.Aliases[@"C:\m\in\LONGNA~1.TXT"] = ma;
        var shortMove = Move(new JsonObject { ["paths"] = Paths(@"C:\m\in\LONGNA~1.TXT"), ["destination"] = outbox });
        check(IsError(shortMove) && ((string)Body(shortMove)["error"]!).Contains("named a.txt"), "MCP plan_move refuses an 8.3 short name, which the moved file would keep: " + Body(shortMove).ToJsonString());
        mover.Aliases.Clear();

        // Row issues: already there (also through another spelling of the folder), a name taken
        // there, two files with one name, a read-only file and one on another drive.
        mover.Aliases[@"C:\m\IN~1"] = inbox;
        var already = Body(Move(new JsonObject { ["paths"] = Paths(ma, mb), ["destination"] = inbox }));
        var viaAlias = Body(Move(new JsonObject { ["paths"] = Paths(ma), ["destination"] = @"C:\m\IN~1" }));
        mover.Locked.Add(ma);
        var lockedViaAlias = Body(Move(new JsonObject { ["paths"] = Paths(ma), ["destination"] = @"C:\m\IN~1" }));
        check((string?)lockedViaAlias["moves"]![0]!["issue"] == "Already in this folder.", "MCP plan_move says a read-only file reached through the folder's alias is already there: " + lockedViaAlias.ToJsonString());
        mover.Locked.Clear();
        check(!already["can_apply"]!.GetValue<bool>() && already["plan_id"] is null && (string?)already["moves"]![0]!["issue"] == "Already in this folder." && already["moves"]![1]!["issue"] is null
            && (string?)viaAlias["moves"]![0]!["issue"] == "Already in this folder.", "MCP plan_move reports a file already in the folder, however the folder is spelt: " + viaAlias.ToJsonString());
        mover.Aliases.Clear();
        string twin = elsewhere + @"\a.txt"; mover.Add(twin); mover.Taken.Add(outbox + @"\c.txt");
        var clashes = Body(Move(new JsonObject { ["paths"] = Paths(ma, twin, mc), ["destination"] = outbox }));
        var clashRows = (JsonArray)clashes["moves"]!;
        check(!clashes["can_apply"]!.GetValue<bool>() && ((string)clashRows[0]!["issue"]!).Contains("same name") && ((string)clashRows[1]!["issue"]!).Contains("same name")
            && ((string)clashRows[2]!["issue"]!).Contains("already in the destination") && (string?)clashRows[2]!["to"] == outbox + @"\c.txt", "MCP plan_move reports two files with one name and a name taken there: " + clashes.ToJsonString());
        mover.Files.Remove(twin); mover.Taken.Clear();
        mover.Locked.Add(mc); mover.OtherDrive.Add(mb);
        var cannot = Body(Move(new JsonObject { ["paths"] = Paths(ma, mb, mc), ["destination"] = outbox }));
        check(!cannot["can_apply"]!.GetValue<bool>() && cannot["moves"]![0]!["issue"] is null && ((string)cannot["moves"]![1]!["issue"]!).Contains("same NTFS volume") && ((string)cannot["moves"]![2]!["issue"]!).Contains("read-only"),
            "MCP plan_move reports a file on another drive and a read-only file on their rows: " + cannot.ToJsonString());
        mover.Locked.Clear(); mover.OtherDrive.Clear(); mover.NameChecks = 0;

        // A move from two folders into a third, logged, listed and undone file by file.
        var movePlan = Body(Move(new JsonObject { ["paths"] = Paths(ma, mb), ["destination"] = outbox + @"\" }));
        string na = outbox + @"\a.txt", nb = outbox + @"\b.png";
        check(movePlan["can_apply"]!.GetValue<bool>() && (string?)movePlan["action"] == "move" && (string?)movePlan["destination"] == outbox && (string?)movePlan["moves"]![0]!["to"] == na
            && (string?)movePlan["moves"]![1]!["to"] == nb && mover.Files.ContainsKey(ma) && mover.Moved.Count == 0 && mover.NameChecks == 2, "MCP plan_move lists each new path and changes nothing: " + movePlan.ToJsonString());
        var movedBody = Body(Call(moving, "apply_plan", new JsonObject { ["plan_id"] = (string)movePlan["plan_id"]! }));
        check((string?)movedBody["action"] == "move" && movedBody["entry_id"] is JsonValue && (int)movedBody["completed"]! == 2 && (int)movedBody["total"]! == 2 && (string?)movedBody["moves"]![1]!["to"] == nb
            && !mover.Files.ContainsKey(ma) && mover.Files.ContainsKey(na) && mover.Files.ContainsKey(nb), "MCP apply_plan moves the planned files: " + movedBody.ToJsonString());
        check(IsError(Call(moving, "apply_plan", new JsonObject { ["plan_id"] = (string)movePlan["plan_id"]! })), "MCP a move plan is single use");
        var movedLog = (JsonObject)JsonNode.Parse(mover.History!)!["entries"]![0]!;
        check((string?)movedLog["action"] == "move" && movedLog["outputs"] is JsonArray { Count: 0 } && movedLog["renames"] is null && (string?)movedLog["moves"]![1]!["from"] == mb,
            "MCP a move is logged under moves with an empty outputs list");
        var listedMove = (JsonObject)Body(Call(moving, "history", new JsonObject()))["entries"]![0]!;
        check((string?)listedMove["moves"]![0]!["path"] == na && (string?)listedMove["moves"]![0]!["from"] == ma, "MCP history lists the moved files");
        var dryMove = Body(Call(moving, "undo", new JsonObject { ["dry_run"] = true }));
        check(((JsonArray)dryMove["results"]!).All(r => (string?)r!["result"] == "would be moved back") && mover.Files.ContainsKey(na) && mover.Moved.Count == 1, "MCP undo dry_run of a move changes nothing");
        mover.Locked.Add(nb);
        var dryLocked = Body(Call(moving, "undo", new JsonObject { ["dry_run"] = true }));
        check((string?)dryLocked["results"]![0]!["result"] == "would be moved back" && ((string)dryLocked["results"]![1]!["result"]!).StartsWith("would not be moved back: ") && mover.Moved.Count == 1,
            "MCP undo dry_run of a move says which file the move would refuse: " + dryLocked.ToJsonString());
        mover.Locked.Clear();
        var editedNb = mover.Files[nb]; mover.Files[nb] = editedNb with { WriteTime = 5 };
        var halfBack = Body(Call(moving, "undo", new JsonObject()));
        check((string?)halfBack["results"]![0]!["result"] == "moved back" && (string?)halfBack["results"]![1]!["result"] == "changed since the move; left alone" && !halfBack["complete"]!.GetValue<bool>()
            && mover.Files.ContainsKey(ma) && mover.Files.ContainsKey(nb), "MCP undo of a move puts back the unchanged file and leaves the edited one: " + halfBack.ToJsonString());
        mover.Files[nb] = editedNb;
        var restBack = Body(Call(moving, "undo", new JsonObject()));
        check((string?)restBack["results"]![0]!["result"] == "already undone" && (string?)restBack["results"]![1]!["result"] == "moved back" && restBack["complete"]!.GetValue<bool>()
            && mover.Files.ContainsKey(mb) && !mover.Files.ContainsKey(nb), "MCP a second undo moves back the rest");
        check(IsError(Call(moving, "undo", new JsonObject())) && ((string)Body(Call(moving, "undo", new JsonObject()))["error"]!).Contains("Nothing to undo"), "MCP nothing left to undo after the move");

        // Undo leaves a file whose old name is taken, or the same file at both paths (a hard link made since).
        string nc = outbox + @"\c.txt";
        Call(moving, "apply_plan", new JsonObject { ["plan_id"] = (string)Body(Move(new JsonObject { ["paths"] = Paths(mc), ["destination"] = outbox }))["plan_id"]! });
        mover.Add(mc);
        check((string?)Body(Call(moving, "undo", new JsonObject()))["results"]![0]!["result"] == "its old name is taken now; left alone" && mover.Files.ContainsKey(nc), "MCP undo of a move leaves a file whose old name is taken");
        mover.Files[mc] = mover.Files[nc];
        check((string?)Body(Call(moving, "undo", new JsonObject()))["results"]![0]!["result"] == "its old name is taken now; left alone" && mover.Files.ContainsKey(nc), "MCP undo of a move does not take a hard link at the old path for the file being back");
        mover.Files.Remove(mc);
        mover.Folders.Remove(inbox);
        var folderGone = Body(Call(moving, "undo", new JsonObject { ["dry_run"] = true }));
        check(((string)folderGone["results"]![0]!["result"]!).StartsWith("its old folder is unavailable") && mover.Files.ContainsKey(nc), "MCP undo of a move says when the old folder is gone: " + folderGone.ToJsonString());
        mover.Folders.Add(inbox);
        // A history that can't be saved after the undo says so; the next undo finds the file back.
        mover.SaveFails = true;
        var unsavedMove = Call(moving, "undo", new JsonObject());
        check(IsError(unsavedMove) && ((string)Body(unsavedMove)["error"]!).Contains("history could not be updated") && (string?)Body(unsavedMove)["results"]![0]!["result"] == "moved back" && mover.Files.ContainsKey(mc),
            "MCP undo of a move reports a history it could not save: " + Body(unsavedMove).ToJsonString());
        mover.SaveFails = false;
        var foundBack = Body(Call(moving, "undo", new JsonObject()));
        check((string?)foundBack["results"]![0]!["result"] == "already back in its old folder" && foundBack["complete"]!.GetValue<bool>(), "MCP the next undo finds the moved file already back: " + foundBack.ToJsonString());

        // After planning: a name taken there, or the file edited, refuses the apply; a failed move leaves nothing logged.
        var lateMove = Body(Move(new JsonObject { ["paths"] = Paths(ma), ["destination"] = outbox }));
        mover.Taken.Add(na);
        var lateTaken = Call(moving, "apply_plan", new JsonObject { ["plan_id"] = (string)lateMove["plan_id"]! });
        check(IsError(lateTaken) && ((string)Body(lateTaken)["error"]!).EndsWith("Nothing was moved.") && mover.Files.ContainsKey(ma), "MCP apply_plan refuses a move onto a name taken after planning: " + Body(lateTaken).ToJsonString());
        mover.Taken.Clear();
        lateMove = Body(Move(new JsonObject { ["paths"] = Paths(ma), ["destination"] = outbox }));
        mover.Files[ma] = mover.Files[ma] with { Size = 77 };
        check(IsError(Call(moving, "apply_plan", new JsonObject { ["plan_id"] = (string)lateMove["plan_id"]! })) && mover.Files.ContainsKey(ma), "MCP apply_plan refuses a move of a file edited after planning");
        int movesLogged = ((JsonArray)JsonNode.Parse(mover.History!)!["entries"]!).Count;
        mover.Files[ma] = mover.Files[ma] with { Size = 1 };
        lateMove = Body(Move(new JsonObject { ["paths"] = Paths(ma), ["destination"] = outbox }));
        mover.Replaced[outbox] = 1;
        var replacedFolder = Call(moving, "apply_plan", new JsonObject { ["plan_id"] = (string)lateMove["plan_id"]! });
        check(IsError(replacedFolder) && ((string)Body(replacedFolder)["error"]!).Contains("replaced since planning") && mover.Files.ContainsKey(ma) && ((JsonArray)JsonNode.Parse(mover.History!)!["entries"]!).Count == movesLogged,
            "MCP apply_plan refuses a move into a folder replaced since planning: " + Body(replacedFolder).ToJsonString());
        mover.Replaced.Clear();
        var renameToo = Body(Call(moving, "plan_rename", new JsonObject { ["paths"] = Paths(mc), ["prefix"] = "x " }));
        Call(moving, "apply_plan", new JsonObject { ["plan_id"] = (string)renameToo["plan_id"]! });
        string xc = inbox + @"\x c.txt"; mover.Locked.Add(xc);
        var dryRenameLocked = Body(Call(moving, "undo", new JsonObject { ["dry_run"] = true }));
        check(((string)dryRenameLocked["results"]![0]!["result"]!).StartsWith("would not be renamed back: ") && mover.Files.ContainsKey(xc), "MCP undo dry_run of a rename says when the rename would be refused: " + dryRenameLocked.ToJsonString());
        mover.Locked.Clear();
        check(Body(Call(moving, "undo", new JsonObject()))["complete"]!.GetValue<bool>() && mover.Files.ContainsKey(mc), "MCP undo of that rename");
        movesLogged = ((JsonArray)JsonNode.Parse(mover.History!)!["entries"]!).Count;
        mover.MoveFails = FakeHost.RenameFailure.RolledBack;
        var rolledMove = Call(moving, "apply_plan", new JsonObject { ["plan_id"] = (string)Body(Move(new JsonObject { ["paths"] = Paths(ma, mb), ["destination"] = outbox }))["plan_id"]! });
        check(IsError(rolledMove) && ((string)Body(rolledMove)["error"]!).Contains("nothing was moved") && ((JsonArray)JsonNode.Parse(mover.History!)!["entries"]!).Count == movesLogged,
            "MCP a move that rolled back is reported and not logged: " + Body(rolledMove).ToJsonString());
        mover.MoveFails = FakeHost.RenameFailure.Stuck;
        var stuckMove = Call(moving, "apply_plan", new JsonObject { ["plan_id"] = (string)Body(Move(new JsonObject { ["paths"] = Paths(ma, mb), ["destination"] = outbox }))["plan_id"]! });
        var stuckLog = (JsonObject)((JsonArray)JsonNode.Parse(mover.History!)!["entries"]!)[^1]!;
        check(IsError(stuckMove) && ((string)Body(stuckMove)["error"]!).Contains("could not be moved back") && ((JsonArray)Body(stuckMove)["moves"]!).Count == 1 && Body(stuckMove)["entry_id"] is JsonValue
            && ((JsonArray)stuckLog["moves"]!).Count == 1 && (string?)stuckLog["moves"]![0]!["path"] == na && mover.Files.ContainsKey(na) && mover.Files.ContainsKey(mb),
            "MCP a move whose rollback failed lists and logs where the file is: " + Body(stuckMove).ToJsonString());
        mover.MoveFails = FakeHost.RenameFailure.None;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var stopped = (JsonObject)JsonNode.Parse(moving.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "undo", ["arguments"] = new JsonObject() } }.ToJsonString(), cancelled.Token)!)!;
            check((string?)stopped["result"]!["structuredContent"]!["results"]![0]!["result"] == "not moved back: Cancelled." && mover.Files.ContainsKey(na), "MCP a cancelled undo of a move moves nothing back");
        }
        check(Body(Call(moving, "undo", new JsonObject()))["complete"]!.GetValue<bool>() && mover.Files.ContainsKey(ma), "MCP undo of the stuck move");

        string? goodMoves = mover.History;
        foreach (string tampered in new[]
        {
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"move\",\"outputs\":[{\"path\":\"p\",\"volume\":1,\"index\":1,\"size\":1,\"write\":1,\"creation\":1}],\"moves\":[{\"path\":\"q\",\"from\":\"r\",\"volume\":1,\"index\":2,\"size\":1,\"write\":1,\"creation\":1}]}]}",
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"rename\",\"outputs\":[],\"moves\":[{\"path\":\"q\",\"from\":\"r\",\"volume\":1,\"index\":2,\"size\":1,\"write\":1,\"creation\":1}]}]}",
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"move\",\"outputs\":[],\"renames\":[{\"path\":\"s\",\"from\":\"t\",\"volume\":1,\"index\":3,\"size\":1,\"write\":1,\"creation\":1}],\"moves\":[{\"path\":\"q\",\"from\":\"r\",\"volume\":1,\"index\":2,\"size\":1,\"write\":1,\"creation\":1}]}]}",
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"move\",\"outputs\":[],\"moves\":{}}]}",
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"move\",\"outputs\":[],\"moves\":[{\"path\":\"q\",\"volume\":1,\"index\":2,\"size\":1,\"write\":1,\"creation\":1}]}]}",
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"move\",\"outputs\":[],\"moves\":[{\"path\":\"C:\\\\o\\\\b.txt\",\"from\":\"C:\\\\i\\\\a.txt\",\"volume\":1,\"index\":2,\"size\":1,\"write\":1,\"creation\":1}]}]}",
            "{\"version\":1,\"entries\":[{\"id\":\"x\",\"action\":\"move\",\"outputs\":[],\"moves\":[{\"path\":\"C:\\\\o\\\\a.txt\",\"from\":\"C:\\\\i\\\\a.txt\",\"volume\":1,\"index\":2,\"size\":1,\"write\":1,\"creation\":1},{\"path\":\"C:\\\\p\\\\b.txt\",\"from\":\"C:\\\\i\\\\b.txt\",\"volume\":1,\"index\":3,\"size\":1,\"write\":1,\"creation\":1}]}]}",
        })
        {
            mover.History = tampered;
            check(IsError(Call(moving, "history", new JsonObject())) && IsError(Call(moving, "undo", new JsonObject())) && mover.History == tampered, "MCP a damaged move log is refused and kept");
        }
        mover.History = goodMoves;

        // plan_compress: images become JPEG, videos MP4, each under the target; logged and undone like an action.
        var shrinker = new FakeHost(); var shrinking = new McpSession(shrinker, "0.108.0");
        string pic = @"C:\c\pic.png", jpeg = @"C:\c\shot.jpg", clip = @"C:\c\clip.mov", quiet = @"C:\c\quiet.mp4", text = @"C:\c\read me.txt";
        foreach (string f in new[] { pic, jpeg, clip, quiet, text }) shrinker.Add(f);
        shrinker.Videos[clip] = new VideoProbe(3840, 2160, 60, true, 20_000_000);
        shrinker.Videos[quiet] = new VideoProbe(1280, 720, 10, false, 0);
        shrinker.Files[clip] = shrinker.Files[clip] with { Size = 50_000_000 };
        JsonObject Compress(JsonObject arguments) => Call(shrinking, "plan_compress", arguments);
        var squeezed = Body(Compress(new JsonObject { ["paths"] = Paths(pic, jpeg, clip, quiet), ["target_bytes"] = 2_000_000 }));
        var rows = (JsonArray)squeezed["inputs"]!;
        check((string?)squeezed["action"] == "compress" && (long)squeezed["target_bytes"]! == 2_000_000 && rows.Count == 4
            && (string?)rows[0]!["kind"] == "image" && ((string)rows[0]!["output"]!).StartsWith(@"C:\c\pic compressed.jpg", StringComparison.Ordinal)
            && (string?)rows[2]!["kind"] == "video" && ((string)rows[2]!["output"]!).StartsWith(@"C:\c\clip compressed.mp4", StringComparison.Ordinal)
            && (int)rows[2]!["audio_bitrate"]! == 96_000 && (int)rows[2]!["width"]! == 640 && (int)rows[2]!["height"]! == 360
            && (int)rows[3]!["audio_bitrate"]! == 0 && (int)rows[3]!["width"]! == 1280 && rows[0]!["note"] is JsonValue && rows[2]!["note"] is null
            && shrinker.Compressed.Count == 0 && shrinker.Files.Count == 5, "MCP plan_compress plans images and videos and writes nothing: " + squeezed.ToJsonString());
        var shrunk = Body(Call(shrinking, "apply_plan", new JsonObject { ["plan_id"] = (string)squeezed["plan_id"]! }));
        check((int)shrunk["completed"]! == 4 && (string?)shrunk["action"] == "compress" && (string?)shrunk["outputs"]![0] == @"C:\c\pic compressed.jpg" && (string?)shrunk["outputs"]![3] == @"C:\c\quiet compressed.mp4"
            && shrinker.Compressed.All(c => c.Bytes == 2_000_000) && shrinker.Compressed.Count == 4, "MCP apply of plan_compress makes each new file with the target");
        var squeezeLog = (JsonObject)((JsonArray)JsonNode.Parse(shrinker.History!)!["entries"]!)[0]!;
        check((string?)squeezeLog["action"] == "compress" && ((JsonArray)squeezeLog["outputs"]!).Count == 4 && squeezeLog["renames"] is null && squeezeLog["moves"] is null, "MCP plan_compress is logged as created files");
        var again = Body(Call(shrinking, "apply_plan", new JsonObject { ["plan_id"] = (string)Body(Compress(new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 1000, ["format"] = "JPG" }))["plan_id"]! }));
        check((string?)again["outputs"]![0] == @"C:\c\pic compressed 2.jpg", "MCP plan_compress never replaces an earlier compressed copy");
        check(Body(Call(shrinking, "undo", new JsonObject()))["complete"]!.GetValue<bool>() && !shrinker.Files.ContainsKey(@"C:\c\pic compressed 2.jpg") && shrinker.Files.ContainsKey(@"C:\c\pic compressed.jpg"), "MCP undo of plan_compress recycles its newest copy");
        var dryShrink = Body(Call(shrinking, "undo", new JsonObject { ["dry_run"] = true }));
        check(((JsonArray)dryShrink["results"]!).Count == 4 && dryShrink["results"]!.AsArray().All(r => (string?)r!["result"] == "would move to the Recycle Bin"), "MCP undo dry run of plan_compress lists every copy");
        check(Body(Call(shrinking, "undo", new JsonObject()))["complete"]!.GetValue<bool>() && shrinker.Recycled.Count == 5 && shrinker.Files.Count == 5, "MCP undo of plan_compress recycles all copies, originals kept");

        foreach (var (arguments, expected, label) in new (JsonObject, string, string)[]
        {
            (new JsonObject { ["paths"] = Paths(pic) }, "target_bytes must be", "missing target"),
            (new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 999 }, "target_bytes must be", "target below 1,000"),
            (new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 1_000_000_000_001 }, "target_bytes must be", "target above 1 TB"),
            (new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 5000.5 }, "target_bytes must be", "fractional target"),
            (new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = "5000" }, "target_bytes must be", "target as text"),
            (new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 5000, ["quality"] = 50 }, "Unknown argument quality", "unknown argument"),
            (new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 5000, ["format"] = "heic" }, "only as JPEG", "HEIC format"),
            (new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 5000, ["format"] = "png" }, "only as JPEG", "PNG format"),
            (new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 5000, ["format"] = "webp" }, "format must be same or jpg", "unknown format"),
            (new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 5000, ["format"] = 3 }, "format must be same or jpg", "format as a number"),
            (new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 5000, ["format"] = "same" }, "format same keeps only JPEG images and MP4 videos", "same for a PNG"),
            (new JsonObject { ["paths"] = Paths(jpeg, clip), ["target_bytes"] = 5_000_000, ["format"] = "same" }, "format same keeps only JPEG images and MP4 videos", "same for a MOV"),
            (new JsonObject { ["paths"] = Paths(text), ["target_bytes"] = 5000 }, "plan_compress takes", "a text file"),
            (new JsonObject { ["paths"] = Paths(@"C:\c\gone.jpg"), ["target_bytes"] = 5000 }, @"C:\c\gone.jpg: ", "a missing file"),
            (new JsonObject { ["paths"] = Paths(@"C:\c\odd.mp4"), ["target_bytes"] = 5_000_000 }, "odd.mp4: ", "a video Windows can't open"),
            (new JsonObject { ["paths"] = Paths(clip), ["target_bytes"] = 1_000_000 }, "needs at least 1,946,202 bytes", "a target too small for the video's length"),
        })
        {
            if (label == "a video Windows can't open") shrinker.Add(@"C:\c\odd.mp4");
            var refusal = Compress(arguments);
            check(IsError(refusal) && ((string)Body(refusal)["error"]!).Contains(expected, StringComparison.Ordinal), "MCP plan_compress refuses " + label + ": " + Body(refusal).ToJsonString());
        }
        check(!IsError(Compress(new JsonObject { ["paths"] = Paths(jpeg, quiet), ["target_bytes"] = 201_465, ["format"] = "same" })) && !IsError(Compress(new JsonObject { ["paths"] = Paths(clip), ["target_bytes"] = 1_946_202, ["format"] = "jpeg" })),
            "MCP plan_compress accepts format same for JPEG images and MP4 videos, jpeg for jpg, and the smallest target it named");
        check(shrinker.Compressed.Count == 5, "MCP refused compress plans run nothing");

        var changing = Body(Compress(new JsonObject { ["paths"] = Paths(jpeg), ["target_bytes"] = 5000 }));
        shrinker.Files[jpeg] = shrinker.Files[jpeg] with { Size = 11 };
        check(IsError(Call(shrinking, "apply_plan", new JsonObject { ["plan_id"] = (string)changing["plan_id"]! })) && shrinker.Compressed.Count == 5, "MCP plan_compress refused when an input changed, nothing written");
        var racing = Body(Compress(new JsonObject { ["paths"] = Paths(pic, jpeg), ["target_bytes"] = 5000 }));
        bool edited = false;
        shrinker.DuringRun = () => { if (!edited) { edited = true; shrinker.Files[jpeg] = shrinker.Files[jpeg] with { Size = 12 }; } };
        var raced = Call(shrinking, "apply_plan", new JsonObject { ["plan_id"] = (string)racing["plan_id"]! });
        shrinker.DuringRun = null;
        check(IsError(raced) && (int)Body(raced)["completed"]! == 1 && ((string)Body(raced)["error"]!).StartsWith(jpeg + ": The file changed since planning", StringComparison.Ordinal), "MCP plan_compress rechecks each file when it is held, so one edited during an earlier file's work is refused: " + Body(raced).ToJsonString());
        shrinker.FailOn = clip;
        var halfway = Call(shrinking, "apply_plan", new JsonObject { ["plan_id"] = (string)Body(Compress(new JsonObject { ["paths"] = Paths(pic, clip, quiet), ["target_bytes"] = 5_000_000 }))["plan_id"]! });
        check(IsError(halfway) && (int)Body(halfway)["completed"]! == 1 && ((string)Body(halfway)["error"]!).StartsWith(clip + ": The target size", StringComparison.Ordinal) && Body(halfway)["entry_id"] is JsonValue
            && !shrinker.Compressed.Any(c => c.Path == quiet && c.Bytes == 5_000_000), "MCP a compress that fails partway keeps and logs what was made, stops there: " + Body(halfway).ToJsonString());
        shrinker.FailOn = null;
        var offPlan = Body(Compress(new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 5000 }));
        shrinker.Enabled = false;
        check(IsError(Compress(new JsonObject { ["paths"] = Paths(pic), ["target_bytes"] = 5000 })) && IsError(Call(shrinking, "apply_plan", new JsonObject { ["plan_id"] = (string)offPlan["plan_id"]! })), "MCP plan_compress and its apply refused while Agent Tools is off");
        shrinker.Enabled = true;

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
