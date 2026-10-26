using System.Text.Json.Nodes;

namespace Swivel.Core;

/// Text the user copies into an agent host. The command is the packaged app's execution
/// alias, or the exe path of a portable copy.
public static class McpSetup
{
    public const string Alias = "swivel-preview.exe", ServerName = "swivel-windows";

    public static string Text(string command)
    {
        string quoted = "\"" + command + "\""; // Windows paths cannot contain a double quote
        string desktop = new JsonObject
        {
            ["mcpServers"] = new JsonObject { [ServerName] = new JsonObject { ["command"] = command, ["args"] = new JsonArray("--mcp") } },
        }.ToJsonString();
        return "Claude Code:\r\nclaude mcp add --scope user " + ServerName + " -- " + quoted + " --mcp\r\n\r\n"
            + "Claude Desktop (claude_desktop_config.json, edit with Claude closed):\r\n" + desktop + "\r\n\r\n"
            + "Codex (~/.codex/config.toml):\r\n[mcp_servers." + ServerName + "]\r\ncommand = \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\r\nargs = [\"--mcp\"]\r\n";
    }
}
