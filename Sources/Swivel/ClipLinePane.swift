import AppKit
import UniformTypeIdentifiers
import WebKit

/// 晾图: the screenshot clothesline page, embedded in the app window.
///
/// The page is served from `swivel-app://clipline/index.html` instead of a file URL so WebKit keeps its
/// IndexedDB (the hung screenshots) between launches.
@MainActor
final class ClipLinePane: NSViewController, WKUIDelegate, WKScriptMessageHandler, WKNavigationDelegate {
    private var web: WKWebView!
    private var ready = false
    private var pending: [[String: String]] = []

    override func loadView() {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .default()
        config.setURLSchemeHandler(ClipLineScheme(), forURLScheme: "swivel-app")
        config.userContentController.add(self, name: "liangtu")         // page events: ready, style, lamp, breeze
        config.userContentController.add(self, name: "liangtuNative")   // requests: copy an image to the pasteboard
        web = WKWebView(frame: NSRect(x: 0, y: 0, width: 1100, height: 720), configuration: config)
        web.uiDelegate = self
        web.navigationDelegate = self
        web.setValue(false, forKey: "drawsBackground")
        view = web
        preferredContentSize = NSSize(width: 1100, height: 720)
        web.load(URLRequest(url: URL(string: "swivel-app://clipline/index.html?embedded=1")!))
    }

    /// Hangs image files on the line (used by the ⌃ Actions menu).
    func hang(_ urls: [URL]) {
        let images = urls.compactMap { url -> [String: String]? in
            guard let data = try? Data(contentsOf: url) else { return nil }
            let mime = UTType(filenameExtension: url.pathExtension)?.preferredMIMEType ?? "image/png"
            return ["name": url.lastPathComponent, "dataURL": "data:\(mime);base64,\(data.base64EncodedString())"]
        }
        pending += images
        _ = view  // make sure the page is loading
        flush()
    }

    private func flush() {
        guard ready, !pending.isEmpty,
              let json = try? JSONSerialization.data(withJSONObject: pending),
              let text = String(data: json, encoding: .utf8) else { return }
        pending.removeAll()
        web.evaluateJavaScript("window.liangtu && window.liangtu.addImages(\(text))")
    }

    // MARK: Page messages

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let body = message.body as? [String: Any], let type = body["type"] as? String else { return }
        switch (message.name, type) {
        case ("liangtu", "ready"):
            ready = true
            flush()
        case ("liangtuNative", "copy"):
            guard let dataURL = body["dataURL"] as? String else { return }
            // Retina screenshots arrive as multi-megabyte base64; decode off the main thread.
            Task.detached(priority: .userInitiated) {
                guard let comma = dataURL.firstIndex(of: ","),
                      let png = Data(base64Encoded: String(dataURL[dataURL.index(after: comma)...])) else { return }
                await MainActor.run {
                    let pasteboard = NSPasteboard.general
                    pasteboard.clearContents()
                    pasteboard.setData(png, forType: .png)
                }
            }
        default:
            break
        }
    }

    // The page may have loaded before it got around to posting "ready"; ask it directly as a fallback.
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        webView.evaluateJavaScript("!!(window.liangtu && window.liangtu.addImages)") { [weak self] result, _ in
            MainActor.assumeIsolated {
                if result as? Bool == true { self?.ready = true; self?.flush() }
            }
        }
    }

    // MARK: File picker for <input type=file>

    func webView(_ webView: WKWebView, runOpenPanelWith parameters: WKOpenPanelParameters,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping ([URL]?) -> Void) {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = parameters.allowsMultipleSelection
        panel.canChooseDirectories = false
        panel.allowedContentTypes = [.image]
        panel.prompt = "Hang"
        guard let window = view.window else { return completionHandler(nil) }
        panel.beginSheetModal(for: window) { response in
            completionHandler(response == .OK ? panel.urls : nil)
        }
    }
}

/// Serves the bundled 晾图 page for `swivel-app://clipline/…`.
final class ClipLineScheme: NSObject, WKURLSchemeHandler {
    /// The bundled copy (build.sh puts it in Resources), or the source file when running from `swift build`.
    static var pageURL: URL? {
        if let bundled = Bundle.main.url(forResource: "clipline", withExtension: "html") { return bundled }
        let source = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appendingPathComponent("clipline/index.html")
        return FileManager.default.fileExists(atPath: source.path) ? source : nil
    }

    func webView(_ webView: WKWebView, start task: WKURLSchemeTask) {
        guard let url = task.request.url, let page = Self.pageURL, let data = try? Data(contentsOf: page) else {
            task.didFailWithError(URLError(.fileDoesNotExist))
            return
        }
        task.didReceive(URLResponse(url: url, mimeType: "text/html", expectedContentLength: data.count, textEncodingName: "utf-8"))
        task.didReceive(data)
        task.didFinish()
    }

    func webView(_ webView: WKWebView, stop task: WKURLSchemeTask) {}
}
