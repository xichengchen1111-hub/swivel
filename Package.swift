// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "Swivel",
    platforms: [.macOS(.v15)],
    targets: [
        // Vendored MIT data for Mac Doctor only; see ThirdParty/ClearDisk/UPSTREAM.md.
        .target(name: "DoctorCatalog", path: "ThirdParty/ClearDisk", exclude: ["LICENSE", "UPSTREAM.md"],
                swiftSettings: [.swiftLanguageMode(.v5)]),
        .executableTarget(name: "Swivel", dependencies: ["DoctorCatalog"], path: "Sources/Swivel",
                          swiftSettings: [.swiftLanguageMode(.v5)]),
        .testTarget(name: "SwivelTests", dependencies: ["Swivel", "DoctorCatalog"], swiftSettings: [.swiftLanguageMode(.v5)])
    ]
)
