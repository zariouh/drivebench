package com.drivebench.controller

enum class PauseCause(val message: String) {
    STOP_BUTTON("Stop button pressed"), TOUCH_CANCELLED("Android cancelled the touch gesture"),
    TOUCH_OUTSIDE("Android ended the gesture outside the controls"), FOCUS_LOST("Phone window lost focus"),
    APP_BACKGROUND("Phone app moved to the background"), SETTINGS("Control settings opened"),
    CONNECTION_MENU("Connection menu opened"), MODE_CHANGED("Steering mode changed"),
    CENTER_CHANGED("Gyro center changed"), UI_DELAY("Phone input update was delayed"),
    GYRO_DELAY("Motion sensor update was delayed"), FEEDBACK_DELAY("PC input feedback was delayed"),
    INPUT_DISABLED("Phone controls became inactive"), RECOVERY_TIMEOUT("Interruption lasted too long · tap Resume"), DISPLAY_UNAVAILABLE("Controller display became unavailable")
}

/** First cause wins until a deliberate Resume; later generic PC replies cannot erase it. */
class PauseHistory {
    private val gate=Any()
    private val history=ArrayDeque<String>()
    private var current: String?=null
    private val interruptions=ArrayDeque<String>()
    private var recoveries=0
    fun transient(message: String, nowUs: Long, detail: String="")=synchronized(gate) {
        if(interruptions.size>=12) interruptions.removeFirst()
        interruptions.addLast("${nowUs/1000} ms: ${ConnectionTrace.scrub(message).take(120)} ${ConnectionTrace.scrub(detail).take(80)}")
    }
    fun recovered(nowUs: Long)=synchronized(gate) {
        recoveries++;transient("Automatically recovered",nowUs)
    }
    val reason: String? get()=synchronized(gate) { current }
    fun resume()=synchronized(gate) { current=null }
    fun capture(message: String, nowUs: Long, detail: String=""): Boolean = synchronized(gate) {
        if(current!=null) return@synchronized false
        val safe=ConnectionTrace.scrub(message).take(120)
        current=if(detail.isEmpty()) safe else "$safe (${ConnectionTrace.scrub(detail).take(80)})"
        if(history.size==12) history.removeFirst()
        history.addLast("${nowUs.coerceAtLeast(0)/1000} ms: $current")
        true
    }
    fun report(): String=synchronized(gate) {
        buildString {
            append("Pause history (phone monotonic time):\n")
            if(history.isEmpty()) append("- No pause recorded\n")
            else history.forEach { append("- ").append(it).append('\n') }
            append("Automatic recoveries: $recoveries\nBrief interruptions (output neutral):\n")
            interruptions.forEach { append("- ").append(it).append('\n') }
        }
    }
}

/** These are the existing freshness limits, independent of rendering or socket APIs. */
object SenderHealth {
    fun interruption(snapshot: PhoneSnapshot, nowUs: Long, feedbackUs: Long): PauseCause? = when {
        !snapshot.enabled && (snapshot.disabledReason==DisabledReason.WINDOW_FOCUS || snapshot.disabledReason==DisabledReason.BACKGROUND || snapshot.disabledReason==DisabledReason.ENGINE_PAUSED) -> when(snapshot.disabledReason) {
            DisabledReason.WINDOW_FOCUS -> PauseCause.FOCUS_LOST
            DisabledReason.BACKGROUND -> PauseCause.APP_BACKGROUND
            else -> PauseCause.INPUT_DISABLED
        }
        snapshot.producedUs<=0 || nowUs-snapshot.producedUs !in 0..50_000 -> PauseCause.UI_DELAY
        snapshot.gyro && (!snapshot.sensorReady || snapshot.sensorUs<=0 || nowUs-snapshot.sensorUs !in 0..50_000) -> PauseCause.GYRO_DELAY
        !snapshot.enabled -> when(snapshot.disabledReason) {
            DisabledReason.WINDOW_FOCUS -> PauseCause.FOCUS_LOST
            DisabledReason.BACKGROUND -> PauseCause.APP_BACKGROUND
            DisabledReason.GYRO_UNCALIBRATED -> PauseCause.GYRO_DELAY
            else -> PauseCause.INPUT_DISABLED
        }
        feedbackUs>0 && nowUs-feedbackUs>150_000 -> PauseCause.FEEDBACK_DELAY
        else -> null
    }
    fun detail(cause: PauseCause, snapshot: PhoneSnapshot, nowUs: Long, feedbackUs: Long): String = when(cause) {
        PauseCause.UI_DELAY -> "update age ${(nowUs-snapshot.producedUs)/1000} ms"
        PauseCause.GYRO_DELAY -> "sensor age ${(nowUs-snapshot.sensorUs)/1000} ms"
        PauseCause.FEEDBACK_DELAY -> "feedback age ${(nowUs-feedbackUs)/1000} ms"
        else -> if(!snapshot.enabled) "disabled=${snapshot.disabledReason}" else ""
    }
}
