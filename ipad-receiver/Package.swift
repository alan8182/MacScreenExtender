// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "MacScreenReceiver-iPad",
    platforms: [
        .iOS(.v16)
    ],
    products: [
        .library(
            name: "MacScreenReceiverLib",
            targets: ["MacScreenReceiverLib"]
        )
    ],
    targets: [
        .target(
            name: "MacScreenReceiverLib",
            path: "MacScreenReceiver"
        )
    ]
)
