package com.drivebench.controller

/** Immutable, detached copies: the renderer never reads or mutates the control engine. */
data class RenderContact(val id: Int, val role: Role, val originX: Float, val originY: Float, val x: Float, val y: Float)
data class ControlRenderState(val controls: Controls = Controls(), val mode: DrivingMode = DrivingMode.TOUCH,
    val paused: Boolean = true, val ready: Boolean = false, val needsNeutral: Boolean = false,
    val contacts: List<RenderContact> = emptyList(), val width: Int = 0, val height: Int = 0) {
    companion object {
        fun capture(engine: ControlEngine, ready: Boolean, width: Int, height: Int): ControlRenderState =
            ControlRenderState(engine.output(),engine.mode,engine.paused,ready,engine.pedalNeedsNeutral,
                engine.contacts.map { RenderContact(it.id,it.role,it.originX,it.originY,it.x,it.y) },width,height)
    }
}

/** MotionEvent uses uptime, whereas network/sensor freshness uses elapsed realtime. */
object InputEventTime {
    fun elapsedUs(eventUptimeMs: Long, uptimeNowMs: Long, elapsedNowUs: Long): Long? {
        val ageMs=uptimeNowMs-eventUptimeMs
        if(ageMs !in 0..50) return null
        return (elapsedNowUs-(ageMs+1)*1000).takeIf { it>0 }
    }
}

/** Single input-looper owner; at most the latest position source waits for publication. */
class InputDispatchProgress {
    private var positionUs=0L
    fun position(eventUptimeMs: Long, uptimeNowMs: Long, elapsedNowUs: Long): Boolean {
        val source=InputEventTime.elapsedUs(eventUptimeMs,uptimeNowMs,elapsedNowUs) ?: return false
        positionUs=source;return true
    }
    fun sourceForPublication(nowUs: Long): Long {
        val source=if(positionUs>0) minOf(positionUs,nowUs) else nowUs
        positionUs=0
        return source
    }
}
