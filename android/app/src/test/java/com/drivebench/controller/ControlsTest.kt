package com.drivebench.controller

import org.junit.Assert.*
import org.junit.Test
import kotlin.math.*

class ControlsTest {
    @Test fun pointersKeepTheirRolesAndReleaseIndependently() {
        val e=ControlEngine()
        assertTrue(e.down(9,Role.STEER,100f,100f))
        assertFalse(e.down(10,Role.STEER,150f,100f))
        assertTrue(e.down(22,Role.PEDALS,400f,100f))
        e.move(9,176f,100f);e.move(22,400f,24f)
        assertEquals(1f,e.output().steering,0f);assertEquals(1f,e.output().throttle,0f)
        e.up(9);assertEquals(0f,e.output().steering,0f);assertEquals(1f,e.output().throttle,0f)
        e.up(22);assertEquals(Controls(),e.output())
    }
    @Test fun brakeRequiresNeutralBeforeThrottleCanReturn() {
        val e=ControlEngine();e.down(1,Role.PEDALS,400f,200f)
        e.move(1,400f,124f);assertEquals(1f,e.output().throttle,0f)
        e.move(1,324f,200f);assertEquals(1f,e.output().brake,0f)
        e.move(1,400f,124f);assertEquals(0f,e.output().throttle,0f)
        e.move(1,400f,200f);assertEquals(Controls(),e.output())
        e.move(1,400f,124f);assertEquals(1f,e.output().throttle,0f)
    }
    @Test fun stoppingChangingModeAndCancellingClearHeldState() {
        val e=ControlEngine();e.down(1,Role.PEDALS,400f,200f);e.move(1,400f,100f)
        e.togglePaused();assertEquals(Controls(),e.output());assertFalse(e.down(2,Role.PEDALS,400f,200f))
        e.togglePaused();assertEquals(Controls(),e.output());e.move(1,400f,100f);assertEquals(Controls(),e.output())
        e.down(2,Role.STEER,100f,100f);e.move(2,-1000f,100f);assertEquals(-1f,e.output().steering,0f)
        e.setMode(DrivingMode.GYRO);assertTrue(e.contacts.isEmpty());assertEquals(Controls(),e.output())
        e.down(3,Role.THROTTLE,400f,200f);e.move(3,400f,100f);assertEquals(Controls(),e.output())
        e.gyroReady=true;e.releaseAll();assertEquals(Controls(),e.output())
    }
    @Test fun gyroBrakeDoesNotRestoreOldHeldThrottle() {
        val e=ControlEngine();e.setMode(DrivingMode.GYRO);e.gyroReady=true
        e.down(1,Role.THROTTLE,400f,200f);e.move(1,400f,124f);assertEquals(1f,e.output().throttle,0f)
        e.down(2,Role.BRAKE,100f,200f);e.move(2,100f,124f);assertEquals(0f,e.output().throttle,0f)
        e.up(2);assertEquals(0f,e.output().throttle,0f)
        e.move(1,400f,200f);e.output();e.move(1,400f,124f);assertEquals(1f,e.output().throttle,0f)
    }
    @Test fun nonfiniteInputIsReleasedAndAxesStayBounded() {
        val e=ControlEngine();assertFalse(e.down(1,Role.STEER,Float.NaN,0f))
        e.down(1,Role.STEER,0f,0f);e.move(1,Float.POSITIVE_INFINITY,0f);assertEquals(Controls(),e.output())
        e.down(2,Role.PEDALS,500f,500f);e.move(2,500f,-10000f);assertEquals(1f,e.output().throttle,0f)
    }
    @Test fun invalidPreferencesCannotPoisonControlMath() {
        val e=ControlEngine();e.travel=Float.NaN;assertEquals(76f,e.travel,0f)
        e.travel=10000f;assertEquals(120f,e.travel,0f)
        val gyro=GyroSteering();gyro.fullTurnDegrees=Double.POSITIVE_INFINITY
        assertEquals(30.0,gyro.fullTurnDegrees,0.0)
    }
    private fun rotation(degrees: Double, timestamp: Long=1_000_000_000L): RotationSample {
        val half=Math.toRadians(degrees)/2
        return RotationSample(Quaternion(0.0,0.0,sin(half),cos(half)),timestamp)
    }
    @Test fun gyroUsesRelativeGripAndEquivalentQuaternionSigns() {
        val g=GyroSteering();assertTrue(g.center(rotation(40.0),1_000_000_000))
        repeat(40) { g.sample(rotation(10.0,1_000_000_000L+it*16_000_000L),1_000_000_000L+it*16_000_000L) }
        val expected=g.sample(rotation(10.0,1_640_000_000),1_640_000_000)
        assertTrue(expected>.99f)
        val q=rotation(10.0).rotation
        val equivalent=Quaternion(-q.x,-q.y,-q.z,-q.w)
        assertEquals(expected,g.sample(RotationSample(equivalent,1_656_000_000),1_656_000_000),.001f)
    }
    @Test fun staleGyroRequiresExplicitCenterAgain() {
        val g=GyroSteering();assertTrue(g.center(rotation(0.0),1_000_000_000))
        assertEquals(0f,g.sample(rotation(30.0),1_100_000_001),0f);assertFalse(g.calibrated)
        assertEquals(0f,g.sample(rotation(30.0,2_000_000_000),2_000_000_000),0f)
        assertFalse(g.center(rotation(0.0,3_000_000_000),2_000_000_000))
        assertTrue(g.center(rotation(0.0,2_000_000_000),2_000_000_000))
        assertEquals(0f,g.sample(rotation(.5,2_016_000_000),2_016_000_000),0f)
    }
}
