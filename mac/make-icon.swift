// Draws the app icon (1024x1024 PNG): silver CD, flame, music note, "MP3" badge.
import AppKit

let S: CGFloat = 1024
let img = NSImage(size: NSSize(width: S, height: S))
img.lockFocus()
let ctx = NSGraphicsContext.current!.cgContext

// Rounded-square background, dark charcoal like Nero's classic look
let bg = NSBezierPath(roundedRect: NSRect(x: 100, y: 100, width: 824, height: 824), xRadius: 185, yRadius: 185)
NSGradient(colors: [NSColor(white: 0.20, alpha: 1), NSColor(white: 0.06, alpha: 1)])!.draw(in: bg, angle: -90)

// Disc
let c = CGPoint(x: 512, y: 540), r: CGFloat = 330
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -14), blur: 30, color: NSColor.black.withAlphaComponent(0.6).cgColor)
let disc = NSBezierPath(ovalIn: NSRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r))
NSColor(white: 0.85, alpha: 1).setFill(); disc.fill()
ctx.restoreGState()
ctx.saveGState()
disc.addClip()
// rainbow sheen
let sheen = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: [
    NSColor(white: 0.95, alpha: 1).cgColor, NSColor(calibratedRed: 0.75, green: 0.85, blue: 1, alpha: 1).cgColor,
    NSColor(white: 0.70, alpha: 1).cgColor, NSColor(calibratedRed: 1, green: 0.85, blue: 0.80, alpha: 1).cgColor,
    NSColor(white: 0.92, alpha: 1).cgColor] as CFArray, locations: [0, 0.25, 0.5, 0.75, 1])!
ctx.drawLinearGradient(sheen, start: CGPoint(x: c.x - r, y: c.y + r), end: CGPoint(x: c.x + r, y: c.y - r), options: [])
ctx.restoreGState()
// hub
for (rad, w) in [(CGFloat(110), CGFloat(0.55)), (70, 0.8)] {
    NSColor(white: w, alpha: 1).setFill()
    NSBezierPath(ovalIn: NSRect(x: c.x - rad, y: c.y - rad, width: 2 * rad, height: 2 * rad)).fill()
}
NSColor(white: 0.12, alpha: 1).setFill()
NSBezierPath(ovalIn: NSRect(x: c.x - 34, y: c.y - 34, width: 68, height: 68)).fill()

// Flame rising from the lower-left of the disc
func flame(_ s: CGFloat, _ dx: CGFloat, _ colors: [NSColor]) {
    let p = NSBezierPath()
    let bx = 330 + dx, by: CGFloat = 230
    p.move(to: NSPoint(x: bx, y: by))
    p.curve(to: NSPoint(x: bx - 20 * s, y: by + 330 * s), controlPoint1: NSPoint(x: bx - 150 * s, y: by + 60 * s), controlPoint2: NSPoint(x: bx - 140 * s, y: by + 230 * s))
    p.curve(to: NSPoint(x: bx + 30 * s, y: by + 160 * s), controlPoint1: NSPoint(x: bx + 30 * s, y: by + 280 * s), controlPoint2: NSPoint(x: bx - 10 * s, y: by + 200 * s))
    p.curve(to: NSPoint(x: bx + 80 * s, y: by + 400 * s), controlPoint1: NSPoint(x: bx + 90 * s, y: by + 240 * s), controlPoint2: NSPoint(x: bx + 140 * s, y: by + 330 * s))
    p.curve(to: NSPoint(x: bx, y: by), controlPoint1: NSPoint(x: bx + 260 * s, y: by + 200 * s), controlPoint2: NSPoint(x: bx + 160 * s, y: by))
    p.close()
    NSGradient(colors: colors)!.draw(in: p, angle: 90)
}
ctx.saveGState()
ctx.setShadow(offset: .zero, blur: 40, color: NSColor.orange.withAlphaComponent(0.7).cgColor)
flame(1.0, 0, [NSColor(calibratedRed: 0.75, green: 0.02, blue: 0.02, alpha: 1), NSColor(calibratedRed: 1, green: 0.35, blue: 0.0, alpha: 1)])
ctx.restoreGState()
flame(0.6, 25, [NSColor(calibratedRed: 1, green: 0.55, blue: 0, alpha: 1), NSColor(calibratedRed: 1, green: 0.9, blue: 0.3, alpha: 1)])

// Music note (upper right)
let noteColor = NSColor(white: 0.10, alpha: 1)
noteColor.setFill()
ctx.saveGState()
ctx.translateBy(x: 640, y: 520); ctx.rotate(by: -0.35)
NSBezierPath(ovalIn: NSRect(x: -60, y: -40, width: 120, height: 80)).fill()
ctx.restoreGState()
NSBezierPath(rect: NSRect(x: 680, y: 520, width: 26, height: 230)).fill()
let flag = NSBezierPath()
flag.move(to: NSPoint(x: 680, y: 750))
flag.curve(to: NSPoint(x: 770, y: 600), controlPoint1: NSPoint(x: 760, y: 730), controlPoint2: NSPoint(x: 820, y: 680))
flag.curve(to: NSPoint(x: 706, y: 690), controlPoint1: NSPoint(x: 760, y: 650), controlPoint2: NSPoint(x: 730, y: 680))
flag.close(); flag.fill()

// "MP3" badge
let badge = NSBezierPath(roundedRect: NSRect(x: 560, y: 150, width: 300, height: 130), xRadius: 34, yRadius: 34)
NSColor(calibratedRed: 0.82, green: 0.08, blue: 0.08, alpha: 1).setFill(); badge.fill()
NSColor.white.withAlphaComponent(0.9).setStroke(); badge.lineWidth = 8; badge.stroke()
let text = NSAttributedString(string: "MP3", attributes: [.font: NSFont.systemFont(ofSize: 96, weight: .black), .foregroundColor: NSColor.white])
let ts = text.size()
text.draw(at: NSPoint(x: 710 - ts.width / 2, y: 215 - ts.height / 2))

img.unlockFocus()
let rep = NSBitmapImageRep(data: img.tiffRepresentation!)!
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
