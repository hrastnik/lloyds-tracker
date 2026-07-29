import AppKit

/// Ikona aplikacije — isti brand pločica kao u traci (žuti kvadrat sa crnim satom), samo u
/// proporcijama macOS ikone (tijelo ~80% platna, ostatak je prozirna margina). Koristi se
/// za `NSApp.applicationIconImage` (Dock i Cmd+Tab) i za `AppIcon.icns` u bundle-u —
/// generira ga `Support/IconGen` iz ovog istog koda, pa ne mogu razići.
enum AppIcon {
    static func image(side: CGFloat) -> NSImage {
        let image = NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            let inset = rect.width * 0.098
            let body = rect.insetBy(dx: inset, dy: inset)
            let radius = body.width * 0.2245

            let tile = NSBezierPath(roundedRect: body, xRadius: radius, yRadius: radius)
            NSColor.lloydsYellow.setFill()
            tile.fill()

            let config = NSImage.SymbolConfiguration(pointSize: body.width * 0.56, weight: .semibold)
            guard let symbol = NSImage(systemSymbolName: "clock.fill", accessibilityDescription: nil)?
                .withSymbolConfiguration(config) else { return true }
            // SF Symbols su template slike — draw() ih iscrta crno, što na žutoj pločici i želimo.
            let size = symbol.size
            let origin = NSPoint(x: rect.midX - size.width / 2, y: rect.midY - size.height / 2)
            symbol.draw(in: NSRect(origin: origin, size: size))
            return true
        }
        image.isTemplate = false
        return image
    }

    /// PNG u točnoj pixel veličini — za `.iconset` iz kojeg `iconutil` složi `.icns`.
    static func png(side: Int) -> Data? {
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: side, pixelsHigh: side,
            bitsPerSample: 8, samplesPerPixel: 4,
            hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0, bitsPerPixel: 0
        ) else { return nil }
        rep.size = NSSize(width: side, height: side)

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        image(side: CGFloat(side)).draw(in: NSRect(x: 0, y: 0, width: side, height: side))
        NSGraphicsContext.restoreGraphicsState()

        return rep.representation(using: .png, properties: [:])
    }
}
