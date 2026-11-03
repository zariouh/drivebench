package com.drivebench.controller

import org.junit.Assert.*
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

class LanWireTest {
    private val key=ByteArray(32) { it.toByte() }
    @Test fun independentGoldenVector() {
        val snapshot=PhoneSnapshot(Controls(-16384f/32767f,1f,32768f/65535f),899000,900000,0,false,true)
        val bytes=LanWire.input(7,9,2,key,snapshot,900000,true,false)
        val golden="445243310101030007000000000000000900000000000000a0bb0d0000000000a0bb0d0000000000020000000000000000c0000000000000ffff008000000000e803000000000000a33285ce1af7083fe95abd331208f439"
        assertEquals(golden,bytes.joinToString("") { "%02x".format(it.toInt() and 255) })
    }
    @Test fun modificationAndWrongDirectionKeyFail() {
        val b=LanWire.input(1,1,1,key,PhoneSnapshot(producedUs=100000,steeringUs=100000),100001,false,false)
        assertTrue(LanWire.authentic(b,72,key)); assertFalse(LanWire.authentic(b,72,ByteArray(32))); b[48]=1
        assertFalse(LanWire.authentic(b,72,key)); assertFalse(LanWire.authentic(b,72,ByteArray(32)))
    }
    @Test fun onlyTheScannedCertificateFingerprintIsAccepted() {
        val cert="certificate-fixture".toByteArray()
        val pin="da4506ebde81a30857b07bfd3375d3ce763d11dae54de9876bf528de9bb506be"
        assertTrue(LanWire.certificateMatches(cert,pin))
        assertFalse(LanWire.certificateMatches("different certificate".toByteArray(),pin))
        assertFalse(LanWire.certificateMatches(cert,"00".repeat(32)))
        assertFalse(LanWire.certificateMatches(cert,"not a pin"))
    }
    @Test fun independentStatusVectorAndSessionIsolation() {
        val text="4452433101020a00070000000000000002000000000000000900000000000000a0bb0d000000000040420f000000000008430f000000000008000000000000006cbb43591af9cd16ca5d905ccfeaf38f"
        val bytes=text.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        val status=LanWire.feedback(bytes,7,key)!!
        assertEquals(9L,status.echoed);assertEquals(8L,status.applied);assertEquals(1000200L,status.sentUs)
        assertNull(LanWire.feedback(bytes,8,key));assertNull(LanWire.feedback(bytes,7,ByteArray(32)))
    }
    @Test fun stoppedAndUnarmedPacketsAreAlwaysNeutral() {
        val snapshot=PhoneSnapshot(Controls(1f,1f,1f),100000,100000,100000,false,true)
        for ((armed,stop) in listOf(false to false,true to true)) {
            val b=LanWire.input(1,1,1,key,snapshot,100001,armed,stop)
            assertTrue(b.copyOfRange(44,60).all { it==0.toByte() })
        }
    }
    @Test fun frozenUiAndStaleSensorsCannotClaimHealth() {
        val snapshot=PhoneSnapshot(Controls(1f,0f,1f),100000,100000,100000,true,true)
        val b=LanWire.input(1,1,1,key,snapshot,160001,false,false)
        val flags=ByteBuffer.wrap(b).order(ByteOrder.LITTLE_ENDIAN).getShort(6).toInt()
        assertEquals(0,flags and 6); assertEquals(8,flags and 8)
        val noCenter=LanWire.input(1,2,1,key,snapshot.copy(sensorReady=false),100010,false,false)
        assertEquals(0,ByteBuffer.wrap(noCenter).order(ByteOrder.LITTLE_ENDIAN).getShort(6).toInt() and 4)
    }
    @Test fun pairingRequiresPrivateAddressFullPinAndRandomToken() {
        val token="A".repeat(43);val pin="ab".repeat(32)
        val text="drivebench://pair?v=1&host=192.168.1.2&port=49174&pin=$pin&token=$token"
        assertEquals("192.168.1.2",PairInfo.parse(text).host)
        for(invalid in listOf(text.replace("192.168.1.2","8.8.8.8"),text+"&v=1",text.replace(pin,"abcd"),text.replace(token,"short"))) {
            try { PairInfo.parse(invalid);fail("Accepted invalid pairing") } catch (_: IllegalArgumentException) { }
        }
    }
}
