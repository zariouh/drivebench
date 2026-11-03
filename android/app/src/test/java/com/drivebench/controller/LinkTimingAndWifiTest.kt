package com.drivebench.controller

import org.junit.Assert.*
import org.junit.Test

class LinkTimingAndWifiTest {
    @Test fun wifiRequestIsScopedIdempotentAndClosedForGood() {
        var acquired=0;var released=0
        val lease=ScopedWifiLease({ acquired++;true },{ released++ })
        lease.connected(true);assertEquals(0,acquired)
        lease.foreground(true);lease.foreground(true);assertEquals(1,acquired)
        assertTrue(lease.report().contains("effect unverified"))
        lease.foreground(false);lease.foreground(false);assertEquals(1,released)
        lease.foreground(true);assertEquals(2,acquired)
        lease.connected(false);assertEquals(2,released)
        lease.connected(true);lease.close();assertEquals(3,released)
        lease.foreground(true);lease.connected(true);assertEquals(3,acquired)
    }
    @Test fun unsupportedAndFailedWifiRequestNeverBlockConnectionOrHammerService() {
        var attempts=0
        val lease=ScopedWifiLease({ attempts++;throw SecurityException("denied") },{})
        lease.foreground(true);lease.connected(true);lease.connected(true)
        assertEquals(1,attempts);assertTrue(lease.report().contains("SecurityException"))
        lease.connected(false);lease.connected(true);assertEquals(2,attempts);lease.close()
        val absent=ScopedWifiLease({ false },{ fail("nothing held") })
        absent.foreground(true);absent.connected(true);absent.close()
        assertTrue(absent.report().contains("not available"))
    }
    @Test fun staleConnectionNotificationsRecomputeCurrentOwnership() {
        var active=false;var acquired=0;var released=0
        val lease=ScopedWifiLease({ acquired++;true },{ released++ });lease.foreground(true)
        active=true;lease.refreshConnected { active };assertEquals(1,acquired)
        active=false;lease.refreshConnected { active };lease.refreshConnected { active }
        assertEquals(1,released)
    }
    @Test fun timingUsesEchoPhoneClockAndPcResidenceSeparatelyAndIsBounded() {
        val timing=LinkTiming()
        val snapshot=PhoneSnapshot(producedUs=999_000,armGeneration=4,disabledReason=DisabledReason.ENGINE_PAUSED)
        timing.wake(1_000_000,snapshot);timing.wake(1_025_000,snapshot)
        timing.sent(1_025_000,1_027_000)
        timing.feedback(1_030_000,1_020_000,900_000_000,900_003_000)
        repeat(20) { timing.pause(1_030_000L+it,"event $it",DrivePhase.DRIVING,4,2) }
        val report=timing.report()
        assertTrue(report.contains("wake 25ms"));assertTrue(report.contains("send 2ms"))
        assertTrue(report.contains("echo elapsed 10ms"));assertTrue(report.contains("PC residence 3ms"))
        assertEquals(6,report.lineSequence().count { it.startsWith("- ") });assertTrue(report.length<5000)
        assertFalse(report.contains("event 0;"));assertTrue(report.contains("ENGINE_PAUSED"))
    }
}
