import AppKit

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    let args = CommandLine.arguments

    // Isolated preview: no screenshot takeover or background watchers. Uses the executable's defaults domain.
    if args.count >= 3, args[1] == "--tool" {
        for feature in [Feature.textCapture, .shelf, .imagePresets, .quickActions] { Prefs.setFeatureEnabled(feature, true) }
        app.setActivationPolicy(.regular)
        app.mainMenu = AppWindow.mainMenu()
        if let appearance = ProcessInfo.processInfo.environment["SWIVEL_PREVIEW_APPEARANCE"] {
            app.appearance = NSAppearance(named: appearance == "light" ? .aqua : .darkAqua)
        }
        QuickActions.shared.syncSettings()
        NotificationCenter.default.addObserver(forName: Prefs.changed, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { QuickActions.shared.syncSettings() }
        }
        switch args[2] {
        case "text":
            if args.count >= 4 { TextCaptureWindow.present(URL(fileURLWithPath: args[3])) }
            else { AppWindow.shared.show(.textCapture) }
        case "shelf": Shelf.shared.add(args.dropFirst(3).map { URL(fileURLWithPath: $0) })
        case "quick": QuickActions.shared.show(files: args.dropFirst(3).map { URL(fileURLWithPath: $0) })
        case "presets": AppWindow.shared.show(.imagePresets)
        default: AppWindow.shared.show()
        }
        app.run()
    }

    // File-only OCR for smoke tests; does not touch the clipboard or launch screenshot watchers.
    if args.count == 3, args[1] == "--recognize" {
        Task { @MainActor in
            do { print(try await TextRecognition.recognize(URL(fileURLWithPath: args[2]))); exit(0) }
            catch { print("FAIL \(error.localizedDescription)"); exit(2) }
        }
        app.setActivationPolicy(.prohibited)
        app.run()
    }

    // `Swivel --run <convert|tools|actions> <item title> <file>...` runs one wheel item headlessly (for testing).
    if args.count >= 5, args[1] == "--run" {
        let mode: WheelMode = args[2] == "tools" ? .tools : args[2] == "actions" ? .actions : .convert
        let urls = args[4...].map { URL(fileURLWithPath: $0) }
        let items = Catalog.items(for: urls, mode: mode)
        guard let item = items.first(where: { $0.title.caseInsensitiveCompare(args[3]) == .orderedSame }) else {
            print("No item “\(args[3])”. Available: \(items.map(\.title).joined(separator: ", "))")
            exit(1)
        }
        Task { @MainActor in
            do {
                let result = try await item.run(urls)
                print("OK \(result.message)", result.files.map(\.path))
                exit(0)
            } catch {
                print("FAIL \(error.localizedDescription)")
                exit(2)
            }
        }
        app.setActivationPolicy(.prohibited)
        app.run()
    }

    // `Swivel --edit <crop|compress|edit|bg|redact|metadata|annotate|collage|trim> <file>` opens one editor directly (for testing).
    if args.count >= 4, args[1] == "--edit" {
        let url = URL(fileURLWithPath: args[3])
        app.setActivationPolicy(.accessory)
        Task { @MainActor in
            do {
                let editor: Editor = switch args[2] {
                case "compress": try CompressEditor(source: url)
                case "edit": try AdjustEditor(source: url)
                case "bg": try BackgroundEditor(source: url)
                case "redact": try RedactEditor(source: url)
                case "metadata": try MetadataEditor(source: url)
                case "annotate": try AnnotateEditor(source: url)
                case "collage": try CollageEditor(urls: args[3...].map { URL(fileURLWithPath: $0) })
                case "trim": try await TrimEditor(source: url)
                default: try CropEditor(source: url)
                }
                print("OK", try await editor.present().map(\.path))
            } catch {
                print("FAIL", error.localizedDescription)
            }
            exit(0)
        }
        app.run()
    }

    // `Swivel --demo <mode> <file> [hover index] [style]` shows the wheel at the cursor without a drag (for screenshots).
    if args.count >= 4, args[1] == "--demo" {
        let mode: WheelMode = args[2] == "tools" ? .tools : args[2] == "actions" ? .actions : .convert
        if args.count >= 6, let style = MenuStyle(rawValue: args[5]) { Prefs.style = style }
        let controller = WheelController()
        controller.keyTapped(mode, urls: [URL(fileURLWithPath: args[3])])
        if args.count >= 5, let index = Int(args[4]) { controller.demoHover(index) }
        app.setActivationPolicy(.accessory)
        DispatchQueue.main.asyncAfter(deadline: .now() + 6) { exit(0) }
        app.run()
    }

    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()
}
