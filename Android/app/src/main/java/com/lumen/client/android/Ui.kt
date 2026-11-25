package com.lumen.client.android

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.TextView

/** Tiny helpers for building the plain-View screens in code. */
object Ui {
    fun dp(ctx: Context, v: Int): Int = (v * ctx.resources.displayMetrics.density + 0.5f).toInt()

    @Suppress("DEPRECATION")
    fun color(ctx: Context, id: Int): Int = ctx.resources.getColor(id, ctx.theme)

    fun text(ctx: Context, s: String, sp: Float, colorId: Int, bold: Boolean = false): TextView {
        val t = TextView(ctx)
        t.text = s
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp)
        t.setTextColor(color(ctx, colorId))
        if (bold) t.setTypeface(t.typeface, Typeface.BOLD)
        return t
    }

    fun sectionTitle(ctx: Context, s: String): TextView {
        val t = text(ctx, s.uppercase(), 12f, R.color.lumen_text2, true)
        t.letterSpacing = 0.08f
        t.setPadding(dp(ctx, 4), dp(ctx, 20), 0, dp(ctx, 6))
        return t
    }

    fun cardBackground(ctx: Context, clickable: Boolean): android.graphics.drawable.Drawable {
        val gd = GradientDrawable()
        gd.setColor(color(ctx, R.color.lumen_card))
        gd.cornerRadius = dp(ctx, 14).toFloat()
        if (!clickable) return gd
        return RippleDrawable(ColorStateList.valueOf(0x33808080), gd, null)
    }

    fun card(ctx: Context): LinearLayout {
        val l = LinearLayout(ctx)
        l.orientation = LinearLayout.VERTICAL
        l.background = cardBackground(ctx, false)
        l.setPadding(dp(ctx, 16), dp(ctx, 12), dp(ctx, 16), dp(ctx, 12))
        return l
    }

    /** A tappable two-line-ish row: title on the left, detail on the right. */
    fun row(ctx: Context, title: String, detail: String): LinearLayout {
        val l = LinearLayout(ctx)
        l.orientation = LinearLayout.HORIZONTAL
        l.gravity = Gravity.CENTER_VERTICAL
        l.background = cardBackground(ctx, true)
        l.isClickable = true
        l.setPadding(dp(ctx, 16), dp(ctx, 16), dp(ctx, 16), dp(ctx, 16))
        val a = text(ctx, title, 16f, R.color.lumen_text)
        l.addView(a, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        if (detail.isNotEmpty()) l.addView(text(ctx, detail, 13f, R.color.lumen_text2))
        val lp = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        lp.bottomMargin = dp(ctx, 8)
        l.layoutParams = lp
        return l
    }

    fun spacer(ctx: Context, h: Int): View {
        val v = View(ctx)
        v.layoutParams = LinearLayout.LayoutParams(1, dp(ctx, h))
        return v
    }
}
