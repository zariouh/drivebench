package com.drivebench.controller

import org.junit.Assert.*
import org.junit.Test

class PauseHistoryTest {
    private fun sample(gyro: Boolean=false)=PhoneSnapshot(producedUs=1_000_000,steeringUs=1_000_000,
        sensorUs=1_000_000,gyro=gyro,enabled=true,sensorReady=true)
    @Test fun frameDeadlineBoundaryAndTouchDoesNotRequireGyro() {
        assertNull(SenderHealth.interruption(sample().copy(sensorUs=0,sensorReady=false),1_050_000,1_000_000))
        assertEquals(PauseCause.UI_DELAY,SenderHealth.interruption(sample(),1_050_001,1_050_000))
    }
    @Test fun gyroAndFeedbackKeepTheirExistingLimits() {
        assertEquals(PauseCause.GYRO_DELAY,SenderHealth.interruption(sample(true).copy(producedUs=1_050_001),1_050_001,1_050_000))
        assertNull(SenderHealth.interruption(sample().copy(producedUs=1_150_000),1_150_000,1_000_000))
        assertEquals(PauseCause.FEEDBACK_DELAY,SenderHealth.interruption(sample().copy(producedUs=1_150_001),1_150_001,1_000_000))
    }
    @Test fun disabledAndFutureInputCannotDrive() {
        assertEquals(PauseCause.INPUT_DISABLED,SenderHealth.interruption(sample().copy(enabled=false),1_000_000,1_000_000))
        assertEquals(PauseCause.UI_DELAY,SenderHealth.interruption(sample(),999_999,999_999))
        assertEquals(PauseCause.GYRO_DELAY,SenderHealth.interruption(sample(true).copy(sensorUs=1_000_001),1_000_000,1_000_000))
    }
    @Test fun firstPhoneCauseSurvivesGenericPcStopAndMenuFocus() {
        val pauses=PauseHistory()
        assertTrue(pauses.capture(PauseCause.UI_DELAY.message,1_065_000,"update age 65 ms"))
        assertFalse(pauses.capture("PC: Stopped — tap Resume",1_100_000))
        assertFalse(pauses.capture(PauseCause.CONNECTION_MENU.message,1_200_000))
        assertEquals("Phone input update was delayed (update age 65 ms)",pauses.reason)
        assertFalse(pauses.report().contains("PC: Stopped"))
    }
    @Test fun resumeClearsCurrentButPreservesPreviousEvidence() {
        val pauses=PauseHistory();pauses.capture(PauseCause.TOUCH_CANCELLED.message,1000)
        pauses.resume();assertNull(pauses.reason)
        assertTrue(pauses.capture(PauseCause.FOCUS_LOST.message,2000))
        assertTrue(pauses.report().contains(PauseCause.TOUCH_CANCELLED.message))
        assertEquals(PauseCause.FOCUS_LOST.message,pauses.reason)
    }
    @Test fun boundedHistoryRedactsSecretsAndNeverStoresQr() {
        val pauses=PauseHistory();val token="Q".repeat(43)
        repeat(30) { pauses.resume();pauses.capture("entry $it drivebench://pair?token=$token",it.toLong(),"K".repeat(44)) }
        val report=pauses.report()
        assertEquals(12,report.lineSequence().count { it.startsWith("- ") })
        assertFalse(report.contains(token));assertFalse(report.contains("drivebench://"))
        assertFalse(report.contains("entry 0 "));assertTrue(report.length<4000)
    }
}
