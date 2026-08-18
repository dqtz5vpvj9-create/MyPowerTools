// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "InputMonitor",
    platforms: [
        .macOS(.v13)
    ],
    targets: [
        .executableTarget(
            name: "InputMonitor",
            path: "Sources/InputMonitor",
            linkerSettings: [
                .linkedLibrary("sqlite3")
            ]
        )
    ]
)
