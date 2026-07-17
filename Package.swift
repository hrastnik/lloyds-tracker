// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "LloydsTracker",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "LloydsTracker",
            path: "Sources/LloydsTracker",
            swiftSettings: [.swiftLanguageMode(.v5)]
        )
    ]
)
