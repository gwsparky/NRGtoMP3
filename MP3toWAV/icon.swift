// Draws the app icon and writes AppIcon.iconset/ (run: swift icon.swift)
import AppKit

func render(_ size: CGFloat) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size),
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let s = size / 1024

    // Rounded-square background with a blue→purple gradient
    let inset = 100 * s
    let rect = NSRect(x: inset, y: inset, width: size - 2 * inset, height: size - 2 * inset)
    let bg = NSBezierPath(roundedRect: rect, xRadius: 185 * s, yRadius: 185 * s)
    NSGradient(starting: NSColor(red: 0.20, green: 0.45, blue: 0.98, alpha: 1),
               ending: NSColor(red: 0.55, green: 0.25, blue: 0.90, alpha: 1))!.draw(in: bg, angle: -90)

    // Waveform bars
    NSColor.white.setFill()
    let heights: [CGFloat] = [110, 200, 310, 240, 380, 280, 170, 240, 130]
    let barW = 44 * s, gap = 22 * s
    let total = CGFloat(heights.count) * barW + CGFloat(heights.count - 1) * gap
    var x = (size - total) / 2
    let midY = 610 * s
    for h in heights {
        let bh = h * s
        NSBezierPath(roundedRect: NSRect(x: x, y: midY - bh / 2, width: barW, height: bh),
                     xRadius: barW / 2, yRadius: barW / 2).fill()
        x += barW + gap
    }

    // "WAV" label
    let font = NSFont.systemFont(ofSize: 150 * s, weight: .heavy)
    let label = NSAttributedString(string: "WAV", attributes: [.font: font, .foregroundColor: NSColor.white])
    let ls = label.size()
    label.draw(at: NSPoint(x: (size - ls.width) / 2, y: 200 * s))

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let dir = URL(fileURLWithPath: "AppIcon.iconset")
try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
for base in [16, 32, 128, 256, 512] {
    try! render(CGFloat(base)).write(to: dir.appendingPathComponent("icon_\(base)x\(base).png"))
    try! render(CGFloat(base * 2)).write(to: dir.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
