import AppKit

// Generator AppIcon.icns-a. Kompajlira se zajedno s AppIcon.swift + Theme.swift (vidi
// build.sh), pa je ikona u bundle-u uvijek ista pločica koju app crta u runtimeu.
//
//   swiftc Support/IconGen/main.swift Sources/LloydsTracker/AppIcon.swift \
//          Sources/LloydsTracker/Theme.swift -o .build/icongen
//   .build/icongen <izlazni .icns>

guard CommandLine.arguments.count == 2 else {
    FileHandle.standardError.write(Data("upotreba: icongen <izlaz.icns>\n".utf8))
    exit(2)
}
let output = URL(fileURLWithPath: CommandLine.arguments[1])

// Imena koja iconutil očekuje u .iconset folderu.
let variants: [(name: String, side: Int)] = [
    ("icon_16x16", 16), ("icon_16x16@2x", 32),
    ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256),
    ("icon_256x256", 256), ("icon_256x256@2x", 512),
    ("icon_512x512", 512), ("icon_512x512@2x", 1024),
]

let iconset = URL(fileURLWithPath: NSTemporaryDirectory())
    .appendingPathComponent("LloydsTracker-\(ProcessInfo.processInfo.processIdentifier).iconset")
try? FileManager.default.removeItem(at: iconset)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

for v in variants {
    guard let data = AppIcon.png(side: v.side) else {
        FileHandle.standardError.write(Data("greška: ne mogu iscrtati \(v.name)\n".utf8))
        exit(1)
    }
    try data.write(to: iconset.appendingPathComponent("\(v.name).png"))
}

try? FileManager.default.createDirectory(
    at: output.deletingLastPathComponent(), withIntermediateDirectories: true)

let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", output.path]
try iconutil.run()
iconutil.waitUntilExit()
try? FileManager.default.removeItem(at: iconset)
exit(iconutil.terminationStatus)
