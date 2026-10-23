// Draws the browser thumbnails (large.png, small.png)
import AppKit
func draw(_ w: Int, _ h: Int, _ file: String) {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8,
        samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSColor(white: 0.12, alpha: 1).setFill(); NSRect(x: 0, y: 0, width: w, height: h).fill()
    let font = NSFont(name: "SnellRoundhand-Bold", size: CGFloat(h) * 0.32)!
    let t = NSAttributedString(string: "Your Name", attributes: [.font: font, .foregroundColor: NSColor.white])
    let sz = t.size()
    t.draw(at: NSPoint(x: (CGFloat(w) - sz.width) / 2, y: (CGFloat(h) - sz.height) / 2))
    NSGraphicsContext.restoreGraphicsState()
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: file))
}
draw(256, 144, "large.png"); draw(128, 72, "small.png")
