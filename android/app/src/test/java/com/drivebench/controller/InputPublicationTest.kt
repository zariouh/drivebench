package com.drivebench.controller

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class InputPublicationTest {
    @Test fun renderSnapshotDoesNotShareMutableContactsOrTouchEngineAfterPublication() {
        val engine=ControlEngine();engine.down(1,Role.PEDALS,100f,100f);engine.move(1,100f,10f)
        val published=ControlRenderState.capture(engine,false,800,400)
        engine.move(1,50f,100f);engine.up(1)
        assertEquals(10f,published.contacts.single().y,0f)
        assertTrue(published.controls.throttle>0);assertEquals(0f,engine.output().throttle,0f)
    }
    @Test fun blockedRendererDoesNotOwnControlPublicationOrEngineLock() {
        val started=CountDownLatch(1);val release=CountDownLatch(1)
        val engine=ControlEngine();val render=ControlRenderState.capture(engine,false,800,400)
        val thread=Thread { started.countDown();release.await();assertEquals(0f,render.controls.throttle,0f) }
        thread.start();assertTrue(started.await(1,TimeUnit.SECONDS))
        try {
            engine.down(1,Role.PEDALS,100f,100f);engine.move(1,100f,0f)
            repeat(100) { assertTrue(ControlRenderState.capture(engine,false,800,400).controls.throttle>0) }
        } finally { release.countDown();thread.join(1000) }
    }
    @Test fun delayedAndFutureTouchEventsCannotBeRestampedWithDispatchTime() {
        assertEquals(899_000L,InputEventTime.elapsedUs(980,1000,920_000))
        assertNull(InputEventTime.elapsedUs(940,1000,920_000))
        assertNull(InputEventTime.elapsedUs(1001,1000,920_000))
    }
    @Test fun actualInputOwnerStallRemainsStaleEvenWhileRendererAndNetworkRun() {
        val p=PhoneSnapshot(Controls(throttle=1f),1_000_000,1_000_000,1_000_000,false,true)
        assertEquals(PauseCause.UI_DELAY,SenderHealth.interruption(p,1_055_000,1_055_000))
        val bytes=LanWire.input(7,1,2,ByteArray(32),p,1_055_000,false,false,true)
        val b=java.nio.ByteBuffer.wrap(bytes).order(java.nio.ByteOrder.LITTLE_ENDIAN)
        assertEquals(1_000_000,b.getLong(32));assertEquals(55_000,b.getInt(64))
        assertEquals(0,b.getShort(58).toInt());assertTrue(b.getShort(6).toInt() and 32!=0)
    }
    @Test fun genuineInputCyclePreservesNewMotionSourceAndDoesNotReclassifyAnOldQueuedMove() {
        val progress=InputDispatchProgress()
        assertTrue(progress.position(980,1000,920_000))
        assertEquals(899_000L,progress.sourceForPublication(921_000))
        assertFalse(progress.position(940,1000,922_000))
        // Stationary held contacts can be validated only by another real input-owner cycle.
        assertEquals(923_000L,progress.sourceForPublication(923_000))
    }

}
