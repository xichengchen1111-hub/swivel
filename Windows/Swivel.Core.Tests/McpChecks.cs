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
        public FileStamp Inspect(string path) => Files.TryGetValue(path, out var s) ? s : throw (path.EndsWith("denied.png") ? new UnauthorizedAccessException("Access is denied.") : new UserError("Choose a local regular file."));
        public string Run(string action, string path, CancellationToken token)
        {
            if (path == FailOn) throw Throw ?? new UserError("Could not read the image.");
            DuringRun?.Invoke();
            string output = System.IO.Path.ChangeExtension(path, null) + " converted." + ImageOutputInfo.Format(action);
            while (Files.ContainsKey(output)) output = output.Replace(" converted", " converted 2");
            Add(output); return output;
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
        check(tools.SequenceEqual(new[] { "list_actions", "inspect_files", "plan_action", "apply_plan", "undo", "history" }), "MCP tool list");
        check((int)Call(session, "format_disk", new JsonObject())["error"]!["code"]! == -32602, "MCP unknown tool is an invalid-params error");

        host.Enabled = false;
        check(IsError(Call(session, "list_actions", new JsonObject())) && ((string)Body(Call(session, "list_actions", new JsonObject()))["error"]!).Contains("Agent Tools is off"), "MCP tools refuse while Agent Tools is off");
        host.Enabled = true;
        check(((JsonArray)Body(Call(session, "list_actions", new JsonObject()))["actions"]!).Count == McpSession.Actions.Count, "MCP list_actions");

        string photo = @"C:\Users\me\Pictures\photo.jpg", doc = @"C:\Users\me\notes.txt";
        host.Add(photo); host.Add(doc);
        var inspected = (JsonArray)Body(Call(session, "inspect_files", new JsonObject { ["paths"] = Paths(photo, doc, @"C:\missing.png") }))["files"]!;
        check(inspected[0]!["ok"]!.GetValue<bool>() && ((JsonArray)inspected[0]!["actions"]!).Count == McpSession.Actions.Count && ((JsonArray)inspected[1]!["actions"]!).Count == 0 && !inspected[2]!["ok"]!.GetValue<bool>(), "MCP inspect_files reports each path");
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
