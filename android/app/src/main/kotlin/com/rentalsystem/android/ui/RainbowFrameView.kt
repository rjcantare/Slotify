package com.rentalsystem.android.ui

import android.animation.ValueAnimator
import android.content.Context
import android.graphics.Canvas
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.SweepGradient
import android.view.View
import android.view.animation.LinearInterpolator

// Animated rainbow LED frame that mirrors the coin box's physical acrylic
// RGB-cycling frame. Drawn as a rounded-rect stroke filled with a SweepGradient
// that rotates continuously while a coin window is open. Driven by claim state
// (start() on Granted, stop() otherwise) — never a standalone timer. Matches the
// Windows lock/active overlays: same 6-colour spectrum, ~4 s continuous cycle.
class RainbowFrameView(context: Context) : View(context) {

    private val strokePx = dp(8f)
    private val cornerPx = dp(20f)

    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = strokePx
    }

    // Red -> Green -> Blue -> Purple -> Yellow -> Cyan -> Red (wrap) so the
    // sweep is seamless as the gradient rotates.
    private val colors = intArrayOf(
        0xFFFF2D2D.toInt(), 0xFF19C819.toInt(), 0xFF2F6BFF.toInt(),
        0xFFA23BD6.toInt(), 0xFFFFE000.toInt(), 0xFF1FE0E0.toInt(),
        0xFFFF2D2D.toInt()
    )

    private val rect = RectF()
    private val matrix = Matrix()
    private var gradient: SweepGradient? = null
    private var angle = 0f

    private val animator = ValueAnimator.ofFloat(0f, 360f).apply {
        duration = 4000L
        interpolator = LinearInterpolator()
        repeatCount = ValueAnimator.INFINITE
        addUpdateListener {
            angle = it.animatedValue as Float
            invalidate()
        }
    }

    init {
        visibility = GONE
        // Decorative overlay only — let touches fall through to the buttons below.
        isClickable = false
        isFocusable = false
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        gradient = SweepGradient(w / 2f, h / 2f, colors, null)
        paint.shader = gradient
    }

    override fun onDraw(canvas: Canvas) {
        val g = gradient ?: return
        val inset = strokePx / 2f
        rect.set(inset, inset, width - inset, height - inset)
        matrix.setRotate(angle, width / 2f, height / 2f)
        g.setLocalMatrix(matrix)
        canvas.drawRoundRect(rect, cornerPx, cornerPx, paint)
    }

    fun start() {
        if (visibility != VISIBLE) {
            visibility = VISIBLE
        }
        if (!animator.isStarted) {
            animator.start()
        }
    }

    fun stop() {
        if (animator.isStarted) {
            animator.cancel()
        }
        if (visibility != GONE) {
            visibility = GONE
        }
    }

    override fun onDetachedFromWindow() {
        animator.cancel()
        super.onDetachedFromWindow()
    }

    private fun dp(value: Float): Float = value * resources.displayMetrics.density
}
