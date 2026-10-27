import Foundation
import Darwin

// MARK: - Browser profile caches

/// Code, graphics and shader caches that Chromium-based browsers keep inside their profiles (owner asked 2026-10-27:
/// browser storage the browser rebuilds by itself). Their web caches in ~/Library/Caches are in the ClearDisk
/// catalogue already. Only these folder names are offered, never logins, history, bookmarks, extensions or site data
/// (Service Worker, IndexedDB, Local Storage), and only the folder's contents move, as with other caches.
enum DoctorBrowserCaches {
    struct Browser: Sendable {
        let name: String
        /// Under ~/Library/Application Support.
        let root: String
        let bundleIDs: [String]
    }

    static let browsers: [Browser] = [
        Browser(name: "Google Chrome", root: "Google/Chrome", bundleIDs: ["com.google.Chrome"]),
        Browser(name: "Google Chrome Beta", root: "Google/Chrome Beta", bundleIDs: ["com.google.Chrome.beta"]),
        Browser(name: "Google Chrome Canary", root: "Google/Chrome Canary", bundleIDs: ["com.google.Chrome.canary"]),
        Browser(name: "Chromium", root: "Chromium", bundleIDs: ["org.chromium.Chromium"]),
        Browser(name: "Microsoft Edge", root: "Microsoft Edge", bundleIDs: ["com.microsoft.edgemac"]),
        Browser(name: "Brave", root: "BraveSoftware/Brave-Browser", bundleIDs: ["com.brave.Browser"]),
        Browser(name: "Arc", root: "Arc/User Data", bundleIDs: ["company.thebrowser.Browser"]),
        Browser(name: "Vivaldi", root: "Vivaldi", bundleIDs: ["com.vivaldi.Vivaldi"]),
        Browser(name: "Opera", root: "com.operasoftware.Opera", bundleIDs: ["com.operasoftware.Opera"]),
        Browser(name: "Opera GX", root: "com.operasoftware.OperaGX", bundleIDs: ["com.operasoftware.OperaGX"]),
    ]

    /// Caches next to the profiles, shared by all of them.
    static let sharedCaches = ["GrShaderCache", "GraphiteDawnCache", "ShaderCache", "GPUPersistentCache"]
    /// Caches inside each profile.
    static let profileCaches = ["Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache"]

    /// "Default", "Profile 3", "Guest Profile", "System Profile".
    static func isProfile(_ name: String) -> Bool {
        if ["Default", "Guest Profile", "System Profile"].contains(name) { return true }
        guard name.hasPrefix("Profile "), let number = Int(name.dropFirst(8)) else { return false }
        return number > 0 && name == "Profile \(number)"
    }

    struct Found: Sendable {
        let browser: Browser
        /// "~/Library/Application Support/…/Default/GPUCache".
        let path: String
        let profile: String?
        let folder: String
    }

    /// Every cache folder that exists, as plain folders (links are never followed). Opera keeps its profile in the
    /// root itself, so profile caches are looked for there too. Reads folder names only.
    static func find(home: URL) -> [Found] {
        let base = ((try? DoctorTrash.realHome(home)) ?? home).appendingPathComponent("Library/Application Support")
        var found: [Found] = []
        func isFolder(_ url: URL) -> Bool {
            var info = stat()
            return lstat(url.path, &info) == 0 && info.st_mode & S_IFMT == S_IFDIR
        }
        for browser in browsers {
            let root = base.appendingPathComponent(browser.root)
            guard isFolder(root) else { continue }
            let prefix = "~/Library/Application Support/" + browser.root
            for name in sharedCaches + profileCaches where isFolder(root.appendingPathComponent(name)) {
                found.append(Found(browser: browser, path: prefix + "/" + name, profile: nil, folder: name))
            }
            let profiles = ((try? FileManager.default.contentsOfDirectory(atPath: root.path)) ?? []).filter(isProfile).sorted()
            for profile in profiles where isFolder(root.appendingPathComponent(profile)) {
                for name in profileCaches where isFolder(root.appendingPathComponent(profile).appendingPathComponent(name)) {
                    found.append(Found(browser: browser, path: prefix + "/" + profile + "/" + name, profile: profile, folder: name))
                }
            }
        }
        return found
    }

    /// True only for one of these exact folders, moved by contents.
    static func isAllowed(_ target: DoctorTrashTarget) -> Bool {
        guard target.mode == .contents, target.origin == .standard, !target.path.contains("/../") else { return false }
        for browser in browsers {
            let prefix = "~/Library/Application Support/" + browser.root + "/"
            guard target.path.hasPrefix(prefix) else { continue }
            let parts = target.path.dropFirst(prefix.count).split(separator: "/", omittingEmptySubsequences: false).map(String.init)
            switch parts.count {
            case 1: return sharedCaches.contains(parts[0]) || profileCaches.contains(parts[0])
            case 2: return isProfile(parts[0]) && profileCaches.contains(parts[1])
            default: return false
            }
        }
        return false
    }

    /// The owning browser's bundle identifiers, for a path `find` returned.
    static func owners(of path: String) -> [String] {
        browsers.first { path.hasPrefix("~/Library/Application Support/" + $0.root + "/") }?.bundleIDs ?? []
    }

    /// "Code cache", "Graphics cache"… for a folder name.
    static func kind(_ folder: String) -> String {
        switch folder {
        case "Code Cache": "Code cache"
        case "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GPUPersistentCache": "Graphics cache"
        default: "Shader cache"
        }
    }
}
