using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Swivel.Core;

/// What the Windows app does for the agent server: file checks, the work itself, the Recycle
/// Bin and history storage. Core decides everything else and is tested with a fake host.
public interface IMcpHost
{
    /// Agent Tools is switched on in Swivel's settings.
    bool Enabled { get; }
    DateTime UtcNow { get; }
    /// A plain absolute path to an existing local regular file, not a link or cloud placeholder.
    FileStamp Inspect(string path);
    /// Runs one allowed action on one input and returns the new file it created beside it.
    string Run(string action, string path, CancellationToken token);
    /// The identity of a file Swivel created, or null when it no longer exists.
    FileStamp? Stamp(string path);
    /// Moves a file Swivel created to the Recycle Bin only if it is still exactly that file.
    void Recycle(string path, FileStamp expected);
    /// Held around every read-modify-write of the history, so two servers never lose an entry.
    IDisposable LockHistory();
    string? LoadHistory();
    void SaveHistory(string json);
}

/// Agent-facing errors: for models, so they stay English like the Mac server's.
public sealed class McpToolError(string message) : Exception(message)
{
    /// Anything the host may throw for one file (missing, access denied, a damaged image, a full
    /// disk) becomes a tool error for that file; only failures of the process itself propagate.
    public static bool Expected(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}

public sealed record McpAction(string Id, string Title, string Suffix);

/// Swivel's agent tools over MCP (JSON-RPC 2.0, one message per line on stdin/stdout).
/// Read-only tools inspect; every write goes plan → apply_plan (single use, one hour, refused
/// if an input changed) and is logged so undo can move the created files to the Recycle Bin.
public sealed class McpSession
{
    public const int MaxLineChars = 16 * 1024 * 1024, MaxPaths = 100, MaxPlans = 64, MaxHistory = 200, MaxHistoryChars = 4 * 1024 * 1024;
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromHours(1);
    public static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

    public static readonly IReadOnlyList<McpAction> Actions = new McpAction[]
    {
        new("convert.png", "Convert to PNG", " converted"), new("convert.jpg", "Convert to JPEG", " converted"),
        new("convert.bmp", "Convert to BMP", " converted"), new("convert.tiff", "Convert to TIFF", " converted"),
        new("convert.gif", "Convert to Static GIF", " converted"),
        new("image.rotate", "Rotate 90° Clockwise", " rotated"), new("image.flip", "Flip Horizontally", " flipped"),
        new("image.gray", "Grayscale", " grayscale"),
    };

    private readonly IMcpHost host;
    private readonly string version;
    private readonly Dictionary<string, Plan> plans = new(StringComparer.Ordinal);

    private sealed record Plan(string Id, McpAction Action, IReadOnlyList<(string Path, FileStamp Stamp)> Inputs, DateTime Expires);

    public McpSession(IMcpHost host, string version) { this.host = host; this.version = version; }

    /// One line in, at most one line out (notifications get no reply).
    public string? Handle(string line, CancellationToken token = default)
    {
        if (line.Length > MaxLineChars) return Error(null, -32600, "Message too large.");
        JsonNode? message;
        try { message = JsonNode.Parse(line); }
        catch (JsonException) { return Error(null, -32700, "Parse error."); }
        if (message is JsonArray) return Error(null, -32600, "JSON-RPC batches are not supported.");
        // A response from the client (no method, result or error) needs no reply.
        if (message is JsonObject response && !response.ContainsKey("method") && (response.ContainsKey("result") || response.ContainsKey("error"))) return null;
        if (message is not JsonObject request || request["jsonrpc"]?.GetValueKind() != JsonValueKind.String || (string?)request["jsonrpc"] != "2.0"
            || request["method"] is not JsonValue methodValue || methodValue.GetValueKind() != JsonValueKind.String)
            return Error(IdOf(message), -32600, "Invalid request.");
        string method = (string)methodValue!;
        bool notification = !request.ContainsKey("id");
        JsonNode? id = request["id"];
        if (!notification && (id is null || id.GetValueKind() is not JsonValueKind.String and not JsonValueKind.Number)) return Error(null, -32600, "Invalid request id.");
        if (notification) return null; // initialized, cancelled and other notifications need no reply
        var parameters = request["params"] as JsonObject ?? new JsonObject();
        try
        {
            return method switch
            {
                "initialize" => Success(id, Initialize(parameters)),
                "ping" => Success(id, new JsonObject()),
                "tools/list" => Success(id, new JsonObject { ["tools"] = Tools() }),
                "tools/call" => Success(id, Call(parameters, token)),
                _ => Error(id, -32601, "Method not found: " + method),
            };
        }
        catch (McpInvalidParams error) { return Error(id, -32602, error.Message); }
        catch (Exception error) when (McpToolError.Expected(error)) { return Error(id, -32603, "Internal error: " + error.Message); }
    }

    private sealed class McpInvalidParams(string message) : Exception(message);

    private JsonObject Initialize(JsonObject parameters)
    {
        string requested = parameters["protocolVersion"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? (string)v! : "";
        return new JsonObject
        {
            ["protocolVersion"] = Versions.Contains(requested) ? requested : Versions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "swivel-windows", ["title"] = "Swivel (Windows preview)", ["version"] = version },
            ["instructions"] = "Local Windows file tools. Inspect first. Writes need plan_action, then apply_plan with the returned plan_id; outputs are new files beside the originals and nothing is overwritten. undo moves the files an apply created to the Recycle Bin if they are unchanged.",
        };
    }

    private static JsonArray Tools()
    {
        static JsonObject Paths() => new() { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = MaxPaths, ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Absolute Windows paths to existing local files." };
        static JsonObject Tool(string name, string title, string description, JsonObject properties, string[] required, bool readOnly, bool destructive)
            => new()
            {
                ["name"] = name, ["title"] = title, ["description"] = description,
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()), ["additionalProperties"] = false },
                ["annotations"] = new JsonObject { ["title"] = title, ["readOnlyHint"] = readOnly, ["destructiveHint"] = destructive, ["idempotentHint"] = readOnly, ["openWorldHint"] = false },
            };
        return new JsonArray(
            Tool("list_actions", "List actions", "Lists the actions plan_action accepts and what each one writes.", new JsonObject(), Array.Empty<string>(), true, false),
            Tool("inspect_files", "Inspect files", "Checks that paths are local regular files Swivel can read and lists the actions available for each. Reads metadata only.", new JsonObject { ["paths"] = Paths() }, new[] { "paths" }, true, false),
            Tool("plan_action", "Plan action", "Prepares one action for 1–100 files without writing anything. Returns a plan_id that apply_plan uses within one hour, once.",
                new JsonObject { ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Actions.Select(a => (JsonNode)a.Id).ToArray()) }, ["paths"] = Paths() }, new[] { "action", "paths" }, true, false),
            Tool("apply_plan", "Apply plan", "Runs a plan from plan_action. Refused if any input changed since planning. Creates new files beside the originals; never overwrites. Logged for undo.",
                new JsonObject { ["plan_id"] = new JsonObject { ["type"] = "string" } }, new[] { "plan_id" }, false, false),
            Tool("undo", "Undo", "Moves the files created by one logged apply to the Recycle Bin, only if each is unchanged. Without entry_id it takes the newest apply that is not fully undone; to reach an older one, pass its entry_id from history. dry_run reports without changing anything.",
                new JsonObject { ["entry_id"] = new JsonObject { ["type"] = "string" }, ["dry_run"] = new JsonObject { ["type"] = "boolean" } }, Array.Empty<string>(), false, true),
            Tool("history", "History", "Lists logged applies, newest first, with their outputs and whether they were undone.",
                new JsonObject { ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxHistory } }, Array.Empty<string>(), true, false));
    }

    private JsonObject Call(JsonObject parameters, CancellationToken token)
    {
        if (parameters["name"] is not JsonValue nameValue || nameValue.GetValueKind() != JsonValueKind.String) throw new McpInvalidParams("Missing tool name.");
        string name = (string)nameValue!;
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        if (parameters["arguments"] is not null and not JsonObject) throw new McpInvalidParams("Tool arguments must be an object.");
        Func<JsonObject, CancellationToken, JsonObject> tool = name switch
        {
            "list_actions" => (_, _) => ListActions(), "inspect_files" => InspectFiles, "plan_action" => PlanAction,
            "apply_plan" => ApplyPlan, "undo" => Undo, "history" => History,
            _ => throw new McpInvalidParams("Unknown tool: " + name),
        };
        try
        {
            if (!host.Enabled) throw new McpToolError("Agent Tools is off. Turn it on in Swivel's settings first.");
            return Result(tool(arguments, token), false);
        }
        catch (McpPartial partial) { return Result(partial.Body, true); }
        catch (OperationCanceledException) { return Result(new JsonObject { ["error"] = "Cancelled." }, true); }
        catch (Exception error) when (McpToolError.Expected(error)) { return Result(new JsonObject { ["error"] = error.Message }, true); }
    }

    private sealed class McpPartial(JsonObject body) : Exception("Partial result.") { public JsonObject Body { get; } = body; }

    private static JsonObject Result(JsonObject body, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = body.ToJsonString() }),
        ["structuredContent"] = body.DeepClone(), ["isError"] = isError,
    };

    private static JsonObject ListActions() => new()
    {
        ["actions"] = new JsonArray(Actions.Select(a => (JsonNode)new JsonObject
        {
            ["id"] = a.Id, ["title"] = a.Title, ["inputs"] = "PNG, JPEG, BMP, TIFF or static GIF images",
            ["writes"] = "A new " + ImageOutputInfo.Format(a.Id).ToUpperInvariant() + " file beside each original, named '<name>" + a.Suffix + "." + ImageOutputInfo.Format(a.Id) + "' or the next free numbered name.",
        }).ToArray()),
    };

    private static string[] PathsArgument(JsonObject arguments)
    {
        if (arguments["paths"] is not JsonArray array || array.Count is < 1 or > MaxPaths || array.Any(p => p is not JsonValue v || v.GetValueKind() != JsonValueKind.String))
            throw new McpToolError("paths must be a list of 1–100 strings.");
        var paths = array.Select(p => (string)p!).ToArray();
        if (!paths.All(IsWellFormed)) throw new McpToolError("paths must be valid Unicode text.");
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new McpToolError("Each path may appear only once.");
        return paths;
    }

    private JsonObject InspectFiles(JsonObject arguments, CancellationToken token)
    {
        var files = new JsonArray();
        foreach (string path in PathsArgument(arguments))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var stamp = host.Inspect(path);
                files.Add(new JsonObject
                {
                    ["path"] = path, ["ok"] = true, ["size"] = stamp.Size,
                    ["actions"] = new JsonArray((Catalog.IsImage(path) ? Actions.Select(a => (JsonNode)a.Id) : Enumerable.Empty<JsonNode>()).ToArray()),
                });
            }
            catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { files.Add(new JsonObject { ["path"] = path, ["ok"] = false, ["error"] = error.Message }); }
        }
        return new JsonObject { ["files"] = files };
    }

    private JsonObject PlanAction(JsonObject arguments, CancellationToken token)
    {
        string id = arguments["action"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? (string)v! : "";
        var action = Actions.FirstOrDefault(a => a.Id == id) ?? throw new McpToolError("Unknown action. Use list_actions.");
        var paths = PathsArgument(arguments);
        var inputs = new List<(string, FileStamp)>();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!Catalog.IsImage(path)) throw new McpToolError(path + ": this action needs a PNG, JPEG, BMP, TIFF or GIF image.");
            try { inputs.Add((path, host.Inspect(path))); }
            catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { throw new McpToolError(path + ": " + error.Message); }
        }
        DateTime now = host.UtcNow;
        foreach (var stale in plans.Values.Where(p => p.Expires <= now).ToArray()) plans.Remove(stale.Id);
        if (plans.Count >= MaxPlans) throw new McpToolError("Too many unused plans. Apply or let some expire first.");
        var plan = new Plan(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), action, inputs, now + PlanLifetime);
        plans[plan.Id] = plan;
        string extension = ImageOutputInfo.Format(action.Id);
        return new JsonObject
        {
            ["plan_id"] = plan.Id, ["action"] = action.Id, ["expires"] = plan.Expires.ToString("O"),
            ["inputs"] = new JsonArray(inputs.Select(i => (JsonNode)new JsonObject
            {
                ["path"] = i.Item1,
                ["output"] = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(i.Item1) ?? "", System.IO.Path.GetFileNameWithoutExtension(i.Item1) + action.Suffix + "." + extension) + " (or the next free numbered name)",
            }).ToArray()),
        };
    }

    private JsonObject ApplyPlan(JsonObject arguments, CancellationToken token)
    {
        string id = arguments["plan_id"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? (string)v! : "";
        if (!plans.Remove(id, out var plan)) throw new McpToolError("Unknown or already used plan_id. Plan again.");
        if (plan.Expires <= host.UtcNow) throw new McpToolError("This plan expired. Plan again.");
        using (host.LockHistory()) LoadHistory(); // an unreadable log stops the apply before anything is written
        foreach (var (path, stamp) in plan.Inputs)
        {
            FileStamp now;
            try { now = host.Inspect(path); }
            catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { throw new McpToolError(path + ": " + error.Message + " Nothing was applied."); }
            if (!now.SameObjectAndContent(stamp)) throw new McpToolError(path + " changed since planning. Nothing was applied; plan again.");
        }
        var outputs = new JsonArray();
        var entry = new JsonObject
        {
            ["id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), ["time"] = host.UtcNow.ToString("O"),
            ["action"] = plan.Action.Id, ["outputs"] = outputs,
        };
        string? failure = null;
        foreach (var (path, _) in plan.Inputs)
        {
            if (token.IsCancellationRequested) { failure = "Cancelled."; break; }
            try
            {
                string output = host.Run(plan.Action.Id, path, token);
                var stamp = host.Stamp(output) ?? throw new McpToolError("The new file disappeared.");
                outputs.Add(StampNode(output, stamp));
            }
            catch (Exception error) when (McpToolError.Expected(error))
            {
                failure = path + ": " + (error is OperationCanceledException ? "Cancelled." : error.Message);
                break;
            }
        }
        string? unlogged = null;
        if (outputs.Count > 0)
        {
            // Reloaded under the lock, so another server's apply since planning is kept.
            try { using (host.LockHistory()) Record(LoadHistory(), entry); }
            catch (Exception error) when (McpToolError.Expected(error)) { unlogged = error.Message; }
        }
        var body = new JsonObject
        {
            ["entry_id"] = outputs.Count > 0 ? (string)entry["id"]! : null, ["action"] = plan.Action.Id,
            ["outputs"] = new JsonArray(outputs.Select(o => (JsonNode)(string)o!["path"]!).ToArray()),
            ["completed"] = outputs.Count, ["total"] = plan.Inputs.Count,
        };
        if (unlogged is not null)
        {
            body["entry_id"] = null;
            body["error"] = (failure is null ? "" : failure + " ") + "The new files were kept but could not be logged for undo (" + unlogged + "); remove them yourself if they are not wanted.";
            throw new McpPartial(body);
        }
        if (failure is null) return body;
        body["error"] = failure + " Files already created were kept and logged for undo.";
        throw new McpPartial(body);
    }

    private JsonObject Undo(JsonObject arguments, CancellationToken token)
    {
        bool dryRun = arguments["dry_run"] is JsonValue d && d.GetValueKind() is JsonValueKind.True;
        string? wanted = arguments["entry_id"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? (string)v! : null;
        using var held = host.LockHistory();
        var history = LoadHistory();
        var entries = (JsonArray)history["entries"]!;
        var entry = (wanted is null
            ? entries.OfType<JsonObject>().LastOrDefault(e => ((JsonArray)e["outputs"]!).Any(o => o!["undone"]?.GetValue<bool>() != true))
            : entries.OfType<JsonObject>().FirstOrDefault(e => (string?)e["id"] == wanted))
            ?? throw new McpToolError(wanted is null ? "Nothing to undo." : "Unknown entry_id. Use history.");
        var results = new JsonArray();
        bool changed = false;
        try
        {
            foreach (var output in ((JsonArray)entry["outputs"]!).OfType<JsonObject>())
            {
                token.ThrowIfCancellationRequested();
                string path = (string)output["path"]!;
                if (output["undone"]?.GetValue<bool>() == true) { results.Add(new JsonObject { ["path"] = path, ["result"] = "already undone" }); continue; }
                var expected = ReadStamp(output);
                try
                {
                    var current = host.Stamp(path);
                    if (current is null) { results.Add(new JsonObject { ["path"] = path, ["result"] = "missing; left as is" }); continue; }
                    if (!current.Value.SameObjectAndContent(expected)) { results.Add(new JsonObject { ["path"] = path, ["result"] = "changed since apply; left alone" }); continue; }
                    if (dryRun) { results.Add(new JsonObject { ["path"] = path, ["result"] = "would move to the Recycle Bin" }); continue; }
                    host.Recycle(path, expected);
                    output["undone"] = true; changed = true;
                    results.Add(new JsonObject { ["path"] = path, ["result"] = "moved to the Recycle Bin" });
                }
                catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { results.Add(new JsonObject { ["path"] = path, ["result"] = "not moved: " + error.Message }); }
            }
        }
        finally
        {
            // Files already in the Recycle Bin stay marked, even if a later step stopped the undo.
            if (changed) Save(history);
        }
        bool complete = ((JsonArray)entry["outputs"]!).All(o => o!["undone"]?.GetValue<bool>() == true);
        return new JsonObject { ["entry_id"] = (string?)entry["id"], ["dry_run"] = dryRun, ["complete"] = complete, ["results"] = results };
    }

    private JsonObject History(JsonObject arguments, CancellationToken token)
    {
        int limit = arguments["limit"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out int n) ? Math.Clamp(n, 1, MaxHistory) : 20;
        var entries = ((JsonArray)LoadHistory()["entries"]!).OfType<JsonObject>().Reverse().Take(limit);
        return new JsonObject
        {
            ["entries"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
            {
                ["entry_id"] = (string?)e["id"], ["time"] = (string?)e["time"], ["action"] = (string?)e["action"],
                ["outputs"] = new JsonArray(((JsonArray)e["outputs"]!).OfType<JsonObject>().Select(o => (JsonNode)new JsonObject { ["path"] = (string?)o["path"], ["undone"] = o["undone"]?.GetValue<bool>() == true }).ToArray()),
            }).ToArray()),
        };
    }

    private static JsonObject StampNode(string path, FileStamp stamp) => new()
    {
        ["path"] = path, ["volume"] = stamp.Volume, ["index"] = stamp.Index, ["size"] = stamp.Size,
        ["write"] = stamp.WriteTime, ["creation"] = stamp.CreationTime, ["undone"] = false,
    };

    private static FileStamp ReadStamp(JsonObject node) => new(node["volume"]!.GetValue<uint>(), node["index"]!.GetValue<ulong>(),
        node["size"]!.GetValue<ulong>(), node["write"]!.GetValue<long>(), node["creation"]!.GetValue<long>());

    /// A damaged log is never overwritten: applies and undo stop until it is readable again.
    private JsonObject LoadHistory()
    {
        string? json = host.LoadHistory();
        if (json is null) return new JsonObject { ["version"] = 1, ["entries"] = new JsonArray() };
        try
        {
            if (json.Length > MaxHistoryChars || JsonNode.Parse(json) is not JsonObject root || root["version"]?.GetValue<int>() != 1 || root["entries"] is not JsonArray entries)
                throw new FormatException();
            foreach (var entry in entries)
            {
                if (entry is not JsonObject e || !IsString(e["id"]) || !IsString(e["action"]) || e["time"] is not null && !IsString(e["time"]) || e["outputs"] is not JsonArray outputs) throw new FormatException();
                foreach (var output in outputs)
                {
                    if (output is not JsonObject o || !IsString(o["path"]) || o["undone"] is not null && o["undone"]!.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException();
                    ReadStamp(o);
                }
            }
            return root;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or NullReferenceException or ArgumentException)
        {
            throw new McpToolError("Swivel's agent history is unreadable, so nothing was changed. It is kept as it is.");
        }
    }

    private static bool IsString(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    private static bool IsWellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (char.IsSurrogate(text[i])) return false;
        }
        return true;
    }

    private void Record(JsonObject history, JsonObject entry)
    {
        var entries = (JsonArray)history["entries"]!;
        entries.Add(entry);
        while (entries.Count > MaxHistory) entries.RemoveAt(0);
        Save(history);
    }

    private void Save(JsonObject history)
    {
        string json = history.ToJsonString();
        // Drop the oldest entries rather than fail to log a new one.
        var entries = (JsonArray)history["entries"]!;
        while (json.Length > MaxHistoryChars && entries.Count > 1) { entries.RemoveAt(0); json = history.ToJsonString(); }
        host.SaveHistory(json);
    }

    private static JsonNode? IdOf(JsonNode? message)
        => message is JsonObject o && o["id"] is JsonValue v && v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number ? v.DeepClone() : null;

    private static string Success(JsonNode? id, JsonObject result)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
}
