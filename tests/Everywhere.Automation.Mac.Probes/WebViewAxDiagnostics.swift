import ApplicationServices
import Foundation

@_silgen_name("_AXUIElementGetWindow")
private func AXUIElementGetWindow(_ element: AXUIElement, _ window: UnsafeMutablePointer<CGWindowID>) -> AXError

private let scalarAttributes: [CFString] = [
    kAXRoleAttribute as CFString,
    kAXSubroleAttribute as CFString,
    kAXEnabledAttribute as CFString,
    kAXFocusedAttribute as CFString,
    kAXHiddenAttribute as CFString,
    kAXSelectedAttribute as CFString,
    kAXTitleAttribute as CFString,
    kAXPositionAttribute as CFString,
    kAXSizeAttribute as CFString,
    kAXWindowAttribute as CFString,
    "AXNumberOfCharacters" as CFString,
]
private let isSummary = CommandLine.arguments.contains("--summary")

private func describe(_ value: CFTypeRef?) -> String {
    guard let value else { return "null" }
    let typeId = CFGetTypeID(value)
    let description = CFCopyDescription(value) as String? ?? "?"
    if typeId == AXValueGetTypeID() {
        return "AXValue(\(AXValueGetType(value as! AXValue).rawValue)) \(description)"
    }
    return "CFType(\(typeId)) \(description)"
}

private func inspect(_ element: AXUIElement, depth: Int, maximumDepth: Int) {
    let indent = String(repeating: "  ", count: depth)
    var processId: pid_t = 0
    let processError = AXUIElementGetPid(element, &processId)
    var windowId: CGWindowID = 0
    let windowError = AXUIElementGetWindow(element, &windowId)
    print("\(indent)element hash=\(CFHash(element)) pid=\(processId) pidError=\(processError.rawValue) window=\(windowId) windowError=\(windowError.rawValue)")

    var batch: CFArray?
    let batchError = AXUIElementCopyMultipleAttributeValues(element, scalarAttributes as CFArray, [], &batch)
    print("\(indent)batch error=\(batchError.rawValue) count=\(batch.map(CFArrayGetCount) ?? -1)")
    if isSummary {
        var roleValue: CFTypeRef?
        let roleError = AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &roleValue)
        var enclosingWindowValue: CFTypeRef?
        let enclosingWindowError = AXUIElementCopyAttributeValue(element, kAXWindowAttribute as CFString, &enclosingWindowValue)
        var enclosingWindowId: CGWindowID = 0
        let enclosingWindowIdError = enclosingWindowValue.map {
            AXUIElementGetWindow($0 as! AXUIElement, &enclosingWindowId)
        }
        print("\(indent)  summary roleError=\(roleError.rawValue) role=\(describe(roleValue)) AXWindowError=\(enclosingWindowError.rawValue) AXWindowId=\(enclosingWindowId) AXWindowIdError=\(enclosingWindowIdError?.rawValue ?? 1)")
    }
    if !isSummary, let batch {
        for index in scalarAttributes.indices where index < CFArrayGetCount(batch) {
            let pointer = CFArrayGetValueAtIndex(batch, index)
            let value = unsafeBitCast(pointer, to: CFTypeRef.self)
            print("\(indent)  batch \(scalarAttributes[index]) = \(describe(value))")
        }
    }

    if !isSummary {
        for attribute in scalarAttributes {
            var value: CFTypeRef?
            let error = AXUIElementCopyAttributeValue(element, attribute, &value)
            print("\(indent)  direct \(attribute) error=\(error.rawValue) value=\(describe(value))")
        }
    }

    guard depth < maximumDepth else { return }
    var childCount: CFIndex = -1
    let childCountError = AXUIElementGetAttributeValueCount(element, kAXChildrenAttribute as CFString, &childCount)
    var indexedChildren: CFArray?
    let indexedError = childCountError == .success && childCount > 0
        ? AXUIElementCopyAttributeValues(element, kAXChildrenAttribute as CFString, 0, min(childCount, 64), &indexedChildren)
        : childCountError
    print("\(indent)  indexed children countError=\(childCountError.rawValue) count=\(childCount) copyError=\(indexedError.rawValue) copied=\(indexedChildren.map(CFArrayGetCount) ?? -1)")

    var childrenValue: CFTypeRef?
    let childrenError = AXUIElementCopyAttributeValue(element, kAXChildrenAttribute as CFString, &childrenValue)
    guard childrenError == .success, let children = childrenValue as? [AXUIElement] else {
        print("\(indent)  children error=\(childrenError.rawValue) value=\(describe(childrenValue))")
        return
    }

    print("\(indent)  children count=\(children.count)")
    for child in children.prefix(64) {
        inspect(child, depth: depth + 1, maximumDepth: maximumDepth)
    }
}

guard CommandLine.arguments.count >= 2, let processId = pid_t(CommandLine.arguments[1]) else {
    fputs("Usage: WebViewAxDiagnostics <pid> [window-id] [maximum-depth]\n", stderr)
    exit(2)
}

let requestedWindowId = CommandLine.arguments.count >= 3 ? CGWindowID(CommandLine.arguments[2]) : nil
let maximumDepth = CommandLine.arguments.count >= 4 ? Int(CommandLine.arguments[3]) ?? 3 : 3
let application = AXUIElementCreateApplication(processId)
var windowsValue: CFTypeRef?
let windowsError = AXUIElementCopyAttributeValue(application, kAXWindowsAttribute as CFString, &windowsValue)
guard windowsError == .success, let windows = windowsValue as? [AXUIElement] else {
    fputs("AXWindows failed: \(windowsError.rawValue) \(describe(windowsValue))\n", stderr)
    exit(1)
}

for window in windows {
    var windowId: CGWindowID = 0
    let error = AXUIElementGetWindow(window, &windowId)
    if requestedWindowId == nil || (error == .success && windowId == requestedWindowId) {
        inspect(window, depth: 0, maximumDepth: maximumDepth)
    }
}
