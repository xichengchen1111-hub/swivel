import AppKit

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    let args = CommandLine.arguments

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

    // `Swivel --demo <mode> <file>` shows the wheel at the cursor without a drag (for screenshots).
    if args.count >= 4, args[1] == "--demo" {
        let mode: WheelMode = args[2] == "tools" ? .tools : args[2] == "actions" ? .actions : .convert
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
