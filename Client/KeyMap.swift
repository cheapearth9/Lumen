import GameController

/// GameController key codes -> macOS virtual key codes (ANSI layout).
enum KeyMap {
    static let table: [GCKeyCode: UInt16] = [
        .keyA: 0, .keyS: 1, .keyD: 2, .keyF: 3, .keyH: 4, .keyG: 5, .keyZ: 6, .keyX: 7,
        .keyC: 8, .keyV: 9, .keyB: 11, .keyQ: 12, .keyW: 13, .keyE: 14, .keyR: 15,
        .keyY: 16, .keyT: 17, .keyO: 31, .keyU: 32, .keyI: 34, .keyP: 35, .keyL: 37,
        .keyJ: 38, .keyK: 40, .keyN: 45, .keyM: 46,

        .one: 18, .two: 19, .three: 20, .four: 21, .five: 23, .six: 22, .seven: 26,
        .eight: 28, .nine: 25, .zero: 29,

        .equalSign: 24, .hyphen: 27, .closeBracket: 30, .openBracket: 33, .quote: 39,
        .semicolon: 41, .backslash: 42, .comma: 43, .slash: 44, .period: 47,
        .graveAccentAndTilde: 50,

        .returnOrEnter: 36, .tab: 48, .spacebar: 49, .deleteOrBackspace: 51, .escape: 53,
        .capsLock: 57,

        .leftGUI: 55, .rightGUI: 54, .leftShift: 56, .rightShift: 60,
        .leftAlt: 58, .rightAlt: 61, .leftControl: 59, .rightControl: 62,

        .F1: 122, .F2: 120, .F3: 99, .F4: 118, .F5: 96, .F6: 97, .F7: 98, .F8: 100,
        .F9: 101, .F10: 109, .F11: 103, .F12: 111,

        .leftArrow: 123, .rightArrow: 124, .downArrow: 125, .upArrow: 126,
        .home: 115, .end: 119, .pageUp: 116, .pageDown: 121, .deleteForward: 117,
        .insert: 114,

        .keypad0: 82, .keypad1: 83, .keypad2: 84, .keypad3: 85, .keypad4: 86,
        .keypad5: 87, .keypad6: 88, .keypad7: 89, .keypad8: 91, .keypad9: 92,
        .keypadPeriod: 65, .keypadAsterisk: 67, .keypadPlus: 69, .keypadSlash: 75,
        .keypadEnter: 76, .keypadHyphen: 78, .keypadEqualSign: 81
    ]
}
