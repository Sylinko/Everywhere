import ApplicationServices
import Foundation

// Run against the controlled WebView fixture. JSON string encoding preserves whitespace boundaries.
guard CommandLine.arguments.count == 2, let pid = pid_t(CommandLine.arguments[1]) else {
    fatalError("Usage: swift TextFragmentDiagnostics.swift <test-app-pid>")
}
let application = AXUIElementCreateApplication(pid)
AXUIElementSetMessagingTimeout(application, 1)
var remaining = 256

func read(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    var value: CFTypeRef?
    return AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success ? value : nil
}

func visit(_ element: AXUIElement, _ depth: Int) {
    guard remaining > 0, depth <= 24 else { return }
    remaining -= 1
    let record: [String: Any] = [
        "depth": depth,
        "role": read(element, kAXRoleAttribute) as? String ?? "",
        "name": read(element, kAXTitleAttribute) as? String ?? "",
        "text": read(element, kAXValueAttribute) as? String ?? "",
        "description": read(element, kAXDescriptionAttribute) as? String ?? ""
    ]
    let data = try! JSONSerialization.data(withJSONObject: record, options: [.sortedKeys, .withoutEscapingSlashes])
    print(String(decoding: data, as: UTF8.self))
    var children: CFArray?
    var count = 0
    guard AXUIElementGetAttributeValueCount(element, kAXChildrenAttribute as CFString, &count) == .success,
          count > 0,
          AXUIElementCopyAttributeValues(element, kAXChildrenAttribute as CFString, 0, min(count, 64), &children) == .success,
          let values = children as? [AXUIElement] else { return }
    for child in values { visit(child, depth + 1) }
}

visit(application, 0)
