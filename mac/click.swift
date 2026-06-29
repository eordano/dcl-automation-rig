// click.swift <x> <y> — post a REAL hardware-level left click at logical coords.
//
// Why this exists: Unity's UGUI EventSystem samples the hardware cursor, so an
// `osascript … click at {x,y}` lands on the window but is IGNORED by in-game UI
// (the red "JUMP INTO DECENTRALAND" button, every UGUI control). We post genuine
// CGEvents (move → down → up) on the HID event tap, which UGUI does see.
// osascript *keystrokes* (Cmd+P / Cmd+R) work fine — only CLICKS need this.
//
// Coords are LOGICAL points (top-left origin): screencapture-pixel / Retina-scale.
//   swift mac/click.swift 640 500
import CoreGraphics
import Foundation

let a = CommandLine.arguments
guard a.count >= 3, let x = Double(a[1]), let y = Double(a[2]) else {
    FileHandle.standardError.write("usage: click.swift <x> <y>\n".data(using: .utf8)!)
    exit(2)
}
let pt = CGPoint(x: x, y: y)
let src = CGEventSource(stateID: .hidSystemState)
func post(_ t: CGEventType) {
    CGEvent(mouseEventSource: src, mouseType: t, mouseCursorPosition: pt, mouseButton: .left)?
        .post(tap: .cghidEventTap)
}
post(.mouseMoved)
usleep(80_000)
post(.leftMouseDown)
usleep(80_000)
post(.leftMouseUp)
