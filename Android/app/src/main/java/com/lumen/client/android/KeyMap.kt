package com.lumen.client.android

import android.content.Context
import android.text.InputType
import android.view.KeyEvent
import android.view.View
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection

/** Android key codes -> macOS virtual key codes (ANSI layout), which is what the wire protocol uses. */
object KeyMap {
    val table: Map<Int, Int> = create()

    private fun create(): Map<Int, Int> {
        val m = HashMap<Int, Int>()
        // letters
        val letters = intArrayOf(
            KeyEvent.KEYCODE_A, 0, KeyEvent.KEYCODE_S, 1, KeyEvent.KEYCODE_D, 2, KeyEvent.KEYCODE_F, 3,
            KeyEvent.KEYCODE_H, 4, KeyEvent.KEYCODE_G, 5, KeyEvent.KEYCODE_Z, 6, KeyEvent.KEYCODE_X, 7,
            KeyEvent.KEYCODE_C, 8, KeyEvent.KEYCODE_V, 9, KeyEvent.KEYCODE_B, 11, KeyEvent.KEYCODE_Q, 12,
            KeyEvent.KEYCODE_W, 13, KeyEvent.KEYCODE_E, 14, KeyEvent.KEYCODE_R, 15, KeyEvent.KEYCODE_Y, 16,
            KeyEvent.KEYCODE_T, 17, KeyEvent.KEYCODE_O, 31, KeyEvent.KEYCODE_U, 32, KeyEvent.KEYCODE_I, 34,
            KeyEvent.KEYCODE_P, 35, KeyEvent.KEYCODE_L, 37, KeyEvent.KEYCODE_J, 38, KeyEvent.KEYCODE_K, 40,
            KeyEvent.KEYCODE_N, 45, KeyEvent.KEYCODE_M, 46,
            // digits
            KeyEvent.KEYCODE_1, 18, KeyEvent.KEYCODE_2, 19, KeyEvent.KEYCODE_3, 20, KeyEvent.KEYCODE_4, 21,
            KeyEvent.KEYCODE_5, 23, KeyEvent.KEYCODE_6, 22, KeyEvent.KEYCODE_7, 26, KeyEvent.KEYCODE_8, 28,
            KeyEvent.KEYCODE_9, 25, KeyEvent.KEYCODE_0, 29,
            // punctuation
            KeyEvent.KEYCODE_EQUALS, 24, KeyEvent.KEYCODE_MINUS, 27, KeyEvent.KEYCODE_RIGHT_BRACKET, 30,
            KeyEvent.KEYCODE_LEFT_BRACKET, 33, KeyEvent.KEYCODE_APOSTROPHE, 39, KeyEvent.KEYCODE_SEMICOLON, 41,
            KeyEvent.KEYCODE_BACKSLASH, 42, KeyEvent.KEYCODE_COMMA, 43, KeyEvent.KEYCODE_SLASH, 44,
            KeyEvent.KEYCODE_PERIOD, 47, KeyEvent.KEYCODE_GRAVE, 50,
            // editing
            KeyEvent.KEYCODE_ENTER, 36, KeyEvent.KEYCODE_TAB, 48, KeyEvent.KEYCODE_SPACE, 49,
            KeyEvent.KEYCODE_DEL, 51, KeyEvent.KEYCODE_ESCAPE, 53, KeyEvent.KEYCODE_CAPS_LOCK, 57,
            // modifiers
            KeyEvent.KEYCODE_META_LEFT, 55, KeyEvent.KEYCODE_META_RIGHT, 54,
            KeyEvent.KEYCODE_SHIFT_LEFT, 56, KeyEvent.KEYCODE_SHIFT_RIGHT, 60,
            KeyEvent.KEYCODE_ALT_LEFT, 58, KeyEvent.KEYCODE_ALT_RIGHT, 61,
            KeyEvent.KEYCODE_CTRL_LEFT, 59, KeyEvent.KEYCODE_CTRL_RIGHT, 62,
            // function keys
            KeyEvent.KEYCODE_F1, 122, KeyEvent.KEYCODE_F2, 120, KeyEvent.KEYCODE_F3, 99, KeyEvent.KEYCODE_F4, 118,
            KeyEvent.KEYCODE_F5, 96, KeyEvent.KEYCODE_F6, 97, KeyEvent.KEYCODE_F7, 98, KeyEvent.KEYCODE_F8, 100,
            KeyEvent.KEYCODE_F9, 101, KeyEvent.KEYCODE_F10, 109, KeyEvent.KEYCODE_F11, 103, KeyEvent.KEYCODE_F12, 111,
            // navigation
            KeyEvent.KEYCODE_DPAD_LEFT, 123, KeyEvent.KEYCODE_DPAD_RIGHT, 124,
            KeyEvent.KEYCODE_DPAD_DOWN, 125, KeyEvent.KEYCODE_DPAD_UP, 126,
            KeyEvent.KEYCODE_MOVE_HOME, 115, KeyEvent.KEYCODE_MOVE_END, 119,
            KeyEvent.KEYCODE_PAGE_UP, 116, KeyEvent.KEYCODE_PAGE_DOWN, 121,
            KeyEvent.KEYCODE_FORWARD_DEL, 117, KeyEvent.KEYCODE_INSERT, 114,
            // keypad
            KeyEvent.KEYCODE_NUMPAD_0, 82, KeyEvent.KEYCODE_NUMPAD_1, 83, KeyEvent.KEYCODE_NUMPAD_2, 84,
            KeyEvent.KEYCODE_NUMPAD_3, 85, KeyEvent.KEYCODE_NUMPAD_4, 86, KeyEvent.KEYCODE_NUMPAD_5, 87,
            KeyEvent.KEYCODE_NUMPAD_6, 88, KeyEvent.KEYCODE_NUMPAD_7, 89, KeyEvent.KEYCODE_NUMPAD_8, 91,
            KeyEvent.KEYCODE_NUMPAD_9, 92, KeyEvent.KEYCODE_NUMPAD_DOT, 65, KeyEvent.KEYCODE_NUMPAD_MULTIPLY, 67,
            KeyEvent.KEYCODE_NUMPAD_ADD, 69, KeyEvent.KEYCODE_NUMPAD_DIVIDE, 75, KeyEvent.KEYCODE_NUMPAD_ENTER, 76,
            KeyEvent.KEYCODE_NUMPAD_SUBTRACT, 78, KeyEvent.KEYCODE_NUMPAD_EQUALS, 81
        )
        var i = 0
        while (i + 1 < letters.size) {
            m[letters[i]] = letters[i + 1]
            i += 2
        }
        return m
    }
}

/** Character -> macOS key code (US layout), same idea as the iOS SoftKeyboardView. */
object SoftKeys {
    private val plain = HashMap<Char, Int>()
    private val shifted = HashMap<Char, Char>()

    init {
        // letters: "<letter><code>"
        for (tok in "a0 s1 d2 f3 h4 g5 z6 x7 c8 v9 b11 q12 w13 e14 r15 y16 t17 o31 u32 i34 p35 l37 j38 k40 n45 m46".split(" ")) {
            plain[tok[0]] = tok.substring(1).toInt()
        }
        val digits = intArrayOf(18, 19, 20, 21, 23, 22, 26, 28, 25, 29) // '1'..'9','0' in that order
        val digitChars = "1234567890"
        for (i in digitChars.indices) plain[digitChars[i]] = digits[i]
        plain['='] = 24; plain['-'] = 27; plain[']'] = 30; plain['['] = 33; plain['\''] = 39; plain[';'] = 41
        plain['\\'] = 42; plain[','] = 43; plain['/'] = 44; plain['.'] = 47; plain['`'] = 50
        plain[' '] = 49; plain['\n'] = 36; plain['\t'] = 48

        val from = "!@#$%^&*()_+{}|:\"<>?~"
        val to = "1234567890-=[]\\;'" + ",./`"
        for (i in from.indices) shifted[from[i]] = to[i]
    }

    /** Sends the key presses that type [ch] (shift is wrapped around when needed). Unknown characters are ignored. */
    fun type(ch: Char, send: (InputEvent) -> Unit) {
        val c = plain[ch]
        if (c != null) { tap(c, false, send); return }
        val base = shifted[ch]
        if (base != null) {
            val bc = plain[base]
            if (bc != null) { tap(bc, true, send); return }
        }
        if (ch.isUpperCase()) {
            val lc = plain[ch.lowercaseChar()]
            if (lc != null) tap(lc, true, send)
        }
    }

    fun backspace(send: (InputEvent) -> Unit) = tap(51, false, send)

    fun forwardDelete(send: (InputEvent) -> Unit) = tap(117, false, send)

    fun enter(send: (InputEvent) -> Unit) = tap(36, false, send)

    private fun tap(code: Int, shift: Boolean, send: (InputEvent) -> Unit) {
        if (shift) send(InputEvent.Key(56, true))
        send(InputEvent.Key(code, true))
        send(InputEvent.Key(code, false))
        if (shift) send(InputEvent.Key(56, false))
    }
}

/** Invisible view that brings up the on-screen keyboard and turns what you type into key presses on the computer. */
class SoftKeyboardView(context: Context) : View(context) {
    var send: ((InputEvent) -> Unit)? = null

    /** Key events coming from the keyboard (backspace, enter, arrows...). */
    var onKey: ((KeyEvent) -> Unit)? = null

    init {
        isFocusable = true
        isFocusableInTouchMode = true
    }

    override fun onCheckIsTextEditor(): Boolean = true

    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection {
        outAttrs.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS or
            InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD or InputType.TYPE_TEXT_FLAG_MULTI_LINE
        outAttrs.imeOptions = EditorInfo.IME_ACTION_NONE or EditorInfo.IME_FLAG_NO_EXTRACT_UI or EditorInfo.IME_FLAG_NO_FULLSCREEN
        return Conn(this)
    }

    private inner class Conn(v: View) : BaseInputConnection(v, false) {
        private var composing = ""

        private fun emit(e: InputEvent) { send?.invoke(e) }

        /** Replace the composing text by [text], typing only what differs. */
        private fun retype(text: String) {
            var cp = 0
            while (cp < composing.length && cp < text.length && composing[cp] == text[cp]) cp++
            for (i in 0 until (composing.length - cp)) SoftKeys.backspace { emit(it) }
            for (i in cp until text.length) SoftKeys.type(text[i]) { emit(it) }
        }

        override fun commitText(text: CharSequence?, newCursorPosition: Int): Boolean {
            retype(text?.toString() ?: "")
            composing = ""
            return true
        }

        override fun setComposingText(text: CharSequence?, newCursorPosition: Int): Boolean {
            val t = text?.toString() ?: ""
            retype(t)
            composing = t
            return true
        }

        override fun finishComposingText(): Boolean {
            composing = ""
            return true
        }

        override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
            for (i in 0 until beforeLength) SoftKeys.backspace { emit(it) }
            for (i in 0 until afterLength) SoftKeys.forwardDelete { emit(it) }
            return true
        }

        override fun sendKeyEvent(event: KeyEvent): Boolean {
            onKey?.invoke(event)
            return true
        }

        override fun performEditorAction(editorAction: Int): Boolean {
            SoftKeys.enter { emit(it) }
            return true
        }
    }
}
