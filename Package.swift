// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "MacScreenExtender",
    platforms: [.macOS(.v13)],
    targets: [
        .target(
            name: "VirtualDisplayLib",
            publicHeadersPath: "include",
            linkerSettings: [.linkedFramework("CoreGraphics")]
        ),
        .executableTarget(
            name: "MacScreenSender",
            dependencies: ["VirtualDisplayLib"],
            linkerSettings: [
                .linkedFramework("CoreGraphics"),
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("CoreVideo"),
                .linkedFramework("VideoToolbox"),
                .linkedFramework("Network"),
                .linkedFramework("AppKit"),
                .linkedFramework("ServiceManagement")
            ]
        )
    ]
)
