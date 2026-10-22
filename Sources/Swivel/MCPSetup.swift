import AppKit

/// Settings → General → Agents: what `Swivel --mcp` offers and the lines to add to an AI app's settings.
@MainActor
final class MCPSetupView: NSStackView {
    let status = NSTextField(labelWithString: "")
    /// Where the copied setup goes. Tests use a private pasteboard.
    var pasteboard = NSPasteboard.general
    private var generation = 0

    init(width: CGFloat = 440) {
        super.init(frame: .zero)
        orientation = .vertical
        alignment = .leading
        spacing = 8
        let blurb = NSTextField(wrappingLabelWithString: L("AI apps such as Claude Desktop, Codex and Cursor can use Swivel's file tools on the files they pass. Changes are planned first, saved next to the originals and can be undone. Nothing listens on the network."))
        blurb.textColor = .secondaryLabelColor
        blurb.preferredMaxLayoutWidth = width
        let claude = NSButton(title: L("Copy Claude Desktop Setup"), target: self, action: #selector(copyClaude))
        let codex = NSButton(title: L("Copy Codex Setup"), target: self, action: #selector(copyCodex))
        let buttons = NSStackView(views: [claude, codex])
        buttons.spacing = 8
        status.font = .systemFont(ofSize: 11)
        status.textColor = .secondaryLabelColor
        status.lineBreakMode = .byWordWrapping
        status.maximumNumberOfLines = 2
        status.preferredMaxLayoutWidth = width
        status.cell?.wraps = true
        for view in [blurb, buttons, status] { addArrangedSubview(view) }
    }

    required init?(coder: NSCoder) { fatalError() }

    /// This copy of Swivel, wherever it's installed.
    nonisolated static var command: String { Bundle.main.executablePath ?? "/Applications/Swivel.app/Contents/MacOS/Swivel" }

    /// For claude_desktop_config.json (Claude → Settings → Developer → Edit Config).
    static func claudeConfig(command: String = command) -> String {
        let config: [String: Any] = ["mcpServers": ["swivel": ["command": command, "args": ["--mcp"]]]]
        let data = (try? JSONSerialization.data(withJSONObject: config, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])) ?? Data()
        return String(decoding: data, as: UTF8.self)
    }

    /// For ~/.codex/config.toml.
    static func codexConfig(command: String = command) -> String {
        let quoted = command.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        return "[mcp_servers.swivel]\ncommand = \"\(quoted)\"\nargs = [\"--mcp\"]\n"
    }

    @objc func copyClaude() {
        copy(Self.claudeConfig(), L("Copied. Add the “swivel” entry to “mcpServers” in Claude's config file, then restart Claude."))
    }

    @objc func copyCodex() {
        copy(Self.codexConfig(), L("Copied. Add it to ~/.codex/config.toml, then restart Codex."))
    }

    private func copy(_ text: String, _ message: String) {
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        status.stringValue = message
        generation += 1
        let current = generation
        DispatchQueue.main.asyncAfter(deadline: .now() + 8) { [weak self] in
            guard let self, self.generation == current else { return }
            self.status.stringValue = ""
        }
    }
}
