// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "VNEGifMaker",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "VNEGifMaker",
            path: "Sources/VNEGifMaker",
            swiftSettings: [.unsafeFlags(["-Osize"])]
        )
    ],
    swiftLanguageVersions: [.v5]
)
