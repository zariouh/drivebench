package com.drivebench.controller

import org.junit.Assert.*
import org.junit.Test

class ConnectionPolicyTest {
    @Test fun cellularDefaultDoesNotHideAvailableWifi() {
        val candidates=listOf(
            WifiCandidate("mobile",false,default=true),
            WifiCandidate("wifi",true,prefixes=listOf(Ipv4Prefix("192.168.222.56",24))))
        assertEquals("wifi",WifiSelection.choose(candidates,"192.168.222.201"))
    }
    @Test fun localSubnetWinsOverAnotherDefaultWifi() {
        val candidates=listOf(
            WifiCandidate("other",true,default=true,prefixes=listOf(Ipv4Prefix("192.168.10.5",24))),
            WifiCandidate("pc-lan",true,prefixes=listOf(Ipv4Prefix("192.168.222.56",24))))
        assertEquals("pc-lan",WifiSelection.choose(candidates,"192.168.222.201"))
    }
    @Test fun wifiSelectionDoesNotFallBackToMobileOrVpn() {
        assertNull(WifiSelection.choose(listOf(WifiCandidate("mobile",false,default=true)),"192.168.222.201"))
        assertNull(WifiSelection.choose(listOf(WifiCandidate("vpn",true,vpn=true)),"192.168.222.201"))
        assertEquals("wifi",WifiSelection.choose(listOf(WifiCandidate("wifi",true)),"192.168.222.201"))
    }
    @Test fun cidrMatchingHandlesBoundaryAndCanonicalAddresses() {
        assertTrue(Ipv4Prefix("192.168.222.10",24).matches("192.168.222.201"))
        assertFalse(Ipv4Prefix("192.168.222.10",24).matches("192.168.221.201"))
        assertFalse(Ipv4Prefix("192.168.222.10",32).matches("192.168.222.11"))
        assertTrue(Ipv4Prefix("10.0.0.1",8).matches("10.255.255.254"))
        assertNull(Ipv4.number("010.0.0.1"));assertNull(Ipv4.number("+10.0.0.1"));assertNull(Ipv4.number("192.168.1.999"))
    }
    @Test fun failedPairingCannotEnableLocalResumeOrHideItsReason() {
        val error=LinkView(reason="Android could not expose Wi-Fi to the app",stage=LinkStage.FAILED)
        for(paused in listOf(false,true)) {
            val ui=ConnectionPolicy.present(error,paused,true)
            assertEquals(error.reason,ui.message);assertEquals("NOT CONNECTED",ui.heading)
            assertFalse(ui.enabled);assertEquals("CONNECT FIRST",ui.action)
        }
    }
    @Test fun resumeRequiresBothAuthenticatedChannelsAndReadyOutput() {
        val ready=LinkView(connected=true,ready=true,sync=true,real=true,stage=LinkStage.READY,releaseConfirmed=true,neutralConfirmed=true)
        assertTrue(ConnectionPolicy.present(ready,true,true).enabled)
        assertFalse(ConnectionPolicy.present(ready.copy(armed=true),true,true).enabled)
        assertFalse(ConnectionPolicy.present(ready.copy(phase=DrivePhase.STOP_PENDING),true,true).enabled)
        assertFalse(ConnectionPolicy.present(ready,true,false).enabled)
        assertFalse(ConnectionPolicy.present(ready.copy(ready=false),true,true).enabled)
        assertFalse(ConnectionPolicy.present(ready.copy(sync=false),true,true).enabled)
        assertTrue(ConnectionPolicy.present(ready.copy(armed=true),false,false).enabled) // Stop stays available.
    }
    @Test fun pausedInstructionsDoNotAskToPressADisabledResumeOrDuplicateTheInstruction() {
        val waiting=ConnectionPolicy.Presentation("PC","Waiting for acknowledged neutral controls…","WAITING…",false)
        val message=ConnectionPolicy.pauseMessage("Interruption lasted too long · tap Resume",waiting)
        assertFalse(message.contains("tap Resume",true));assertTrue(message.contains("Waiting for acknowledged neutral"))
        val ready=waiting.copy(action="RESUME",enabled=true)
        assertEquals(1,Regex("tap Resume",RegexOption.IGNORE_CASE).findAll(ConnectionPolicy.pauseMessage("PC: Connection remains unstable — tap Resume",ready)).count())
    }

}
