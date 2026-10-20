// Tries ways of showing a floating panel over another app's full-screen Space, to pick the one
// MacPanelWindowSupport uses. Starts as a regular Dock app, like Juvin.
//
//   swiftc -swift-version 5 -o /tmp/panel-harness scripts/mac/panel-harness.swift
//   /tmp/panel-harness list
//   /tmp/panel-harness <variant> [delay-seconds] [--preshow] [--focus]
//   /tmp/panel-harness all [delay-seconds] [--preshow]
//
// During the delay, make Chrome full screen and click into it. Every line also goes to
// /tmp/panel-harness.log; each run ends with one SUMMARY line. Its shown= value is not reliable over a
// full-screen Space (it read YES for variants that showed nothing), so judge each variant by a screenshot.

import AppKit
import ApplicationServices

let logPath = "/tmp/panel-harness.log"

func log(_ message: String) {
    let line = "[\(String(format: "%.3f", ProcessInfo.processInfo.systemUptime))] \(message)"
    print(line)
    fflush(stdout)
    if let data = (line + "\n").data(using: .utf8) {
        if let handle = FileHandle(forWritingAtPath: logPath) {
            handle.seekToEndOfFile()
            handle.write(data)
            handle.closeFile()
        } else {
            FileManager.default.createFile(atPath: logPath, contents: data)
        }
    }
}

func after(_ seconds: Double, _ body: @escaping @MainActor () -> Void) {
    DispatchQueue.main.asyncAfter(deadline: .now() + seconds) {
        MainActor.assumeIsolated { body() }
    }
}

// Models Avalonia's Dispatcher.UIThread.Post: the next run loop turn.
func nextTurn(_ body: @escaping @MainActor () -> Void) {
    DispatchQueue.main.async {
        MainActor.assumeIsolated { body() }
    }
}

// Polls on the main run loop so AppKit keeps processing events while waiting.
@MainActor
func waitUntil(_ what: String, timeout: Double = 1.5, _ condition: @escaping @MainActor () -> Bool, then: @escaping @MainActor () -> Void) {
    let start = Date()
    func poll() {
        if condition() {
            log("  \(what) after \(Int(Date().timeIntervalSince(start) * 1000)) ms")
            then()
        } else if Date().timeIntervalSince(start) > timeout {
            log("  \(what): TIMED OUT after \(Int(timeout * 1000)) ms, going on")
            then()
        } else {
            after(0.01, poll)
        }
    }
    poll()
}

func policyName(_ policy: NSApplication.ActivationPolicy) -> String {
    switch policy {
    case .regular: return "regular"
    case .accessory: return "accessory"
    case .prohibited: return "prohibited"
    @unknown default: return "unknown"
    }
}

@MainActor
func frontmostDescription() -> String {
    guard let app = NSWorkspace.shared.frontmostApplication else {
        return "none"
    }
    return "\(app.localizedName ?? "?")(\(app.processIdentifier))"
}

@MainActor
func isOnScreen(_ window: NSWindow) -> Bool {
    guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] else {
        return false
    }
    return list.contains { ($0[kCGWindowNumber as String] as? Int) == window.windowNumber }
}

func behaviorDescription(_ behavior: NSWindow.CollectionBehavior) -> String {
    "0x" + String(behavior.rawValue, radix: 16)
}

// MARK: - Process type

@MainActor
func setPolicy(_ policy: NSApplication.ActivationPolicy) {
    let ok = NSApp.setActivationPolicy(policy)
    log("  setActivationPolicy(\(policyName(policy))) -> \(ok); NSApp=\(policyName(NSApp.activationPolicy())) running=\(policyName(NSRunningApplication.current.activationPolicy))")
}

@MainActor
func transformProcess(toUIElement: Bool) {
    var psn = ProcessSerialNumber(highLongOfPSN: 0, lowLongOfPSN: UInt32(kCurrentProcess))
    let state = toUIElement ? kProcessTransformToUIElementApplication : kProcessTransformToForegroundApplication
    let status = TransformProcessType(&psn, ProcessApplicationTransformState(state))
    log("  TransformProcessType(\(toUIElement ? "UIElement" : "Foreground")) -> \(status)")
}

@MainActor
func waitForAccessory(then: @escaping @MainActor () -> Void) {
    waitUntil("running policy reads accessory", { NSRunningApplication.current.activationPolicy == .accessory }, then: then)
}

@MainActor
func activateSelf() {
    NSApp.activate(ignoringOtherApps: true)
    log("  NSApp.activate(ignoringOtherApps: true); isActive=\(NSApp.isActive)")
}

let panelBehavior: NSWindow.CollectionBehavior = {
    var behavior: NSWindow.CollectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
    if #available(macOS 13.0, *) {
        behavior.insert(.canJoinAllApplications)
    }
    return behavior
}()

@MainActor
func setBehavior(_ window: NSWindow) {
    let conflicting: NSWindow.CollectionBehavior = [
        .moveToActiveSpace, .managed, .fullScreenPrimary, .fullScreenNone,
        .fullScreenAllowsTiling, .fullScreenDisallowsTiling, .primary, .auxiliary,
    ]
    window.collectionBehavior = window.collectionBehavior.subtracting(conflicting).union(panelBehavior)
    log("  collectionBehavior = \(behaviorDescription(window.collectionBehavior))")
}

@MainActor
func setLevel(_ window: NSWindow, _ level: NSWindow.Level, _ name: String) {
    window.level = level
    log("  level = \(name) (\(level.rawValue))")
}

// MARK: - The panel

final class Target: NSObject {
    var clicks = 0
    @objc func apply(_ sender: Any?) {
        clicks += 1
        log("  Apply clicked (\(clicks))")
    }
}

let target = Target()
var textField: NSTextField!

@MainActor
func makeWindow(panel: Bool) -> NSWindow {
    let size = NSSize(width: 460, height: 640)
    let visible = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
    let frame = NSRect(x: visible.maxX - size.width - 16, y: visible.maxY - size.height - 16, width: size.width, height: size.height)
    var style: NSWindow.StyleMask = [.titled, .closable, .resizable]
    let window: NSWindow
    if panel {
        style.insert(.nonactivatingPanel)
        window = NSPanel(contentRect: frame, styleMask: style, backing: .buffered, defer: false)
    } else {
        window = NSWindow(contentRect: frame, styleMask: style, backing: .buffered, defer: false)
    }
    window.title = "Harness rewrite"
    window.isReleasedWhenClosed = false

    let content = NSView(frame: NSRect(origin: .zero, size: size))
    let label = NSTextField(labelWithString: "Panel harness. Type in the box, click Apply.")
    label.frame = NSRect(x: 20, y: size.height - 60, width: 420, height: 24)
    textField = NSTextField(frame: NSRect(x: 20, y: size.height - 100, width: 420, height: 28))
    textField.placeholderString = "Ask for a change"
    let button = NSButton(title: "Apply", target: target, action: #selector(Target.apply(_:)))
    button.frame = NSRect(x: 340, y: 20, width: 100, height: 32)
    content.addSubview(label)
    content.addSubview(textField)
    content.addSubview(button)
    window.contentView = content
    window.initialFirstResponder = textField
    return window
}

// Avalonia's Topmost sets the floating level; Show and Activate send makeKeyAndOrderFront and activate.
@MainActor
func avaloniaShowAndActivate(_ window: NSWindow) {
    window.level = .floating
    window.makeKeyAndOrderFront(nil)
    log("  makeKeyAndOrderFront")
    activateSelf()
}

// The rest of RewritePanelWindow.ShowPanel, run on the next dispatcher turn.
@MainActor
func avaloniaNextTurn(_ window: NSWindow) {
    window.level = .normal
    window.level = .floating
    log("  Topmost toggled (level \(window.level.rawValue))")
    setBehavior(window)
}

@MainActor
func reorderIfOffSpace(_ window: NSWindow, always: Bool) {
    let onActive = window.isOnActiveSpace
    log("  before reorder: isOnActiveSpace=\(onActive) onScreen=\(isOnScreen(window))")
    if always || !onActive {
        window.orderOut(nil)
        window.orderFrontRegardless()
        window.makeKey()
        log("  orderOut + orderFrontRegardless + makeKey")
        activateSelf()
    }
}

@MainActor
func becomePanel(_ window: NSWindow, preventActivation: Bool) {
    let before = String(describing: type(of: window))
    object_setClass(window, NSPanel.self)
    log("  object_setClass \(before) -> \(String(describing: type(of: window)))")
    window.styleMask.insert(.nonactivatingPanel)
    log("  styleMask += nonactivatingPanel (raw 0x\(String(window.styleMask.rawValue, radix: 16)))")
    if let panel = window as? NSPanel {
        panel.isFloatingPanel = true
        panel.becomesKeyOnlyIfNeeded = false
        panel.hidesOnDeactivate = false
    }
    if preventActivation {
        // Private: the window server tag that makes a panel non-activating is set at init only.
        let selector = NSSelectorFromString("_setPreventsActivation:")
        if window.responds(to: selector) {
            typealias SetBool = @convention(c) (AnyObject, Selector, Bool) -> Void
            unsafeBitCast(window.method(for: selector), to: SetBool.self)(window, selector, true)
            log("  _setPreventsActivation:YES")
        } else {
            log("  _setPreventsActivation: not available")
        }
    }
}

// MARK: - Variants

enum HideStyle {
    // Activate the remembered app if Juvin is active, hide, then restore the Dock icon (M-19).
    case returnFocusThenRegular
    case returnFocusThenForeground
    // Hide, then activate the remembered app (it never lost activation, but its window may need key back).
    case hideThenReturnFocus
}

struct Variant {
    let name: String
    let summary: String
    let nsPanelAtInit: Bool
    let hide: HideStyle
    let show: @MainActor (NSWindow, @escaping @MainActor () -> Void) -> Void
}

func levelVariant(_ name: String, _ level: NSWindow.Level, _ levelName: String) -> Variant {
    Variant(name: name, summary: "no policy change, level \(levelName), behavior, orderFrontRegardless, no activation",
            nsPanelAtInit: false, hide: .hideThenReturnFocus) { window, done in
        setLevel(window, level, levelName)
        setBehavior(window)
        window.orderFrontRegardless()
        log("  orderFrontRegardless")
        done()
    }
}

let variants: [Variant] = [
    Variant(name: "a", summary: "current Juvin: accessory, behavior, makeKeyAndOrderFront + activate, next turn Topmost toggle + behavior",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setBehavior(window)
        setPolicy(.accessory)
        avaloniaShowAndActivate(window)
        nextTurn {
            avaloniaNextTurn(window)
            done()
        }
    },
    Variant(name: "a-act", summary: "a, but the app activates before the window is shown",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setBehavior(window)
        setPolicy(.accessory)
        activateSelf()
        avaloniaShowAndActivate(window)
        nextTurn {
            avaloniaNextTurn(window)
            done()
        }
    },
    Variant(name: "a-notoggle", summary: "a, without the next-turn Topmost toggle",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setBehavior(window)
        setPolicy(.accessory)
        avaloniaShowAndActivate(window)
        nextTurn {
            setBehavior(window)
            done()
        }
    },
    Variant(name: "a-reb", summary: "a, but the behavior is set again after the policy change",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setBehavior(window)
        setPolicy(.accessory)
        setBehavior(window)
        avaloniaShowAndActivate(window)
        nextTurn {
            avaloniaNextTurn(window)
            done()
        }
    },
    Variant(name: "a-act-notoggle", summary: "a, activating before the show and without the next-turn level toggle",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setBehavior(window)
        setPolicy(.accessory)
        activateSelf()
        avaloniaShowAndActivate(window)
        nextTurn {
            setBehavior(window)
            done()
        }
    },
    Variant(name: "c-toggle", summary: "c, plus the next-turn level toggle",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setPolicy(.accessory)
        setBehavior(window)
        setLevel(window, .floating, "floating")
        activateSelf()
        window.makeKeyAndOrderFront(nil)
        nextTurn {
            avaloniaNextTurn(window)
            done()
        }
    },
    Variant(name: "a-reb-act", summary: "behavior before and after the policy change, activate, then Avalonia's show",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setBehavior(window)
        setPolicy(.accessory)
        setBehavior(window)
        activateSelf()
        avaloniaShowAndActivate(window)
        nextTurn {
            avaloniaNextTurn(window)
            done()
        }
    },
    Variant(name: "c-pre", summary: "c with the behavior set before the policy change only",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setBehavior(window)
        setPolicy(.accessory)
        setLevel(window, .floating, "floating")
        activateSelf()
        window.makeKeyAndOrderFront(nil)
        nextTurn {
            avaloniaNextTurn(window)
            done()
        }
    },
    Variant(name: "fix", summary: "accessory first, then behavior, then Avalonia's show and next turn",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setPolicy(.accessory)
        setBehavior(window)
        avaloniaShowAndActivate(window)
        nextTurn {
            avaloniaNextTurn(window)
            done()
        }
    },
    Variant(name: "b", summary: "accessory, wait for the running policy, behavior, floating, orderFrontRegardless, no activation",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setPolicy(.accessory)
        waitForAccessory {
            setBehavior(window)
            setLevel(window, .floating, "floating")
            window.orderFrontRegardless()
            log("  orderFrontRegardless")
            done()
        }
    },
    Variant(name: "c", summary: "accessory, wait for the running policy, behavior, floating, activate, makeKeyAndOrderFront",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setPolicy(.accessory)
        waitForAccessory {
            setBehavior(window)
            setLevel(window, .floating, "floating")
            activateSelf()
            window.makeKeyAndOrderFront(nil)
            log("  makeKeyAndOrderFront")
            done()
        }
    },
    levelVariant("d", .statusBar, "statusBar"),
    levelVariant("d-popup", .popUpMenu, "popUpMenu"),
    levelVariant("d-saver", .screenSaver, "screenSaver"),
    Variant(name: "e", summary: "object_setClass NSPanel, nonactivatingPanel, _setPreventsActivation, behavior, floating, orderFrontRegardless + makeKey",
            nsPanelAtInit: false, hide: .hideThenReturnFocus) { window, done in
        becomePanel(window, preventActivation: true)
        setBehavior(window)
        setLevel(window, .floating, "floating")
        window.orderFrontRegardless()
        window.makeKey()
        log("  orderFrontRegardless + makeKey")
        done()
    },
    Variant(name: "e-plain", summary: "like e without _setPreventsActivation",
            nsPanelAtInit: false, hide: .hideThenReturnFocus) { window, done in
        becomePanel(window, preventActivation: false)
        setBehavior(window)
        setLevel(window, .floating, "floating")
        window.orderFrontRegardless()
        window.makeKey()
        log("  orderFrontRegardless + makeKey")
        done()
    },
    Variant(name: "f", summary: "Electron visibleOnFullScreen: TransformProcessType UIElement, behavior, floating, then show (makeKeyAndOrderFront + activate)",
            nsPanelAtInit: false, hide: .returnFocusThenForeground) { window, done in
        transformProcess(toUIElement: true)
        setBehavior(window)
        setLevel(window, .floating, "floating")
        waitForAccessory {
            window.makeKeyAndOrderFront(nil)
            log("  makeKeyAndOrderFront")
            activateSelf()
            done()
        }
    },
    Variant(name: "g", summary: "baseline: NSPanel created nonactivating at init, behavior, floating, orderFrontRegardless + makeKey, regular app",
            nsPanelAtInit: true, hide: .hideThenReturnFocus) { window, done in
        if let panel = window as? NSPanel {
            panel.isFloatingPanel = true
            panel.becomesKeyOnlyIfNeeded = false
            panel.hidesOnDeactivate = false
        }
        setBehavior(window)
        setLevel(window, .floating, "floating")
        window.orderFrontRegardless()
        window.makeKey()
        log("  orderFrontRegardless + makeKey")
        done()
    },
    Variant(name: "juvin", summary: "the Juvin fix: a, then on the next turn orderOut + orderFrontRegardless + makeKey + activate if off the active Space",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setBehavior(window)
        setPolicy(.accessory)
        avaloniaShowAndActivate(window)
        nextTurn {
            avaloniaNextTurn(window)
            reorderIfOffSpace(window, always: false)
            done()
        }
    },
    Variant(name: "juvin-always", summary: "like juvin, but always reorders on the next turn",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setBehavior(window)
        setPolicy(.accessory)
        avaloniaShowAndActivate(window)
        nextTurn {
            avaloniaNextTurn(window)
            reorderIfOffSpace(window, always: true)
            done()
        }
    },
    Variant(name: "juvin-delay", summary: "like juvin, but the next-turn step waits for the running policy (models a delayed post)",
            nsPanelAtInit: false, hide: .returnFocusThenRegular) { window, done in
        setBehavior(window)
        setPolicy(.accessory)
        avaloniaShowAndActivate(window)
        waitForAccessory {
            after(0.1) {
                avaloniaNextTurn(window)
                reorderIfOffSpace(window, always: true)
                done()
            }
        }
    },
]

// MARK: - Run

struct State {
    let visible: Bool
    let onActiveSpace: Bool
    let onScreen: Bool
    let occlusionVisible: Bool
    let key: Bool
    let appActive: Bool
    let frontmost: String

    var shown: Bool { visible && onActiveSpace && onScreen && occlusionVisible }
}

@MainActor
func snapshot(_ window: NSWindow, _ label: String) -> State {
    let state = State(
        visible: window.isVisible,
        onActiveSpace: window.isOnActiveSpace,
        onScreen: isOnScreen(window),
        occlusionVisible: window.occlusionState.contains(.visible),
        key: window.isKeyWindow,
        appActive: NSApp.isActive,
        frontmost: frontmostDescription())
    let fieldIsFirst: Bool = textField.currentEditor() != nil && window.firstResponder === textField.currentEditor()
    var line: String = "  [\(label)] visible=\(state.visible) onActiveSpace=\(state.onActiveSpace) onScreen=\(state.onScreen) "
    line += "occlusionVisible=\(state.occlusionVisible) key=\(state.key) canBecomeKey=\(window.canBecomeKey) "
    line += "firstResponderIsField=\(fieldIsFirst) appActive=\(state.appActive) "
    line += "frontmost=\(state.frontmost) level=\(window.level.rawValue) behavior=\(behaviorDescription(window.collectionBehavior)) "
    line += "policy NSApp=\(policyName(NSApp.activationPolicy())) running=\(policyName(NSRunningApplication.current.activationPolicy))"
    log(line)
    return state
}

@MainActor
func hide(_ window: NSWindow, _ style: HideStyle, returnTo: NSRunningApplication?) {
    func returnFocus() {
        guard let app = returnTo else {
            log("  no app to return to")
            return
        }
        let ok = app.activate(options: [.activateIgnoringOtherApps])
        log("  activate \(app.localizedName ?? "?")(\(app.processIdentifier)) -> \(ok)")
    }
    switch style {
    case .returnFocusThenRegular, .returnFocusThenForeground:
        if NSApp.isActive {
            returnFocus()
        } else {
            log("  harness not active, no focus to give back")
        }
        window.orderOut(nil)
        log("  orderOut")
        if style == .returnFocusThenRegular {
            setPolicy(.regular)
        } else {
            transformProcess(toUIElement: false)
        }
    case .hideThenReturnFocus:
        window.orderOut(nil)
        log("  orderOut")
        returnFocus()
    }
}

@MainActor
func run(_ variant: Variant, delay: Double, preshow: Bool, focus: Bool) {
    log("=== variant \(variant.name): \(variant.summary)")
    log("  macOS \(ProcessInfo.processInfo.operatingSystemVersionString), pid \(getpid())")
    setPolicy(.regular)
    let window = makeWindow(panel: variant.nsPanelAtInit)

    func show() {
        let returnTo = NSWorkspace.shared.frontmostApplication.flatMap { $0.processIdentifier == getpid() ? nil : $0 }
        log("  frontmost before show: \(frontmostDescription())")
        variant.show(window) {
            if focus {
                let ok = window.makeFirstResponder(textField)
                log("  makeFirstResponder(textField) -> \(ok)")
            }
            after(0.5) {
                let early = snapshot(window, "+0.5s")
                let observe = focus ? 10.0 : 4.0
                if focus {
                    log("  FOCUS: click the text box, type a few letters, and click Apply within \(Int(observe)) s")
                } else {
                    log("  screenshot now (\(Int(observe)) s)")
                }
                after(observe) {
                    let late = snapshot(window, "+\(Int(observe) + 1)s")
                    let typed = textField.stringValue.count
                    hide(window, variant.hide, returnTo: returnTo)
                    after(0.7) {
                        let front = NSWorkspace.shared.frontmostApplication
                        let returned = returnTo != nil && front?.processIdentifier == returnTo?.processIdentifier
                        let dock = NSRunningApplication.current.activationPolicy == .regular ? "back" : "hidden"
                        log("SUMMARY variant=\(variant.name) shown=\(late.shown ? "YES" : "no") "
                            + "(early onActiveSpace=\(early.onActiveSpace) onScreen=\(early.onScreen); late onActiveSpace=\(late.onActiveSpace) onScreen=\(late.onScreen) occlusionVisible=\(late.occlusionVisible)) "
                            + "key=\(late.key) appActive=\(late.appActive) frontmostWhileShown=\(late.frontmost) "
                            + (focus ? "typedChars=\(typed) clicks=\(target.clicks) " : "")
                            + "afterHide frontmost=\(frontmostDescription()) returnedToPrevious=\(returned) dockIcon=\(dock)")
                        NSApp.terminate(nil)
                    }
                }
            }
        }
    }

    func countdown() {
        log("  make Chrome full screen and frontmost now; showing in \(Int(delay)) s")
        var left = Int(delay)
        func tick() {
            if left <= 0 {
                show()
                return
            }
            log("  \(left)...")
            left -= 1
            after(1, tick)
        }
        tick()
    }

    if preshow {
        // Models a panel that was shown and hidden before, on the desktop Space.
        log("  preshow on the current Space")
        window.makeKeyAndOrderFront(nil)
        activateSelf()
        after(1) {
            window.orderOut(nil)
            log("  preshow hidden")
            countdown()
        }
    } else {
        countdown()
    }
}

func runAll(delay: Double, extra: [String]) {
    let me = CommandLine.arguments[0]
    var first = true
    for variant in variants {
        let child = Process()
        child.executableURL = URL(fileURLWithPath: me)
        child.arguments = [variant.name, String(Int(first ? delay : 3))] + extra
        first = false
        do {
            try child.run()
            child.waitUntilExit()
        } catch {
            log("could not start \(variant.name): \(error)")
        }
    }
    log("all variants done; grep SUMMARY \(logPath)")
}

let args = Array(CommandLine.arguments.dropFirst())
let flags = args.filter { $0.hasPrefix("--") }
let positional = args.filter { !$0.hasPrefix("--") }
let delay = positional.count > 1 ? Double(positional[1]) ?? 5 : 5
let preshow = flags.contains("--preshow")
let focus = flags.contains("--focus")

guard let name = positional.first, name != "list" else {
    print("usage: panel-harness <variant>|all|list [delay-seconds] [--preshow] [--focus]")
    for variant in variants {
        print("  \(variant.name.padding(toLength: 13, withPad: " ", startingAt: 0)) \(variant.summary)")
    }
    exit(0)
}

if name == "all" {
    runAll(delay: delay, extra: flags.filter { $0 != "--focus" })
    exit(0)
}

guard let variant = variants.first(where: { $0.name == name }) else {
    print("unknown variant \(name); run panel-harness list")
    exit(2)
}

let app = NSApplication.shared
app.setActivationPolicy(.regular)
nextTurn {
    run(variant, delay: delay, preshow: preshow, focus: focus)
}
app.run()
