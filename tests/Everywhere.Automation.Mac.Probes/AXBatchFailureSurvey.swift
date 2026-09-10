import AppKit
import ApplicationServices
import Foundation

private let attributes: [CFString] = [
    kAXRoleAttribute as CFString,
    kAXSubroleAttribute as CFString,
    kAXEnabledAttribute as CFString,
    kAXFocusedAttribute as CFString,
    kAXHiddenAttribute as CFString,
    kAXSelectedAttribute as CFString,
    kAXTitleAttribute as CFString,
    kAXPositionAttribute as CFString,
    kAXSizeAttribute as CFString,
]
private let attributeNames = attributes.map { $0 as String }
private let maximumElementsPerProcess = 1_000
private let maximumChildrenPerElement = 256
private var counts: [String: Int] = [:]
private var examples: [String] = []

private func increment(_ key: String) {
    counts[key, default: 0] += 1
}

private func readError(_ value: CFTypeRef) -> AXError? {
    guard CFGetTypeID(value) == AXValueGetTypeID(), AXValueGetType(value as! AXValue) == .axError else { return nil }
    var error = AXError.success
    return AXValueGetValue(value as! AXValue, .axError, &error) ? error : .failure
}

private func isExpectedType(_ value: CFTypeRef, at index: Int) -> Bool {
    let type = CFGetTypeID(value)
    switch index {
    case 0, 1, 6:
        return type == CFStringGetTypeID()
    case 2, 3, 4, 5:
        return type == CFBooleanGetTypeID()
    case 7:
        return type == AXValueGetTypeID() && AXValueGetType(value as! AXValue) == .cgPoint
    case 8:
        return type == AXValueGetTypeID() && AXValueGetType(value as! AXValue) == .cgSize
    default:
        return false
    }
}

private func isUnsupported(_ error: AXError) -> Bool {
    error == .attributeUnsupported || error == .noValue || error == .parameterizedAttributeUnsupported || error == .notImplemented
}

private func capabilitySummary(_ element: AXUIElement) -> String {
    var attributeValues: CFArray?
    let attributeError = AXUIElementCopyAttributeNames(element, &attributeValues)
    let attributeNames = attributeValues as? [String] ?? []
    var parameterizedValues: CFArray?
    let parameterizedError = AXUIElementCopyParameterizedAttributeNames(element, &parameterizedValues)
    let parameterizedNames = parameterizedValues as? [String] ?? []
    let hasValue = attributeNames.contains(kAXValueAttribute)
    let hasCharacterCount = attributeNames.contains("AXNumberOfCharacters")
    let hasStringForRange = parameterizedNames.contains("AXStringForRange")
    return "advertised(attributes:\(attributeError.rawValue),value:\(hasValue),count:\(hasCharacterCount);parameterized:\(parameterizedError.rawValue),range:\(hasStringForRange))"
}

private func surveyText(_ element: AXUIElement, applicationName: String, processId: pid_t, role: String) {
    var values: CFArray?
    let countAttribute = ["AXNumberOfCharacters" as CFString] as CFArray
    let batchError = AXUIElementCopyMultipleAttributeValues(element, countAttribute, [], &values)
    if batchError != .success {
        let category = batchError == .invalidUIElement ? "unavailable" : isUnsupported(batchError) ? "unsupported" : "provider"
        increment("text-count-batch:\(category):\(batchError.rawValue)")
        return
    }

    guard let slots = values as? [CFTypeRef], let slot = slots.first else {
        increment("text-count:provider:missing-slot")
        return
    }
    if let error = readError(slot) {
        let category = error == .invalidUIElement ? "unavailable" : isUnsupported(error) ? "fallback" : "provider"
        increment("text-count:\(category):\(error.rawValue)")
        if category == "provider", examples.count < 80 {
            examples.append("\(applicationName) pid=\(processId) role=\(role) AXNumberOfCharacters provider:\(error.rawValue)")
        }
        if category != "fallback" { return }

        var value: CFTypeRef?
        let valueError = AXUIElementCopyAttributeValue(element, kAXValueAttribute as CFString, &value)
        let valueCategory = valueError == .success ? "success" : valueError == .invalidUIElement ? "unavailable" : isUnsupported(valueError) ? "unsupported" : "provider"
        increment("text-value:\(valueCategory):\(valueError.rawValue)")
        if valueCategory == "provider", examples.count < 80 {
            examples.append("\(applicationName) pid=\(processId) role=\(role) title=\(copyString(element, kAXTitleAttribute as CFString)) AXValue provider:\(valueError.rawValue) \(capabilitySummary(element))")
        }
        return
    }

    if CFGetTypeID(slot) == CFNumberGetTypeID() {
        var characterCount: Int64 = 0
        guard CFNumberGetValue((slot as! CFNumber), .sInt64Type, &characterCount), characterCount >= 0 else {
            increment("text-count:provider:invalid-value")
            if examples.count < 80 {
                examples.append("\(applicationName) pid=\(processId) role=\(role) AXNumberOfCharacters invalid value")
            }
            return
        }

        increment("text-count:success")
        guard characterCount > 0 else {
            increment("text-range:success:empty")
            return
        }

        var range = CFRange(location: 0, length: min(Int(characterCount), 257))
        guard let parameter = AXValueCreate(.cfRange, &range) else {
            increment("text-range:provider:create-parameter")
            return
        }

        var rangedValue: CFTypeRef?
        let rangeError = AXUIElementCopyParameterizedAttributeValue(
            element,
            "AXStringForRange" as CFString,
            parameter,
            &rangedValue)
        let rangeCategory = rangeError == .success ? "success" :
            rangeError == .invalidUIElement ? "unavailable" :
            isUnsupported(rangeError) || rangeError == .illegalArgument ? "fallback" : "provider"
        increment("text-range:\(rangeCategory):\(rangeError.rawValue)")
        if rangeError == .success {
            if let text = rangedValue as? String, !text.isEmpty {
                increment("text-range-result:success")
            } else {
                increment("text-range-result:provider:invalid-value")
                if examples.count < 80 {
                    let resultType = rangedValue.map { CFCopyTypeIDDescription(CFGetTypeID($0)) as String? ?? "unknown" } ?? "nil"
                    let resultLength: CFIndex? = rangedValue.flatMap { value -> CFIndex? in
                        guard CFGetTypeID(value) == CFStringGetTypeID() else { return nil }
                        return CFStringGetLength(unsafeBitCast(value, to: CFString.self))
                    }
                    var fallbackValue: CFTypeRef?
                    let fallbackError = AXUIElementCopyAttributeValue(element, kAXValueAttribute as CFString, &fallbackValue)
                    let fallbackType = fallbackValue.map { CFCopyTypeIDDescription(CFGetTypeID($0)) as String? ?? "unknown" } ?? "nil"
                    let fallbackLength: CFIndex? = fallbackValue.flatMap { value -> CFIndex? in
                        guard CFGetTypeID(value) == CFStringGetTypeID() else { return nil }
                        return CFStringGetLength(unsafeBitCast(value, to: CFString.self))
                    }
                    let resultLengthText = resultLength.map(String.init) ?? "nil"
                    let fallbackLengthText = fallbackLength.map(String.init) ?? "nil"
                    examples.append("\(applicationName) pid=\(processId) role=\(role) title=\(copyString(element, kAXTitleAttribute as CFString)) description=\(copyString(element, kAXDescriptionAttribute as CFString)) AXNumberOfCharacters=\(characterCount) AXStringForRange type=\(resultType) length=\(resultLengthText) AXValue error=\(fallbackError.rawValue) type=\(fallbackType) length=\(fallbackLengthText)")
                }
            }
        } else if rangeCategory == "provider", examples.count < 80 {
            examples.append("\(applicationName) pid=\(processId) role=\(role) AXStringForRange provider:\(rangeError.rawValue)")
        }
    } else {
        let key = "text-count:provider:type-mismatch:\(CFCopyTypeIDDescription(CFGetTypeID(slot)) as String? ?? "unknown")"
        increment(key)
        if examples.count < 80 {
            examples.append("\(applicationName) pid=\(processId) role=\(role) \(key)")
        }
    }
}

private func copyString(_ element: AXUIElement, _ attribute: CFString) -> String {
    var value: CFTypeRef?
    guard AXUIElementCopyAttributeValue(element, attribute, &value) == .success else { return "" }
    return value as? String ?? ""
}

private func survey(processId: pid_t, applicationName: String) {
    let application = AXUIElementCreateApplication(processId)
    AXUIElementSetMessagingTimeout(application, 1)
    var windowsValue: CFTypeRef?
    let windowsError = AXUIElementCopyAttributeValue(application, kAXWindowsAttribute as CFString, &windowsValue)
    guard windowsError == .success, let windows = windowsValue as? [AXUIElement] else {
        increment("application-windows:\(windowsError.rawValue)")
        return
    }

    var queue = windows
    var nextIndex = 0
    while nextIndex < queue.count && nextIndex < maximumElementsPerProcess {
        let element = queue[nextIndex]
        nextIndex += 1
        var values: CFArray?
        let error = AXUIElementCopyMultipleAttributeValues(element, attributes as CFArray, [], &values)
        var observedRole = ""
        if error != .success {
            let key = "batch:\(error.rawValue)"
            increment(key)
            if examples.count < 80 {
                examples.append("\(applicationName) pid=\(processId) role=\(copyString(element, kAXRoleAttribute as CFString)) \(key)")
            }
        } else if let slots = values as? [CFTypeRef] {
            let role = slots.isEmpty ? "" : slots[0] as? String ?? ""
            observedRole = role
            for index in attributes.indices {
                guard index < slots.count else {
                    increment("\(attributeNames[index]):missing-slot")
                    continue
                }
                let slot = slots[index]
                if let slotError = readError(slot) {
                    if isUnsupported(slotError) {
                        increment("\(attributeNames[index]):unsupported:\(slotError.rawValue)")
                    } else {
                        let key = "\(attributeNames[index]):provider:\(slotError.rawValue)"
                        increment(key)
                        if examples.count < 80 {
                            examples.append("\(applicationName) pid=\(processId) role=\(role) title=\(copyString(element, kAXTitleAttribute as CFString)) \(key) \(capabilitySummary(element))")
                        }
                    }
                } else if !isExpectedType(slot, at: index) {
                    let key = "\(attributeNames[index]):type-mismatch:\(CFCopyTypeIDDescription(CFGetTypeID(slot)) as String? ?? "unknown")"
                    increment(key)
                    if examples.count < 80 {
                        examples.append("\(applicationName) pid=\(processId) role=\(role) \(key)")
                    }
                } else {
                    increment("\(attributeNames[index]):success")
                }
            }
        }

        surveyText(element, applicationName: applicationName, processId: processId, role: observedRole)

        var childCount = 0
        if AXUIElementGetAttributeValueCount(element, kAXChildrenAttribute as CFString, &childCount) == .success && childCount > 0 {
            var children: CFArray?
            let length = min(childCount, maximumChildrenPerElement)
            if AXUIElementCopyAttributeValues(element, kAXChildrenAttribute as CFString, 0, length, &children) == .success,
               let childElements = children as? [AXUIElement] {
                queue.append(contentsOf: childElements)
            }
        }
    }
    increment("elements-surveyed:\(applicationName):\(min(nextIndex, maximumElementsPerProcess))")
}

let windowInfo = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], .zero) as? [[String: Any]] ?? []
var applications: [pid_t: String] = [:]
for window in windowInfo {
    guard let processNumber = window[kCGWindowOwnerPID as String] as? NSNumber else { continue }
    let processId = pid_t(processNumber.int32Value)
    let name = window[kCGWindowOwnerName as String] as? String ?? "unknown"
    applications[processId] = name
}

for (processId, name) in applications.sorted(by: { $0.value < $1.value }) {
    survey(processId: processId, applicationName: name)
}

for (key, count) in counts.sorted(by: { $0.key < $1.key }) {
    print("COUNT\t\(count)\t\(key)")
}
for example in examples {
    print("EXAMPLE\t\(example)")
}
