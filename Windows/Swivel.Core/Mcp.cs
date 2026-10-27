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
    /// The one clean flat QR code in an image (QrFlatRead's strict rules); a UserError when none.
    QrReadResult ReadCode(string path, CancellationToken token);
    /// The installed Windows OCR language tags; empty when OCR cannot run here (no MSIX package
    /// identity, or no language installed).
    IReadOnlyList<string> TextLanguages { get; }
    /// Text recognized in an image with an installed Windows OCR language: one of TextLanguages,
    /// or the user's profile languages when null.
    string RecognizeText(string path, string? language, CancellationToken token);
    /// Size, length, sound and bit rate of a video Swivel can compress; a UserError when Windows
    /// can't open it or it is stored turned.
    VideoProbe ProbeVideo(string path, CancellationToken token);
    /// Compresses one image (to JPEG) or video (to H.264/AAC MP4) into a new file beside it of at
    /// most maxBytes, and returns that file; a UserError, and no file, when it can't get there.
    /// Refused when the file, once held, no longer matches its planned stamp.
    string Compress(string path, long maxBytes, FileStamp expected, CancellationToken token);
    /// The body text of a document DocumentText reads (plain text, DOCX, ODT, RTF, static HTML).
    string ReadDocumentText(string path, CancellationToken token);
    /// Whether a file, folder or link has this name; only "not found" counts as free.
    bool NameTaken(string path);
    /// The file's own name as the file system stores it: its long name, in its exact case.
    string StoredName(string path);
    /// Throws when Swivel cannot rename this file (another file system, a read-only file, a hard link).
    void CheckRename(string path);
    /// Renames files within their folders (staged for swaps and case changes, never replacing
    /// anything) only if each is still exactly the file expected; a failed step rolls back. The
    /// outcome says where each file is afterwards.
    RenameOutcome Rename(IReadOnlyList<RenamePair> plan, IReadOnlyList<FileStamp> expected, CancellationToken token);
    /// Throws unless this is an existing local NTFS folder Swivel can move files into; returns
    /// its identity, so a folder replaced before the move can be told apart.
    MoveIdentity CheckFolder(string folder);
    /// False when the file is already in that folder; throws when Swivel cannot move it there
    /// (another drive or file system, a read-only file, a hard link).
    bool CheckMove(string path, string folder);
    /// Moves files into the one folder their destinations name, keeping their names and never
    /// replacing anything, only if each is still exactly the file expected; a failed step moves
    /// the others back. With `folder`, the destination must still be that folder. The outcome
    /// says where each file is afterwards.
    RenameOutcome Move(IReadOnlyList<RenamePair> plan, IReadOnlyList<FileStamp> expected, MoveIdentity? folder, CancellationToken token);
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
    /// Text returned by extract_text: per file and for the whole reply, in UTF-16 units. Longer
    /// text is refused, never cut.
    public const int MaxFileText = 200_000, MaxReplyText = 400_000;
    public const string NoOcr = "Windows OCR is not available here: reading text in images needs Swivel's MSIX package and an OCR language installed in Windows.";
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromHours(1);
    public static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

    public static readonly IReadOnlyList<McpAction> Actions = new McpAction[]
    {
        new("convert.png", "Convert to PNG", " converted"), new("convert.jpg", "Convert to JPEG", " converted"),
        new("convert.bmp", "Convert to BMP", " converted"), new("convert.tiff", "Convert to TIFF", " converted"),
        new("convert.gif", "Convert to Static GIF", " converted"),
        new("image.rotate", "Rotate 90° Clockwise", " rotated"), new("image.flip", "Flip Horizontally", " flipped"),
        new("image.gray", "Grayscale", " grayscale"),
        // Run on this PC with the pinned U²-Net model; the result is a PNG either way.
        new("image.removebg", "Remove Background", " cutout"), new("image.blurbg", "Blur Background", " blurred"),
    };
    /// plan_compress's work, logged like an action; not offered through plan_action.
    private static readonly McpAction Compression = new("compress", "Compress to a size", " compressed");
    public const long MinTarget = 1_000, MaxTarget = 1_000_000_000_000;

    private readonly IMcpHost host;
    private readonly string version;
    private readonly Dictionary<string, Plan> plans = new(StringComparer.Ordinal);

    /// An action plan has Action; a rename plan has Renames and a move plan Moves (one pair per
    /// input, in order).
    private sealed record Plan(string Id, McpAction? Action, IReadOnlyList<RenamePair>? Renames, IReadOnlyList<(string Path, FileStamp Stamp)> Inputs, DateTime Expires, IReadOnlyList<RenamePair>? Moves = null, MoveIdentity? Folder = null, long? Target = null);

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
            ["instructions"] = "Local Windows file tools. Inspect first. extract_text and read_codes only read. Writes need plan_action, plan_compress, plan_rename or plan_move, then apply_plan with the returned plan_id; outputs (including compressed copies) are new files beside the originals, renames stay in each file's folder, moves stay on one drive and keep names, and nothing is overwritten. undo moves the files an apply created to the Recycle Bin, or renames or moves files back, if they are unchanged.",
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
            Tool("inspect_files", "Inspect files", "Checks that paths are local regular files Swivel can read and lists the plan_action actions and other tools available for each (extract_text for images only where Windows OCR can run). Reads metadata only.", new JsonObject { ["paths"] = Paths() }, new[] { "paths" }, true, false),
            Tool("extract_text", "Extract text", "Returns the text of images (Windows OCR on this PC with an installed language; needs Swivel's MSIX package) and of plain-text, DOCX, ODT, RTF or static HTML documents (body text only). Read-only; Swivel uses no network. A file's text over " + MaxFileText.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " UTF-16 units, or text past " + MaxReplyText.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " in one reply, is refused rather than cut. Hosts may still shorten long tool replies, so ask for a few files at a time.",
                new JsonObject { ["paths"] = Paths(), ["language"] = new JsonObject { ["type"] = "string", ["description"] = "Optional OCR language tag such as en-US or zh-Hans-CN, matched to the installed OCR languages ignoring case. Default: the user's Windows language settings. Ignored for documents." } }, new[] { "paths" }, true, false),
            Tool("read_codes", "Read QR codes", "Reads one clean, flat QR code per image, such as a saved or generated QR image: square, upright or turned in 90° steps, an exact four-module white border and sharp pixels (whole-number scaling is fine). Photos, damaged or cropped codes, smoothly resized images and barcodes are not read. Read-only.",
                new JsonObject { ["paths"] = Paths() }, new[] { "paths" }, true, false),
            Tool("plan_action", "Plan action", "Prepares one action for 1–100 files without writing anything. Returns a plan_id that apply_plan uses within one hour, once.",
                new JsonObject { ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Actions.Select(a => (JsonNode)a.Id).ToArray()) }, ["paths"] = Paths() }, new[] { "action", "paths" }, true, false),
            Tool("plan_compress", "Plan compressing to a size", "Plans compressing 1–100 images or videos so each new file is at most target_bytes (e.g. 500000 for under 500 KB). Images (PNG, JPEG, BMP, TIFF, static GIF; up to 16 megapixels and 16384 pixels per side) become JPEG without their metadata, transparent areas on white: quality is lowered only as far as needed, and if even the lowest quality is too big the long edge shrinks in 15% steps down to 320 pixels. Videos (MP4, MOV, M4V, WMV, up to 2 hours) become H.264/AAC MP4 at a bit rate worked out from their length, never above the original's, at most 1920 pixels on the long edge and smaller at low rates; videos stored turned (as phones often record) are refused, and encoding can take minutes. Originals are kept; new files go beside them. Nothing changes until apply_plan.",
                new JsonObject
                {
                    ["paths"] = Paths(),
                    ["target_bytes"] = new JsonObject { ["type"] = "integer", ["minimum"] = MinTarget, ["maximum"] = MaxTarget, ["description"] = "Largest size each new file may have, in bytes" },
                    ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("same", "jpg"), ["description"] = "Optional. Images always become JPEG and videos MP4 here: jpg, or same when every file is already a JPEG image or an MP4 video." },
                }, new[] { "paths", "target_bytes" }, true, false),
            Tool("plan_rename", "Plan renaming files", "Plans renaming 1–100 files where they are: replace the whole name (base_name), find/replace_with (case-sensitive), add prefix or suffix (before the extension), and optionally number them (-001, -002… in the order of paths; start and digits need number: true). Extensions are kept; each text is at most 120 characters, and added text can't contain \\, / or invisible, blank-looking or text-direction characters. Pass each path with the file's exact name as stored (same case, not an 8.3 short name). Only files on NTFS that are not read-only or hard-linked can be renamed. Returns each old and new path with any problem, and a plan_id when nothing conflicts. Nothing changes until apply_plan.",
                new JsonObject
                {
                    ["paths"] = Paths(),
                    ["base_name"] = new JsonObject { ["type"] = "string", ["description"] = "New name without extension; usually combined with number" },
                    ["find"] = new JsonObject { ["type"] = "string" }, ["replace_with"] = new JsonObject { ["type"] = "string" },
                    ["prefix"] = new JsonObject { ["type"] = "string" }, ["suffix"] = new JsonObject { ["type"] = "string", ["description"] = "Added before the extension" },
                    ["number"] = new JsonObject { ["type"] = "boolean", ["description"] = "Append -NNN in the order of paths" },
                    ["start"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 99_999_999, ["description"] = "First number (default 1)" },
                    ["digits"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 8, ["description"] = "Digits in each number (default 3)" },
                }, new[] { "paths" }, true, false),
            Tool("plan_move", "Plan moving files", "Plans moving 1–100 files into an existing folder on the same NTFS drive, keeping their names. Pass each path with the file's exact name as stored (same case, not an 8.3 short name). Files already in that folder, names already taken there (nothing is overwritten or renumbered), two files with one name, and files on another drive, read-only or hard-linked are reported on their rows; folders are not moved. Returns each old and new path with any problem, and a plan_id when nothing conflicts. Nothing changes until apply_plan.",
                new JsonObject { ["paths"] = Paths(), ["destination"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path of an existing local folder" } }, new[] { "paths", "destination" }, true, false),
            Tool("apply_plan", "Apply plan", "Runs a plan from plan_action, plan_compress, plan_rename or plan_move. Refused if any input changed since planning. Creates new files beside the originals, renames files within their folders, or moves files into the planned folder; never overwrites. Logged for undo.",
                new JsonObject { ["plan_id"] = new JsonObject { ["type"] = "string" } }, new[] { "plan_id" }, false, false),
            Tool("undo", "Undo", "Moves the files created by one logged apply to the Recycle Bin, only if each is unchanged; for a rename, renames all its files back together, only if none changed and every old name is free; for a move, moves each unchanged file back to its old folder if its old name is free, leaving the others where they are. Without entry_id it takes the newest apply that is not fully undone; to reach an older one, pass its entry_id from history. dry_run reports without changing anything.",
                new JsonObject { ["entry_id"] = new JsonObject { ["type"] = "string" }, ["dry_run"] = new JsonObject { ["type"] = "boolean" } }, Array.Empty<string>(), false, true),
            Tool("history", "History", "Lists logged applies, newest first, with their outputs, renames or moves and whether they were undone.",
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
            "list_actions" => (_, _) => ListActions(), "inspect_files" => InspectFiles, "extract_text" => ExtractText, "read_codes" => ReadCodes, "plan_action" => PlanAction, "plan_compress" => PlanCompress, "plan_rename" => PlanRename, "plan_move" => PlanMove,
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
        var files = new JsonArray(); bool? ocr = null; // asked once, only when an image is listed
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
                    ["tools"] = new JsonArray((Catalog.IsImage(path) ? ((ocr ??= host.TextLanguages.Count > 0) ? new[] { "extract_text", "read_codes" } : new[] { "read_codes" }) : IsDocument(path) ? new[] { "extract_text" } : Array.Empty<string>())
                        .Concat(Catalog.IsImage(path) || VideoBudget.IsVideo(path) ? new[] { "plan_compress" } : Array.Empty<string>()).Append("plan_rename").Append("plan_move").Select(t => (JsonNode)t).ToArray()),
                });
            }
            catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { files.Add(new JsonObject { ["path"] = path, ["ok"] = false, ["error"] = error.Message }); }
        }
        return new JsonObject { ["files"] = files };
    }

    private static bool IsDocument(string path)
        => DocumentText.Extensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    private JsonObject ExtractText(JsonObject arguments, CancellationToken token)
    {
        string? language = null;
        if (arguments["language"] is { } node)
        {
            if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String || (string)v! is not { Length: >= 2 and <= 64 } tag
                || !tag.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
                throw new McpToolError("language must be a language tag such as en-US.");
            language = tag;
        }
        var paths = PathsArgument(arguments);
        if (paths.FirstOrDefault(p => !Catalog.IsImage(p) && !IsDocument(p)) is { } other)
            throw new McpToolError("extract_text takes images or plain-text, DOCX, ODT, RTF or HTML documents; " + other + " is neither.");
        // The OCR languages are read once; a tag is checked before any image is decoded and passed
        // on as Windows spells it.
        var languages = paths.Any(Catalog.IsImage) ? host.TextLanguages : Array.Empty<string>();
        if (language is not null && languages.Count > 0)
            language = languages.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpToolError("The OCR language " + language + " is not installed in Windows. Installed: " + string.Join(", ", languages) + ".");
        var files = new JsonArray(); int total = 0;
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                host.Inspect(path);
                bool image = Catalog.IsImage(path);
                if (image && languages.Count == 0) throw new McpToolError(NoOcr);
                string text = image ? host.RecognizeText(path, language, token) : host.ReadDocumentText(path, token);
                token.ThrowIfCancellationRequested();
                if (!IsWellFormed(text)) throw new McpToolError("The text is not valid Unicode.");
                if (text.Length > MaxFileText) throw new McpToolError("The text has " + text.Length + " UTF-16 units, more than " + MaxFileText + "; open it in Swivel instead.");
                if (total + text.Length > MaxReplyText) throw new McpToolError("The reply already holds " + total + " UTF-16 units of text; ask for this file on its own.");
                total += text.Length;
                files.Add(new JsonObject { ["path"] = path, ["ok"] = true, ["kind"] = image ? "image" : "document", ["text"] = text });
            }
            catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { files.Add(new JsonObject { ["path"] = path, ["ok"] = false, ["error"] = error.Message }); }
        }
        return new JsonObject { ["files"] = files };
    }

    private JsonObject ReadCodes(JsonObject arguments, CancellationToken token)
    {
        var paths = PathsArgument(arguments);
        if (paths.FirstOrDefault(p => !Catalog.IsImage(p)) is { } other) throw new McpToolError("read_codes takes PNG, JPEG, BMP, TIFF or GIF images; " + other + " is not one.");
        var codes = new JsonArray(); var unread = new JsonArray();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                host.Inspect(path);
                var code = host.ReadCode(path, token);
                token.ThrowIfCancellationRequested();
                if (!IsWellFormed(code.Text)) throw new McpToolError("The code's text is not valid Unicode.");
                codes.Add(new JsonObject { ["path"] = path, ["type"] = "QR", ["value"] = code.Text, ["version"] = code.Version, ["correction"] = "LMQH"[(int)code.Correction].ToString() });
            }
            catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { unread.Add(new JsonObject { ["path"] = path, ["error"] = error.Message }); }
        }
        return new JsonObject { ["codes"] = codes, ["not_read"] = unread, ["note"] = "The text is what the code holds; it does not show who made the code or that a link is safe." };
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
        var plan = Store(action, null, inputs);
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

    private JsonObject PlanCompress(JsonObject arguments, CancellationToken token)
    {
        foreach (var (key, _) in arguments)
            if (key is not ("paths" or "target_bytes" or "format")) throw new McpToolError("Unknown argument " + key + ". plan_compress takes paths, target_bytes and format.");
        var paths = PathsArgument(arguments);
        if (arguments["target_bytes"] is not JsonValue t || t.GetValueKind() != JsonValueKind.Number || !t.TryGetValue(out double bytes) || bytes != Math.Floor(bytes) || bytes < MinTarget || bytes > MaxTarget)
            throw new McpToolError("target_bytes must be a whole number of bytes from 1,000 to 1,000,000,000,000.");
        long target = (long)bytes;
        string? format = null;
        if (arguments["format"] is not null)
        {
            format = arguments["format"] is JsonValue f && f.GetValueKind() == JsonValueKind.String ? ((string)f!).ToLowerInvariant() : "";
            if (format == "jpeg") format = "jpg";
            if (format is "heic" or "png") throw new McpToolError("Swivel for Windows compresses images to a size only as JPEG. Leave format out, or use jpg.");
            if (format is not ("same" or "jpg")) throw new McpToolError("format must be same or jpg.");
        }
        var inputs = new List<(string, FileStamp)>(); var rows = new JsonArray();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            bool image = Catalog.IsImage(path);
            if (!image && !VideoBudget.IsVideo(path)) throw new McpToolError(path + ": plan_compress takes PNG, JPEG, BMP, TIFF or GIF images and MP4, MOV, M4V or WMV videos.");
            if (format == "same" && System.IO.Path.GetExtension(path).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".mp4"))
                throw new McpToolError(path + ": format same keeps only JPEG images and MP4 videos in their format; Swivel for Windows compresses other images to JPEG and other videos to MP4. Leave format out.");
            var row = new JsonObject { ["path"] = path, ["kind"] = image ? "image" : "video" };
            try
            {
                var stamp = host.Inspect(path);
                inputs.Add((path, stamp));
                if (!image)
                {
                    var first = VideoBudget.Plan(target, host.ProbeVideo(path, token));
                    row["width"] = first.Width; row["height"] = first.Height; row["video_bitrate"] = first.VideoBitrate; row["audio_bitrate"] = first.AudioBitrate;
                }
                if (stamp.Size <= (ulong)target) row["note"] = "Already " + stamp.Size.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " bytes, under the target; it is still re-encoded.";
            }
            catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { throw new McpToolError(path + ": " + error.Message); }
            row["output"] = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", System.IO.Path.GetFileNameWithoutExtension(path) + Compression.Suffix + (image ? ".jpg" : ".mp4")) + " (or the next free numbered name)";
            rows.Add(row);
        }
        var plan = Store(Compression, null, inputs, target: target);
        return new JsonObject
        {
            ["plan_id"] = plan.Id, ["action"] = Compression.Id, ["expires"] = plan.Expires.ToString("O"), ["target_bytes"] = target, ["inputs"] = rows,
            ["note"] = "Compression lowers quality and may lower dimensions; images become JPEG without their metadata, videos H.264/AAC MP4. Originals are kept unchanged.",
        };
    }

    private Plan Store(McpAction? action, IReadOnlyList<RenamePair>? renames, IReadOnlyList<(string, FileStamp)> inputs, IReadOnlyList<RenamePair>? moves = null, MoveIdentity? folder = null, long? target = null)
    {
        DateTime now = host.UtcNow;
        foreach (var stale in plans.Values.Where(p => p.Expires <= now).ToArray()) plans.Remove(stale.Id);
        if (plans.Count >= MaxPlans) throw new McpToolError("Too many unused plans. Apply or let some expire first.");
        var plan = new Plan(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), action, renames, inputs, now + PlanLifetime, moves, folder, target);
        plans[plan.Id] = plan;
        return plan;
    }

    private static readonly string[] RenameArguments = { "paths", "base_name", "find", "replace_with", "prefix", "suffix", "number", "start", "digits" };

    private JsonObject PlanRename(JsonObject arguments, CancellationToken token)
    {
        // A misspelt rule ("replace") must not quietly turn find into a deletion.
        foreach (var (key, _) in arguments)
            if (!RenameArguments.Contains(key)) throw new McpToolError("Unknown argument " + key + ". plan_rename takes " + string.Join(", ", RenameArguments) + ".");
        var paths = PathsArgument(arguments);
        string Text(string name, bool added)
        {
            if (arguments[name] is null) return "";
            if (arguments[name] is not JsonValue v || v.GetValueKind() != JsonValueKind.String || !IsWellFormed((string)v!)) throw new McpToolError(name + " must be text.");
            string text = (string)v!;
            // Text that ends up in names: no folders, and nothing that hides, disguises or reorders a name.
            if (added && (text.Contains('\\') || text.Contains('/'))) throw new McpToolError(name + " can't contain \\ or /: files stay in their folders.");
            if (added && HiddenCharacter(text) is int code) throw new McpToolError(name + " contains an invisible, blank-looking or text-direction character (U+" + code.ToString("X4") + ").");
            return text;
        }
        int Whole(string name, int fallback, int min, int max)
        {
            if (arguments[name] is null) return fallback;
            // 1.0 is a whole number too.
            if (arguments[name] is not JsonValue v || v.GetValueKind() != JsonValueKind.Number || !v.TryGetValue(out double n) || n != Math.Floor(n) || n < min || n > max)
                throw new McpToolError(name + " must be a whole number from " + min + " to " + max + ".");
            return (int)n;
        }
        bool number = arguments["number"] switch
        {
            null => false,
            JsonValue b when b.GetValueKind() is JsonValueKind.True or JsonValueKind.False => b.GetValue<bool>(),
            _ => throw new McpToolError("number must be true or false."),
        };
        if (!number && (arguments["start"] is not null || arguments["digits"] is not null)) throw new McpToolError("start and digits apply only with number: true.");
        var rules = new RenameRules(Text("base_name", true), Text("find", false), Text("replace_with", true), Text("prefix", true), Text("suffix", true), number, Whole("start", 1, 0, 99_999_999), Whole("digits", 3, 1, 8));
        if (rules.Base.Length == 0 && rules.Find.Length == 0 && rules.Prefix.Length == 0 && rules.Suffix.Length == 0 && !rules.Number)
            throw new McpToolError("Give at least one of base_name, find, prefix, suffix or number.");
        rules.CheckLimits(paths.Length);
        var inputs = SpeltInputs(paths, token);
        var destinations = paths.Select(rules.Destination).ToArray();
        var rows = new JsonArray(); bool problems = false;
        for (int i = 0; i < paths.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            string from = paths[i], to = destinations[i], name = to[(to.LastIndexOf('\\') + 1)..];
            bool changed = to != from;
            string? issue = null;
            if (changed)
            {
                int dot = name.LastIndexOf('.');
                if (name.Length == 0 || (dot > 0 ? name[..dot] : name).Trim(' ').Length == 0 || name[0] == '.' && from[from.LastIndexOf('\\') + 1] != '.')
                    issue = "Nothing, or only spaces, would be left of the name before its extension.";
                else
                {
                    try { WindowsPathPolicy.Validate(to); }
                    catch (UserError error) { issue = error.Message; }
                }
            }
            if (issue is null && destinations.Where((d, j) => j != i && string.Equals(d, to, StringComparison.OrdinalIgnoreCase)).Any())
                issue = "Another file in this plan would get the same name.";
            if (issue is null && changed)
            {
                try { host.CheckRename(from); }
                catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { issue = "Swivel can't rename this file: " + error.Message; }
            }
            // A name one of these files has now is freed by the rename (swaps, chains, case changes).
            if (issue is null && changed && !paths.Contains(to, StringComparer.OrdinalIgnoreCase))
            {
                try { if (host.NameTaken(to)) issue = "A file or folder with this name already exists; nothing is overwritten."; }
                catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { issue = "Could not check whether this name is free: " + error.Message; }
            }
            var row = new JsonObject { ["from"] = from, ["to"] = to };
            if (issue is not null) { row["issue"] = issue; problems = true; }
            if (!changed) row["unchanged"] = true;
            rows.Add(row);
        }
        if (problems) return new JsonObject { ["can_apply"] = false, ["renames"] = rows, ["next"] = "Some names conflict or are invalid; change the rules and plan again." };
        if (destinations.SequenceEqual(paths)) return new JsonObject { ["can_apply"] = false, ["renames"] = rows, ["next"] = "These rules don't change any name." };
        // Files keeping their names are left out of the apply: it neither holds nor checks them.
        var moving = Enumerable.Range(0, paths.Length).Where(i => destinations[i] != paths[i]).ToArray();
        var pairs = moving.Select(i => new RenamePair(paths[i], destinations[i])).ToArray();
        RenameRules.Validate(pairs);
        var plan = Store(null, pairs, moving.Select(i => inputs[i]).ToArray());
        return new JsonObject { ["plan_id"] = plan.Id, ["action"] = "rename", ["expires"] = plan.Expires.ToString("O"), ["can_apply"] = true, ["renames"] = rows };
    }

    /// Each file's stamp, with its path spelling its stored name: new names (and the names undo
    /// restores) are built from the path as given, so not another case, not an 8.3 short name.
    private List<(string, FileStamp)> SpeltInputs(string[] paths, CancellationToken token)
    {
        var inputs = new List<(string, FileStamp)>();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            string stored;
            try { inputs.Add((path, host.Inspect(path))); stored = host.StoredName(path); }
            catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { throw new McpToolError(path + ": " + error.Message); }
            if (stored != path[(path.LastIndexOf('\\') + 1)..]) throw new McpToolError(path + ": on disk this file is named " + stored + ". Pass the path with that exact name.");
        }
        return inputs;
    }

    private JsonObject PlanMove(JsonObject arguments, CancellationToken token)
    {
        foreach (var (key, _) in arguments)
            if (key is not ("paths" or "destination")) throw new McpToolError("Unknown argument " + key + ". plan_move takes paths and destination.");
        var paths = PathsArgument(arguments);
        if (arguments["destination"] is not JsonValue d || d.GetValueKind() != JsonValueKind.String || !IsWellFormed((string)d!))
            throw new McpToolError("destination must be the absolute path of an existing folder.");
        string folder; MoveIdentity folderIdentity;
        try { folder = MoveRules.DirectoryPath((string)d!); folderIdentity = host.CheckFolder(folder); }
        catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { throw new McpToolError("destination: " + error.Message); }
        var inputs = SpeltInputs(paths, token);
        // Names are kept, so the moved file is named as the path spells it.
        var destinations = paths.Select(p => folder.TrimEnd('\\') + p[p.LastIndexOf('\\')..]).ToArray();
        var rows = new JsonArray(); bool problems = false;
        for (int i = 0; i < paths.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            string from = paths[i], to = destinations[i];
            string? issue = null;
            if (string.Equals(MoveRules.Parent(from), folder, StringComparison.OrdinalIgnoreCase)) issue = "Already in this folder.";
            else
            {
                try { WindowsPathPolicy.Validate(to); }
                catch (UserError error) { issue = error.Message; }
            }
            if (issue is null && destinations.Where((x, j) => j != i && string.Equals(x, to, StringComparison.OrdinalIgnoreCase)).Any())
                issue = "Another file in this plan has the same name; Swivel keeps names, so only one could go there.";
            if (issue is null)
            {
                // Through an 8.3 spelling of its own folder, the name a file "takes" is its own.
                try { if (host.NameTaken(to)) issue = IsItself(to, inputs[i].Item2) ? "Already in this folder." : "A file or folder with this name is already in the destination; nothing is overwritten."; }
                catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { issue = "Could not check whether this name is free: " + error.Message; }
            }
            if (issue is null)
            {
                // The same folder spelt another way (an 8.3 short name) is caught here.
                try { if (!host.CheckMove(from, folder)) issue = "Already in this folder."; }
                catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { issue = "Swivel can't move this file there: " + error.Message; }
            }
            var row = new JsonObject { ["from"] = from, ["to"] = to };
            if (issue is not null) { row["issue"] = issue; problems = true; }
            rows.Add(row);
        }
        if (problems) return new JsonObject { ["can_apply"] = false, ["destination"] = folder, ["moves"] = rows, ["next"] = "Resolve the issues (or leave those files out) and plan again." };
        var plan = Store(null, null, inputs, paths.Select((p, i) => new RenamePair(p, destinations[i])).ToArray(), folderIdentity);
        return new JsonObject { ["plan_id"] = plan.Id, ["action"] = "move", ["expires"] = plan.Expires.ToString("O"), ["can_apply"] = true, ["destination"] = folder, ["moves"] = rows };
    }

    private bool IsItself(string path, FileStamp stamp)
    {
        try { return host.Stamp(path) is { } there && there.SameObjectAndContent(stamp); }
        catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { return false; }
    }

    /// The first character that hides, disguises or reorders part of a name: controls, format
    /// and text-direction marks, line and paragraph separators, spaces other than U+0020,
    /// Unicode's default-ignorable fillers and selectors, and the braille blank.
    private static int? HiddenCharacter(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            int c = rune.Value;
            var category = System.Text.Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                || category == System.Globalization.UnicodeCategory.SpaceSeparator && c != ' '
                || c is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x2800 or 0x3164 or 0xFFA0
                || c is >= 0x180B and <= 0x180F or >= 0xFE00 and <= 0xFE0F or >= 0xFFF0 and <= 0xFFF8
                || c is >= 0x1BCA0 and <= 0x1BCA3 or >= 0x1D173 and <= 0x1D17A or >= 0xE0000 and <= 0xE0FFF)
                return c;
        }
        return null;
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
        if (plan.Renames is not null || plan.Moves is not null) return ApplyRename(plan, token);
        var action = plan.Action!;
        var outputs = new JsonArray();
        var entry = new JsonObject
        {
            ["id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), ["time"] = host.UtcNow.ToString("O"),
            ["action"] = action.Id, ["outputs"] = outputs,
        };
        string? failure = null;
        foreach (var (path, planned) in plan.Inputs)
        {
            if (token.IsCancellationRequested) { failure = "Cancelled."; break; }
            try
            {
                string output = plan.Target is long target ? host.Compress(path, target, planned, token) : host.Run(action.Id, path, token);
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
            ["entry_id"] = outputs.Count > 0 ? (string)entry["id"]! : null, ["action"] = action.Id,
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

    /// A rename or move plan: both log where each file went, under "renames" or "moves".
    private JsonObject ApplyRename(Plan plan, CancellationToken token)
    {
        bool move = plan.Moves is not null;
        var pairs = plan.Moves ?? plan.Renames!;
        string action = move ? "move" : "rename", list = move ? "moves" : "renames", done = move ? "moved" : "renamed";
        RenameOutcome outcome;
        try
        {
            var expected = plan.Inputs.Select(i => i.Stamp).ToArray();
            outcome = move ? host.Move(pairs, expected, plan.Folder, token) : host.Rename(pairs, expected, token);
        }
        catch (Exception error) when (McpToolError.Expected(error)) { throw new McpToolError((error is OperationCanceledException ? "Cancelled." : error.Message) + " Nothing was " + done + "."); }
        // Only files away from their old names are logged; a rolled-back step leaves none.
        var moved = outcome.Remaining;
        var entry = new JsonObject
        {
            ["id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), ["time"] = host.UtcNow.ToString("O"),
            ["action"] = action, ["outputs"] = new JsonArray(), // kept for older versions, which read only outputs
            [list] = new JsonArray(moved.Select(m => { var node = StampNode(m.Current, m.Stamp); node["from"] = m.Original; return (JsonNode)node; }).ToArray()),
        };
        string? unlogged = null;
        if (moved.Count > 0)
        {
            try { using (host.LockHistory()) Record(LoadHistory(), entry); }
            catch (Exception error) when (McpToolError.Expected(error)) { unlogged = error.Message; }
        }
        string? failure = outcome.Error is null ? null : outcome.Cancelled ? "Cancelled." : outcome.Error.Message;
        if (failure is not null && moved.Count == 0) throw new McpToolError(failure + (move ? " Every file was moved back; nothing was moved." : " Every name was restored; nothing was renamed."));
        var body = new JsonObject
        {
            ["entry_id"] = moved.Count > 0 && unlogged is null ? (string)entry["id"]! : null, ["action"] = action,
            [list] = new JsonArray(moved.Select(m => (JsonNode)new JsonObject { ["from"] = m.Original, ["to"] = m.Current }).ToArray()),
            ["completed"] = moved.Count(m => pairs.Any(p => p.Source == m.Original && p.Destination == m.Current)), ["total"] = pairs.Count(p => p.Changed),
        };
        if (unlogged is not null)
        {
            body["error"] = (failure is null ? "" : failure + " ") + "The files were " + done + " but could not be logged for undo (" + unlogged + "); " + (move ? "move" : "rename") + " them back yourself if that is not wanted.";
            throw new McpPartial(body);
        }
        if (failure is null) return body;
        body["error"] = failure + (move ? " Some files could not be moved back" : " Some names could not be restored") + "; where each of these files is now is listed and logged for undo.";
        throw new McpPartial(body);
    }

    private static bool Pending(JsonObject entry)
        => ((JsonArray)entry["outputs"]!).Any(o => o!["undone"]?.GetValue<bool>() != true)
        || entry["renames"] is JsonArray renames && renames.Any(r => r!["undone"]?.GetValue<bool>() != true)
        || entry["moves"] is JsonArray moves && moves.Any(m => m!["undone"]?.GetValue<bool>() != true);

    private JsonObject Undo(JsonObject arguments, CancellationToken token)
    {
        bool dryRun = arguments["dry_run"] is JsonValue d && d.GetValueKind() is JsonValueKind.True;
        string? wanted = arguments["entry_id"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? (string)v! : null;
        using var held = host.LockHistory();
        var history = LoadHistory();
        var entries = (JsonArray)history["entries"]!;
        var entry = (wanted is null
            ? entries.OfType<JsonObject>().LastOrDefault(Pending)
            : entries.OfType<JsonObject>().FirstOrDefault(e => (string?)e["id"] == wanted))
            ?? throw new McpToolError(wanted is null ? "Nothing to undo." : "Unknown entry_id. Use history.");
        if (entry["renames"] is JsonArray { Count: > 0 } renames) return UndoRename(history, entry, renames, dryRun, token);
        if (entry["moves"] is JsonArray { Count: > 0 } moves) return UndoMove(history, entry, moves, dryRun, token);
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

    /// The files of one rename go back together or not at all, so a swap or chain never ends
    /// half undone; anything changed, missing or blocked leaves every file as it is.
    private JsonObject UndoRename(JsonObject history, JsonObject entry, JsonArray renames, bool dryRun, CancellationToken token)
    {
        var results = new JsonArray();
        var rows = renames.OfType<JsonObject>().Where(r => r["undone"]?.GetValue<bool>() != true).ToArray();
        var ready = new List<(JsonObject Row, JsonObject Result)>();
        bool blocked = false, changed = false;
        foreach (var row in renames.OfType<JsonObject>())
        {
            token.ThrowIfCancellationRequested();
            string path = (string)row["path"]!, from = (string)row["from"]!;
            if (row["undone"]?.GetValue<bool>() == true) { results.Add(new JsonObject { ["path"] = path, ["from"] = from, ["result"] = "already undone" }); continue; }
            string? problem = null; bool back = false;
            try
            {
                var expected = ReadStamp(row);
                // Checked first, as in a chain or swap another file holds `path` again.
                back = Back(path, from, expected);
                var current = back ? null : host.Stamp(path);
                if (back) { }
                else if (current is null) problem = "missing; left as is";
                else if (!current.Value.SameObjectAndContent(expected)) problem = "changed since the rename; left alone";
                else if (!string.Equals(from, path, StringComparison.OrdinalIgnoreCase) && !rows.Any(r => string.Equals((string)r["path"]!, from, StringComparison.OrdinalIgnoreCase)) && host.NameTaken(from))
                    problem = "its old name is taken now; left alone";
                else if (dryRun)
                {
                    try { host.CheckRename(path); }
                    catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { problem = "would not be renamed back: " + error.Message; }
                }
            }
            catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { problem = "not checked: " + error.Message; }
            var result = new JsonObject { ["path"] = path, ["from"] = from, ["result"] = problem ?? (back ? "already back at its old name" : dryRun ? "would be renamed back" : "renamed back") };
            results.Add(result);
            if (back) { if (!dryRun) { row["undone"] = true; changed = true; } }
            else if (problem is null) ready.Add((row, result));
            else blocked = true;
        }
        JsonObject Reply(string? note = null)
        {
            var body = new JsonObject { ["entry_id"] = (string?)entry["id"], ["dry_run"] = dryRun, ["complete"] = renames.All(r => r!["undone"]?.GetValue<bool>() == true), ["results"] = results };
            if (note is not null) body["note"] = note;
            if (!changed) return body;
            try { Save(history); return body; }
            catch (Exception error) when (McpToolError.Expected(error))
            {
                body["error"] = "The names in results are as shown, but the history could not be updated (" + error.Message + "). A later undo finds files already back at their old names and marks them done.";
                throw new McpPartial(body);
            }
        }
        const string together = "The files of one rename go back together; nothing was renamed back.";
        if (blocked)
        {
            foreach (var (_, result) in ready) result["result"] = dryRun ? "would wait for the others" : "not renamed back; waiting for the others";
            return Reply(together);
        }
        if (dryRun || ready.Count == 0) return Reply();
        var pairs = ready.Select(r => new RenamePair((string)r.Row["path"]!, (string)r.Row["from"]!)).ToArray();
        RenameOutcome outcome;
        try { outcome = host.Rename(pairs, ready.Select(r => ReadStamp(r.Row)).ToArray(), token); }
        catch (Exception error) when (McpToolError.Expected(error))
        {
            foreach (var (_, result) in ready) result["result"] = "not renamed back: " + (error is OperationCanceledException ? "Cancelled." : error.Message);
            return Reply(together);
        }
        for (int i = 0; i < ready.Count; i++)
        {
            var (row, result) = ready[i];
            var location = outcome.Locations[i];
            if (location.Current == pairs[i].Destination) { row["undone"] = true; changed = true; continue; }
            // A failed step whose rollback also failed: the log follows the file.
            if (location.Current != pairs[i].Source) { row["path"] = location.Current; changed = true; }
            result["path"] = location.Current;
            result["result"] = "not renamed back: " + (outcome.Cancelled ? "Cancelled." : outcome.Error?.Message ?? "it did not move.");
        }
        return Reply(outcome.Error is null ? null : "The undo stopped; files that did not go back are where results say, and stay logged.");
    }

    /// The file is already at its old path, spelt exactly so: an earlier undo whose log could not
    /// be saved, or by hand. The same file still at `path` too (a hard link made since) is not back.
    private bool Back(string path, string from, FileStamp expected)
        => host.Stamp(from) is { } there && there.SameObjectAndContent(expected) && host.StoredName(from) == from[(from.LastIndexOf('\\') + 1)..]
            && (string.Equals(from, path, StringComparison.OrdinalIgnoreCase) || host.Stamp(path) is not { } at || !at.SameObjectAndContent(expected));

    /// Each moved file goes back on its own, like the files an action created: one that changed,
    /// is missing or whose old name is taken stays where it is, and the others still go back.
    private JsonObject UndoMove(JsonObject history, JsonObject entry, JsonArray moves, bool dryRun, CancellationToken token)
    {
        var results = new JsonArray();
        bool changed = false, cancelled = false;
        foreach (var row in moves.OfType<JsonObject>())
        {
            string path = (string)row["path"]!, from = (string)row["from"]!, result;
            if (row["undone"]?.GetValue<bool>() == true) { results.Add(new JsonObject { ["path"] = path, ["from"] = from, ["result"] = "already undone" }); continue; }
            if (cancelled || token.IsCancellationRequested) { cancelled = true; results.Add(new JsonObject { ["path"] = path, ["from"] = from, ["result"] = "not moved back: Cancelled." }); continue; }
            try
            {
                var expected = ReadStamp(row);
                if (Back(path, from, expected))
                {
                    result = "already back in its old folder";
                    if (!dryRun) { row["undone"] = true; changed = true; }
                }
                else if (host.Stamp(path) is not { } current) result = "missing; left as is";
                else if (!current.SameObjectAndContent(expected)) result = "changed since the move; left alone";
                else if (FolderProblem(MoveRules.Parent(from)) is { } gone) result = "its old folder is unavailable (" + gone + "); left alone";
                else if (host.NameTaken(from)) result = "its old name is taken now; left alone";
                else if (dryRun) result = Unmovable(path, from) is { } why ? "would not be moved back: " + why : "would be moved back";
                else
                {
                    var outcome = host.Move(new[] { new RenamePair(path, from) }, new[] { expected }, null, token);
                    string now = outcome.Locations[0].Current;
                    if (now == from) { row["undone"] = true; changed = true; result = "moved back"; }
                    else
                    {
                        // A failed step whose rollback also failed: the log follows the file.
                        if (now != path) { row["path"] = now; path = now; changed = true; }
                        if (outcome.Cancelled) cancelled = true;
                        result = "not moved back: " + (outcome.Cancelled ? "Cancelled." : outcome.Error?.Message ?? "it did not move.");
                    }
                }
            }
            catch (Exception error) when (McpToolError.Expected(error))
            {
                if (error is OperationCanceledException) cancelled = true;
                result = "not moved back: " + (error is OperationCanceledException ? "Cancelled." : error.Message);
            }
            results.Add(new JsonObject { ["path"] = path, ["from"] = from, ["result"] = result });
        }
        var body = new JsonObject { ["entry_id"] = (string?)entry["id"], ["dry_run"] = dryRun, ["complete"] = moves.All(m => m!["undone"]?.GetValue<bool>() == true), ["results"] = results };
        if (!changed) return body;
        try { Save(history); return body; }
        catch (Exception error) when (McpToolError.Expected(error))
        {
            body["error"] = "The files in results are where it says, but the history could not be updated (" + error.Message + "). A later undo finds files already back in their old folders and marks them done.";
            throw new McpPartial(body);
        }
    }

    /// Why a file can't go back into this folder (deleted, or no longer a plain local folder), or null.
    private string? FolderProblem(string folder)
    {
        try { host.CheckFolder(folder); return null; }
        catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { return error.Message; }
    }

    /// Why the move back would be refused (the same leases, opened and closed), or null.
    private string? Unmovable(string path, string from)
    {
        try { return host.CheckMove(path, MoveRules.Parent(from)) ? null : "It is already in that folder."; }
        catch (Exception error) when (McpToolError.Expected(error) && error is not OperationCanceledException) { return error.Message; }
    }

    private JsonObject History(JsonObject arguments, CancellationToken token)
    {
        int limit = arguments["limit"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out int n) ? Math.Clamp(n, 1, MaxHistory) : 20;
        var entries = ((JsonArray)LoadHistory()["entries"]!).OfType<JsonObject>().Reverse().Take(limit);
        return new JsonObject
        {
            ["entries"] = new JsonArray(entries.Select(e =>
            {
                var item = new JsonObject
                {
                    ["entry_id"] = (string?)e["id"], ["time"] = (string?)e["time"], ["action"] = (string?)e["action"],
                    ["outputs"] = new JsonArray(((JsonArray)e["outputs"]!).OfType<JsonObject>().Select(o => (JsonNode)new JsonObject { ["path"] = (string?)o["path"], ["undone"] = o["undone"]?.GetValue<bool>() == true }).ToArray()),
                };
                foreach (string list in new[] { "renames", "moves" })
                    if (e[list] is JsonArray rows)
                        item[list] = new JsonArray(rows.OfType<JsonObject>().Select(r => (JsonNode)new JsonObject { ["from"] = (string?)r["from"], ["path"] = (string?)r["path"], ["undone"] = r["undone"]?.GetValue<bool>() == true }).ToArray());
                return (JsonNode)item;
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
                if (e["renames"] is not null and not JsonArray || e["moves"] is not null and not JsonArray) throw new FormatException();
                var renames = e["renames"] as JsonArray ?? new JsonArray();
                var moves = e["moves"] as JsonArray ?? new JsonArray();
                foreach (var output in outputs.Concat(renames).Concat(moves))
                {
                    if (output is not JsonObject o || !IsString(o["path"]) || o["undone"] is not null && o["undone"]!.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException();
                    ReadStamp(o);
                }
                if (renames.Concat(moves).Any(r => !IsString(r!["from"]))) throw new FormatException();
                // Swivel writes outputs, renames or moves, never two of them in one entry; such an
                // entry could never finish undoing.
                if (renames.Count > 0 && (outputs.Count > 0 || moves.Count > 0 || (string)e["action"]! != "rename")) throw new FormatException();
                if (moves.Count > 0 && (outputs.Count > 0 || (string)e["action"]! != "move")) throw new FormatException();
                // A move keeps names and goes into one folder; it is logged there, or back where it was.
                static string Leaf(JsonNode? row, string key) { string p = (string)row![key]!; return p[(p.LastIndexOf('\\') + 1)..]; }
                static string Folder(JsonNode? row) { string p = (string)row!["path"]!; return p[..p.LastIndexOf('\\')]; }
                if (moves.Any(m => Leaf(m, "path") != Leaf(m, "from")) || moves.Select(Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) throw new FormatException();
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
