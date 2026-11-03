package com.drivebench.controller

import kotlin.math.*

enum class DrivingMode { TOUCH, GYRO }
enum class Role { STEER, PEDALS, BRAKE, THROTTLE }
data class Contact(val id: Int, val role: Role, val originX: Float, val originY: Float, var x: Float, var y: Float)
data class Controls(val steering: Float = 0f, val brake: Float = 0f, val throttle: Float = 0f)

/** Input rules independent of Android, so pointer ownership and release behavior can be verified. */
class ControlEngine {
    var mode = DrivingMode.TOUCH
        private set
    var paused = false
        private set
    var travel = 76f
        set(value) { field = if(value.isFinite()) value.coerceIn(40f,120f) else 76f }
    var gyroSteering = 0f
    var gyroReady = false
    var pedalNeedsNeutral = false
        private set
    private var gyroThrottleNeedsNeutral = false
    private val active = linkedMapOf<Int, Contact>()
    val contacts: List<Contact> get() = active.values.toList()

    fun setMode(value: DrivingMode) { releaseAll(); gyroSteering = 0f; gyroReady = false; mode = value }
    fun setPaused(value: Boolean) { releaseAll(); gyroSteering = 0f; paused = value }
    fun togglePaused() { setPaused(!paused) }
    fun releaseAll() { active.clear(); pedalNeedsNeutral = false; gyroThrottleNeedsNeutral = false }
    fun down(id: Int, role: Role, x: Float, y: Float): Boolean {
        if (paused || !x.isFinite() || !y.isFinite() || id in active || active.values.any { it.role == role }) return false
        val valid = if (mode == DrivingMode.TOUCH) role == Role.STEER || role == Role.PEDALS
            else role == Role.BRAKE || role == Role.THROTTLE
        if (!valid) return false
        active[id] = Contact(id, role, x, y, x, y)
        return true
    }
    fun move(id: Int, x: Float, y: Float) {
        val point = active[id] ?: return
        if (!x.isFinite() || !y.isFinite()) { up(id); return }
        point.x = x; point.y = y
    }
    fun up(id: Int) {
        when (active.remove(id)?.role) {
            Role.PEDALS -> pedalNeedsNeutral = false
            Role.THROTTLE -> gyroThrottleNeedsNeutral = false
            else -> Unit
        }
    }
    fun output(): Controls {
        if (paused || (mode == DrivingMode.GYRO && !gyroReady)) return Controls()
        fun value(role: Role) = active.values.firstOrNull { it.role == role }
        val distance = travel.coerceIn(40f, 140f)
        val neutral = 12f
        fun amount(displacement: Float) = ((displacement - neutral) / (distance - neutral)).coerceIn(0f, 1f).pow(1.15f)
        if (mode == DrivingMode.TOUCH) {
            val steering = value(Role.STEER)?.let { ((it.x - it.originX) / distance).coerceIn(-1f, 1f) } ?: 0f
            val pedal = value(Role.PEDALS) ?: return Controls(steering)
            val inward = pedal.originX - pedal.x
            val upward = pedal.originY - pedal.y
            if (abs(inward) <= neutral && abs(upward) <= neutral) pedalNeedsNeutral = false
            if (inward > 18f && inward >= max(0f, upward) * .65f) {
                pedalNeedsNeutral = true
                return Controls(steering, amount(inward), 0f)
            }
            return Controls(steering, 0f, if (pedalNeedsNeutral) 0f else amount(upward))
        }
        val brake = value(Role.BRAKE)?.let { amount(it.originY - it.y) } ?: 0f
        val rawThrottle = value(Role.THROTTLE)?.let { amount(it.originY - it.y) } ?: 0f
        if (rawThrottle <= .01f) gyroThrottleNeedsNeutral = false
        if (brake > .02f && rawThrottle > .01f) gyroThrottleNeedsNeutral = true
        val throttle = if (brake > .02f || gyroThrottleNeedsNeutral) 0f else rawThrottle
        return Controls(gyroSteering.takeIf { it.isFinite() }?.coerceIn(-1f, 1f) ?: 0f, brake, throttle)
    }
    fun throttleNeedsNeutral(): Boolean = pedalNeedsNeutral || gyroThrottleNeedsNeutral
}

data class Quaternion(val x: Double, val y: Double, val z: Double, val w: Double) {
    fun normalized(): Quaternion? {
        val length = sqrt(x*x + y*y + z*z + w*w)
        if (!length.isFinite() || length < .00001) return null
        return Quaternion(x/length, y/length, z/length, w/length)
    }
    fun relativeTo(center: Quaternion): Quaternion = Quaternion(
        center.w*x - center.x*w - center.y*z + center.z*y,
        center.w*y + center.x*z - center.y*w - center.z*x,
        center.w*z - center.x*y + center.y*x - center.z*w,
        center.w*w + center.x*x + center.y*y + center.z*z)
    fun screenTwistDegrees(): Double? {
        if (hypot(z, w) < .15) return null
        var result = Math.toDegrees(2 * atan2(z, w))
        while (result > 180) result -= 360
        while (result < -180) result += 360
        return result
    }
}

data class RotationSample(val rotation: Quaternion, val timestamp: Long)
class GyroSteering {
    var fullTurnDegrees = 30.0
        set(value) { field = if(value.isFinite()) value.coerceIn(15.0,60.0) else 30.0 }
    var inverted = false
    var calibrated = false
        private set
    private var center: Quaternion? = null
    private var filtered = 0.0
    private var lastFrame = 0L
    fun reset() { center = null; calibrated = false; filtered = 0.0; lastFrame = 0 }
    fun center(sample: RotationSample?, now: Long): Boolean {
        reset()
        if (sample == null || now - sample.timestamp !in 0..100_000_000L) return false
        center = sample.rotation.normalized() ?: return false
        calibrated = true; return true
    }
    fun sample(sample: RotationSample?, now: Long): Float {
        if (!calibrated) return 0f
        if (sample == null || now - sample.timestamp !in 0..100_000_000L) { reset(); return 0f }
        val q = sample.rotation.normalized() ?: run { reset(); return 0f }
        val twist = q.relativeTo(center!!).screenTwistDegrees() ?: run { reset(); return 0f }
        val direction = if (inverted) 1.0 else -1.0
        val deadZone = 1.2
        val normalized = (max(0.0, abs(twist)-deadZone) / (fullTurnDegrees.coerceIn(15.0, 60.0)-deadZone)).coerceIn(0.0, 1.0)
        val target = direction * sign(twist) * normalized
        val dt = if (lastFrame == 0L) .016 else ((now-lastFrame)/1e9).coerceIn(.001, .05)
        lastFrame = now
        filtered += (target-filtered) * (1-exp(-dt/.025))
        return filtered.toFloat()
    }
}
