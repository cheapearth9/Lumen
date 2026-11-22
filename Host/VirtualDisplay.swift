import Foundation
import CoreGraphics
import ObjectiveC

/// An extra (virtual) display for Extend mode, created through the private CGVirtualDisplay API that
/// DeskPad / BetterDisplay use. Everything goes through the Objective-C runtime so the app still builds and
/// runs (falling back to Mirror) if Apple ever changes or removes it.
final class VirtualDisplay {
    private var display: AnyObject?
    private(set) var displayID: CGDirectDisplayID = 0

    private typealias InitMode = @convention(c) (AnyObject, Selector, UInt32, UInt32, Double) -> Unmanaged<AnyObject>?

    static func create(width: Int, height: Int, refresh: Double = 60) -> VirtualDisplay? {
        guard let descClass = NSClassFromString("CGVirtualDisplayDescriptor") as? NSObject.Type,
              let settingsClass = NSClassFromString("CGVirtualDisplaySettings") as? NSObject.Type,
              let modeClass = NSClassFromString("CGVirtualDisplayMode") as? NSObject.Type,
              let displayClass = NSClassFromString("CGVirtualDisplay") as? NSObject.Type else { return nil }

        let desc = descClass.init()
        desc.setValue("Lumen Display", forKey: "name")
        desc.setValue(UInt32(width), forKey: "maxPixelsWide")
        desc.setValue(UInt32(height), forKey: "maxPixelsHigh")
        desc.setValue(NSValue(size: CGSize(width: Double(width) * 25.4 / 110.0, height: Double(height) * 25.4 / 110.0)), forKey: "sizeInMillimeters")
        desc.setValue(UInt32(0x4C4D), forKey: "vendorID")
        desc.setValue(UInt32(0x0001), forKey: "productID")
        desc.setValue(UInt32(1), forKey: "serialNum")
        desc.setValue(DispatchQueue.main, forKey: "queue")

        guard let allocated = displayClass.perform(NSSelectorFromString("alloc")),
              let created = allocated.takeUnretainedValue().perform(NSSelectorFromString("initWithDescriptor:"), with: desc) else { return nil }
        let obj = created.takeRetainedValue()

        // CGVirtualDisplayMode has a 3-argument initializer, which perform(_:with:) can't call.
        let initSel = NSSelectorFromString("initWithWidth:height:refreshRate:")
        guard let modeAlloc = modeClass.perform(NSSelectorFromString("alloc")),
              let imp = class_getMethodImplementation(modeClass, initSel) else { return nil }
        let initMode = unsafeBitCast(imp, to: InitMode.self)
        guard let mode = initMode(modeAlloc.takeUnretainedValue(), initSel, UInt32(width), UInt32(height), refresh)?.takeRetainedValue() else { return nil }

        let settings = settingsClass.init()
        settings.setValue(UInt32(0), forKey: "hiDPI")
        settings.setValue([mode], forKey: "modes")

        let applied = obj.perform(NSSelectorFromString("applySettings:"), with: settings)
        // applySettings: returns BOOL; a nil perform result or a zero pointer means failure.
        if applied == nil { return nil }
        guard let id = (obj as? NSObject)?.value(forKey: "displayID") as? UInt32, id != 0 else { return nil }

        let v = VirtualDisplay()
        v.display = obj
        v.displayID = id
        return v
    }

    func destroy() {
        display = nil     // releasing the object removes the display
        displayID = 0
    }
}
