// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "Swivel",
    platforms: [.macOS(.v15)],
    targets: [
        .executableTarget(name: "Swivel", path: "Sources/Swivel", swiftSettings: [.swiftLanguageMode(.v5)]),
        .testTarget(name: "SwivelTests", dependencies: ["Swivel"], swiftSettings: [.swiftLanguageMode(.v5)])
    ]
)
