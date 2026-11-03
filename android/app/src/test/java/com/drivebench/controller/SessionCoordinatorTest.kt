package com.drivebench.controller

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class SessionCoordinatorTest {
    private var inputSequence=0L
    private fun ready(): Pair<SessionCoordinator,PauseHistory> {
        val history=PauseHistory();val state=SessionCoordinator(history)
        state.connected();state.ack(ControlAck(false,true,true,1,0,"Connected",true),1_000_000)
        neutral(state,1_000_000)
        return state to history
    }
    private fun neutral(state: SessionCoordinator, now: Long) {
        val sequence=++inputSequence
        state.sending(sequence,SendPlan(state.view.epoch,false,false),now)
        state.feedback(sequence,0,now)
    }
    private fun snapshot(state: SessionCoordinator, now: Long=1_010_000, enabled: Boolean=true)=
        PhoneSnapshot(Controls(throttle=1f),now,now,now,false,enabled,state.view.armGeneration,true,
            if(enabled) DisabledReason.NONE else DisabledReason.ENGINE_PAUSED,state.view.epoch)
    private fun drive(state: SessionCoordinator) {
        assertTrue(state.arm(false,true,1_001_000))
        state.ack(ControlAck(true,true,true,2,1,"Driving",true),1_002_000)
    }
    @Test fun resumeRequiresAcknowledgedUnarmedStateAndDoesNotRepeatPendingRequest() {
        val s=SessionCoordinator(PauseHistory());s.connected()
        assertFalse(s.arm(false,true,1_000_000))
        s.ack(ControlAck(false,true,true,1,0,"Ready",true),1_000_000)
        assertFalse(s.arm(false,true,1_000_001));neutral(s,1_000_001)
        assertFalse(s.arm(false,false,1_001_000));assertTrue(s.arm(false,true,1_001_000))
        assertFalse(s.arm(false,true,1_001_001));assertEquals(1L,s.command().id)
        assertEquals(DrivePhase.ARM_PENDING,s.view.phase)
        assertFalse(s.plan(snapshot(s),1_010_000,1_010_000)!!.armed)
    }
    @Test fun quickResumeAgainstOldArmedViewIsRejectedWithoutFalseInactivePause() {
        val (s,h)=ready();drive(s)
        s.stop(PauseCause.STOP_BUTTON,1_003_000)
        assertTrue(s.view.armed);assertFalse(s.canArm(true,1_004_000));assertFalse(s.arm(false,true,1_004_000))
        val plan=s.plan(snapshot(s,1_004_000,false),1_004_000,1_004_000)!!
        assertTrue(plan.stop);assertFalse(plan.armed)
        assertEquals(PauseCause.STOP_BUTTON.message,h.reason)
        s.ack(ControlAck(false,true,true,3,2,"Stopped",true),1_005_000)
        neutral(s,1_005_001);assertTrue(s.canArm(true,1_006_000))
    }
    @Test fun olderDisarmAckCannotEraseNewerResumeIntent() {
        val (s,_)=ready();drive(s);s.stop(PauseCause.STOP_BUTTON,1_003_000)
        val stopped=ControlAck(false,true,true,3,2,"Stopped",true)
        s.ack(stopped,1_004_000);neutral(s,1_004_001);assertTrue(s.arm(false,true,1_005_000))
        s.ack(stopped,1_006_000)
        assertEquals(DrivePhase.ARM_PENDING,s.view.phase);assertEquals(3L,s.command().id)
        s.ack(ControlAck(true,true,true,4,3,"Driving",true),1_007_000)
        assertEquals(DrivePhase.DRIVING,s.view.phase);assertEquals(2L,s.view.armGeneration)
    }
    @Test fun ackAndResumePublicationRemainCoherentWhenThreadsCompete() {
        val pool=Executors.newFixedThreadPool(2)
        try { repeat(150) {
            val (s,_)=ready();val start=CountDownLatch(1)
            val a=pool.submit { start.await();s.ack(ControlAck(false,true,true,1,0,"Ready",true),1_001_000) }
            val b=pool.submit<Boolean> { start.await();s.arm(false,true,1_001_000) }
            start.countDown();a.get(2,TimeUnit.SECONDS);assertTrue(b.get(2,TimeUnit.SECONDS))
            assertEquals(DrivePhase.ARM_PENDING,s.view.phase);assertEquals("arm",s.command().name)
        } } finally { pool.shutdownNow() }
    }
    @Test fun cancellationDuringArmCannotBeRevivedByLateGrant() {
        val (s,h)=ready();assertTrue(s.arm(false,true,1_001_000))
        s.stop(PauseCause.FOCUS_LOST,1_002_000)
        s.ack(ControlAck(true,true,true,2,1,"Driving",true),1_003_000)
        assertEquals(DrivePhase.STOP_PENDING,s.view.phase);assertEquals(0L,s.view.armGeneration)
        assertTrue(s.plan(snapshot(s),1_010_000,1_010_000)!!.stop)
        assertEquals(PauseCause.FOCUS_LOST.message,h.reason)
        s.ack(ControlAck(false,true,true,3,2,"Stopped",true),1_011_000)
        assertEquals(DrivePhase.PAUSED,s.view.phase)
    }
    @Test fun permissionWaitNeverReusesOldGenerationAndCannotSkipForever() {
        val (s,h)=ready();val old=snapshot(s,1_001_000)
        drive(s)
        assertNull(s.plan(old,1_010_000,1_010_000))
        assertNull(s.plan(old,1_052_000,1_052_000))
        val release=s.plan(old,1_052_001,1_052_001)!!
        assertTrue(release.suspend);assertFalse(release.armed);assertFalse(release.stop)
        assertNull(h.reason);assertTrue(h.report().contains("fresh control generation"))
    }
    @Test fun matchingFreshGenerationCanDriveButNewFaultRequiresNewResume() {
        val (s,h)=ready();drive(s)
        assertTrue(s.plan(snapshot(s),1_010_000,1_010_000)!!.armed)
        s.ack(ControlAck(false,true,true,3,1,"Input source deadline expired",true),1_011_000)
        assertEquals(DrivePhase.PAUSED,s.view.phase)
        assertFalse(s.plan(snapshot(s,1_012_000),1_012_000,1_012_000)!!.armed)
        assertTrue(h.reason!!.contains("Input source deadline expired"))
        neutral(s,1_012_001);assertTrue(s.arm(false,true,1_013_000));assertNull(h.reason)
    }
    @Test fun rejectedResumeKeepsPcReasonAndDoesNotAutoArm() {
        val (s,h)=ready();assertTrue(s.arm(false,true,1_001_000))
        s.ack(ControlAck(false,true,true,1,1,"Waiting for neutral",true),1_002_000)
        assertEquals(DrivePhase.PAUSED,s.view.phase);assertEquals("PC: Waiting for neutral",h.reason)
        assertFalse(s.plan(snapshot(s),1_010_000,1_010_000)!!.armed)
    }
    @Test fun realFeedbackAndUiLimitsNeutralizeRecoverablyAndExplicitStopStillLatches() {
        val (s,h)=ready();drive(s)
        assertTrue(s.plan(snapshot(s,1_151_000),1_151_000,1_001_000)!!.armed)
        assertTrue(s.plan(snapshot(s,1_151_001),1_151_001,1_001_000)!!.suspend)
        assertNull(h.reason);assertTrue(h.report().contains("feedback age 150 ms"))
        s.stop(PauseCause.CONNECTION_MENU,1_152_000)
        assertTrue(h.reason!!.startsWith(PauseCause.CONNECTION_MENU.message))
        val (u,uh)=ready();drive(u)
        assertTrue(u.plan(snapshot(u,1_002_000),1_052_001,1_052_001)!!.suspend)
        assertNull(uh.reason);assertTrue(uh.report().contains(PauseCause.UI_DELAY.message))
    }
    @Test fun disabledFocusAndBackgroundGetSpecificReasons() {
        for((disabled,cause) in listOf(DisabledReason.WINDOW_FOCUS to PauseCause.FOCUS_LOST,
            DisabledReason.BACKGROUND to PauseCause.APP_BACKGROUND,DisabledReason.ENGINE_PAUSED to PauseCause.INPUT_DISABLED)) {
            val (s,h)=ready();drive(s)
            assertTrue(s.plan(snapshot(s).copy(enabled=false,disabledReason=disabled),1_010_000,1_010_000)!!.stop)
            assertTrue(h.reason!!.startsWith(cause.message));assertTrue(h.reason!!.contains(disabled.name))
        }
    }
    @Test fun pendingAcknowledgementsAreBoundedAndClosedSessionsCannotReturn() {
        val (s,_)=ready();assertTrue(s.arm(false,true,1_001_000))
        val plan=s.plan(snapshot(s,3_001_001),3_001_001,3_001_001)!!
        assertNotNull(plan.disconnect);assertFalse(plan.armed)
        s.close();s.ack(ControlAck(true,true,true,2,1,"Driving",true),3_002_000)
        assertFalse(s.view.connected);assertFalse(s.arm(false,true,3_003_000))
        assertNull(s.plan(snapshot(s),3_004_000,3_004_000))
    }
    @Test fun neutralProofMustMatchCurrentEpochAndRemainFresh() {
        val (s,_)=ready();assertTrue(s.canArm(true,1_099_999));assertFalse(s.canArm(true,1_100_001))
        neutral(s,1_101_000);assertTrue(s.canArm(true,1_101_001))
        assertTrue(s.arm(false,true,1_101_002));s.ack(ControlAck(true,true,true,2,1,"Driving",true),1_101_003)
        s.stop(PauseCause.STOP_BUTTON,1_102_000);s.ack(ControlAck(false,true,true,3,2,"Stopped",true),1_103_000)
        s.feedback(1,0,1_103_001);assertFalse(s.canArm(true,1_103_001))
        neutral(s,1_103_002);assertTrue(s.canArm(true,1_103_002))
    }
    @Test fun delayedOrRepeatedNeutralFeedbackCannotRenewProof() {
        val (s,_)=ready()
        s.sending(2,SendPlan(1,false,false),1_000_000)
        s.feedback(2,0,1_150_000);assertFalse(s.canArm(true,1_150_000))
        s.sending(3,SendPlan(1,false,false),1_151_000)
        s.feedback(3,0,1_152_000);assertTrue(s.canArm(true,1_152_000))
        s.feedback(3,0,1_252_000);assertFalse(s.canArm(true,1_252_000))
    }
    @Test fun staleEpochAndUnissuedCommandAreRejected() {
        val (s,_)=ready();drive(s)
        for(ack in listOf(ControlAck(false,true,true,1,1,"old",true),ControlAck(false,true,true,3,100,"future",true))) {
            try { s.ack(ack,1_010_000);fail("invalid acknowledgement accepted") } catch(_: IllegalArgumentException) {}
        }
    }
    @Test fun pcRecoveryKeepsGenerationAndRequiresNewProducerPublication() {
        val (s,h)=ready();drive(s)
        val cached=snapshot(s,1_010_000)
        s.ack(ControlAck(false,true,true,3,1,"Recovering",true,true),1_011_000)
        assertEquals(DrivePhase.RECOVERING,s.view.phase);assertEquals(1L,s.view.armGeneration)
        assertFalse(s.plan(cached,1_012_000,1_012_000)!!.armed)
        assertFalse(s.plan(cached.copy(recoveryEpoch=3),1_012_000,1_012_000)!!.armed)
        assertTrue(s.plan(snapshot(s,1_013_000),1_014_000,1_014_000)!!.armed)
        s.ack(ControlAck(true,true,true,3,1,"Driving",true),1_015_000)
        assertEquals(DrivePhase.DRIVING,s.view.phase);assertEquals(1L,s.view.armGeneration)
        assertNull(h.reason);assertTrue(h.report().contains("Automatic recoveries: 1"))
    }
    @Test fun localFeedbackGapCannotSendCachedActiveUntilPcChallengeAndRealInputProgress() {
        val (s,_)=ready();drive(s)
        val p=snapshot(s,1_200_000)
        assertTrue(s.plan(p,1_200_000,1_002_000)!!.suspend)
        assertEquals("suspend",s.command().name)
        // Old armed ACK predates our request. It must not cancel suspension.
        s.ack(ControlAck(true,true,true,2,1,"Driving",true),1_201_000)
        assertEquals(DrivePhase.RECOVERING,s.view.phase)
        assertFalse(s.plan(p,1_202_000,1_202_000)!!.armed)
        s.ack(ControlAck(false,true,true,3,2,"Recovering",true,true),1_203_000)
        assertFalse(s.plan(p,1_204_000,1_204_000)!!.armed)
        assertTrue(s.plan(snapshot(s,1_205_000),1_205_000,1_205_000)!!.armed)
    }
    @Test fun recoveryTimeoutAndAllExplicitCancellationsBeatLateGrants() {
        for(cause in listOf(PauseCause.STOP_BUTTON,PauseCause.FOCUS_LOST,PauseCause.APP_BACKGROUND,
            PauseCause.TOUCH_CANCELLED,PauseCause.MODE_CHANGED,PauseCause.CENTER_CHANGED)) {
            val (s,h)=ready();drive(s)
            s.ack(ControlAck(false,true,true,3,1,"Recovering",true,true),1_011_000)
            s.stop(cause,1_012_000)
            s.ack(ControlAck(true,true,true,3,1,"Driving",true),1_013_000)
            val plan=s.plan(snapshot(s,1_014_000),1_014_000,1_014_000)!!
            assertTrue(plan.stop);assertFalse(plan.armed);assertEquals(DrivePhase.STOP_PENDING,s.view.phase)
            assertTrue(h.reason!!.startsWith(cause.message))
        }
        val (s,h)=ready();drive(s)
        s.ack(ControlAck(false,true,true,3,1,"Recovering",true,true),1_011_000)
        assertTrue(s.plan(snapshot(s,2_011_000),2_011_000,2_011_000)!!.stop)
        assertTrue(h.reason!!.startsWith(PauseCause.RECOVERY_TIMEOUT.message))
    }
    @Test fun touchUpDuringInterruptionPublishesNeutralInsteadOfResurrectingThrottle() {
        val (s,_)=ready();drive(s)
        val engine=ControlEngine();engine.down(9,Role.PEDALS,100f,100f);engine.move(9,100f,0f)
        assertTrue(engine.output().throttle>0)
        s.ack(ControlAck(false,true,true,3,1,"Recovering",true,true),1_011_000)
        engine.up(9)
        val current=snapshot(s,1_012_000).copy(controls=engine.output())
        assertTrue(s.plan(current,1_013_000,1_013_000)!!.armed)
        assertEquals(0f,current.controls.throttle,0f)
    }
    @Test fun recoveryBeforeInitialArmAckDoesNotGetStuckWaiting() {
        val (s,_)=ready();assertTrue(s.arm(false,true,1_001_000))
        s.ack(ControlAck(false,true,true,3,1,"Recovering",true,true),1_011_000)
        assertEquals(DrivePhase.RECOVERING,s.view.phase)
        assertTrue(s.plan(snapshot(s,1_012_000),1_013_000,1_013_000)!!.armed)
    }

    @Test fun stopOrNewChallengeInvalidatesAnAlreadyPreparedActiveSend() {
        repeat(200) { index ->
            val (s,_)=ready();drive(s)
            val before=s.plan(snapshot(s),1_010_000,1_010_000)!!
            s.stop(if(index%2==0) PauseCause.STOP_BUTTON else PauseCause.FOCUS_LOST,1_010_001)
            assertFalse(s.sending(12,before,1_010_002))
            assertTrue(s.sending(13,s.plan(snapshot(s),1_010_003,1_010_003)!!,1_010_004))
        }
    }

    @Test fun feedbackForRejectedNeutralDataCannotConfirmFreshNeutralProof() {
        val s=SessionCoordinator(PauseHistory());s.connected()
        s.ack(ControlAck(false,true,true,1,0,"Ready",true),1_000_000)
        s.sending(1,SendPlan(1,false,false),1_000_000)
        s.feedback(1,32,1_001_000)
        assertFalse(s.canArm(true,1_001_000))
    }

    @Test fun recorded394msFeedbackGapDoesNotConsumeRecoveryBeforeFreshChallengeInputCanReturn() {
        val (s,h)=ready();drive(s)
        s.plan(snapshot(s,1_151_000),1_151_000,1_000_000)
        assertEquals(DrivePhase.RECOVERING,s.view.phase)
        // Frozen log:308ms after suspension, fresh feedback is65ms old and UI input is6ms old.
        val waiting=s.plan(snapshot(s,1_453_000),1_459_000,1_394_000)!!
        assertFalse(waiting.stop);assertFalse(waiting.armed);assertTrue(waiting.suspend)
        assertEquals(DrivePhase.RECOVERING,s.view.phase);assertNull(h.reason)
        s.ack(ControlAck(false,true,true,3,2,"Recovering",true,true),1_470_000)
        assertFalse(s.plan(snapshot(s,1_469_000),1_471_000,1_470_000)!!.armed)
        assertTrue(s.plan(snapshot(s,1_486_000),1_486_000,1_480_000)!!.armed)
        s.ack(ControlAck(true,true,true,3,2,"Driving",true),1_500_000)
        assertEquals(DrivePhase.DRIVING,s.view.phase);assertNull(h.reason)
    }
    @Test fun returningFeedbackAndRepeatedRecoveryAcksNeverExtendTheFixedWindow() {
        val (s,h)=ready();drive(s)
        s.plan(snapshot(s,1_151_000),1_151_000,1_000_000)
        repeat(8) { i ->
            val at=1_200_000+i*100_000L
            s.ack(ControlAck(false,true,true,3,2,"Recovering",true,true),at)
            val p=s.plan(snapshot(s,at),at,at)!!
            assertFalse(p.stop)
        }
        val expired=s.plan(snapshot(s,2_151_000),2_151_000,2_151_000)!!
        assertTrue(expired.stop);assertEquals(DrivePhase.STOP_PENDING,s.view.phase)
        assertTrue(h.reason!!.contains(PauseCause.RECOVERY_TIMEOUT.message))
    }

}
