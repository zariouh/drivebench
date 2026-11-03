package com.drivebench.controller

import java.net.URI
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlin.math.roundToInt

data class PairInfo(val host: String, val port: Int, val pin: String, val token: String, val resume: Boolean = false) {
    companion object {
        fun parse(text: String): PairInfo {
            require(text.length <= 1024) { "Pairing text is too long" }
            val uri = URI(text.trim())
            require(uri.scheme == "drivebench" && uri.host == "pair" && uri.fragment == null)
            val pairs = (uri.rawQuery ?: "").split('&').map { it.split('=', limit = 2) }
            require(pairs.all { it.size == 2 } && pairs.map { it[0] }.distinct().size == pairs.size)
            val fields = pairs.associate { it[0] to it[1] }
            require(fields.keys == setOf("v", "host", "port", "pin", "token") && fields["v"] == "1")
            val host = fields.getValue("host")
            require(Ipv4.number(host)!=null) { "Use a numeric local IPv4 address" }
            val octets = host.split('.').map { it.toIntOrNull() ?: -1 }
            require(octets.size == 4 && octets.all { it in 0..255 } &&
                (octets[0] == 10 || (octets[0] == 172 && octets[1] in 16..31) || (octets[0] == 192 && octets[1] == 168))) { "Use the PC's local Wi-Fi address" }
            val port = fields.getValue("port").toInt(); require(port in 1024..65535)
            val pin = fields.getValue("pin").lowercase(); require(pin.matches(Regex("[0-9a-f]{64}")))
            val token = fields.getValue("token"); require(token.matches(Regex("[A-Za-z0-9_-]{43}")) && Base64.getUrlDecoder().decode(token).size == 32)
            return PairInfo(host, port, pin, token)
        }
    }
}

enum class DisabledReason { NONE, ENGINE_PAUSED, WINDOW_FOCUS, BACKGROUND, GYRO_UNCALIBRATED }

data class PhoneSnapshot(val controls: Controls = Controls(), val producedUs: Long = 0,
    val steeringUs: Long = 0, val sensorUs: Long = 0, val gyro: Boolean = false,
    val enabled: Boolean = false, val armGeneration: Long = 0, val sensorReady: Boolean = true, val disabledReason: DisabledReason = DisabledReason.NONE, val recoveryEpoch: Long = 0)

object LanWire {
    const val INPUT_SIZE = 88
    fun certificateMatches(encoded: ByteArray, pin: String): Boolean {
        if (!pin.matches(Regex("[0-9a-fA-F]{64}"))) return false
        val expected=pin.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        return MessageDigest.isEqual(MessageDigest.getInstance("SHA-256").digest(encoded),expected)
    }
    fun sign(bytes: ByteArray, prefix: Int, key: ByteArray) {
        require(key.size == 32 && bytes.size == prefix + 16)
        val mac = Mac.getInstance("HmacSHA256"); mac.init(SecretKeySpec(key, "HmacSHA256"))
        mac.update(bytes, 0, prefix); mac.doFinal().copyInto(bytes, prefix, 0, 16)
    }
    fun authentic(bytes: ByteArray, prefix: Int, key: ByteArray): Boolean {
        if (key.size != 32 || bytes.size != prefix + 16) return false
        val mac = Mac.getInstance("HmacSHA256"); mac.init(SecretKeySpec(key,"HmacSHA256")); mac.update(bytes,0,prefix)
        return MessageDigest.isEqual(mac.doFinal().copyOf(16), bytes.copyOfRange(prefix,bytes.size))
    }
    fun input(session: Long, sequence: Long, epoch: Long, key: ByteArray, snapshot: PhoneSnapshot,
              now: Long, armed: Boolean, stop: Boolean, suspend: Boolean = false): ByteArray {
        require(session > 0 && sequence > 0 && epoch in 1..0xffff_ffffL)
        val uiAge = (now-snapshot.producedUs).coerceAtLeast(0)
        val sensorAge = if (snapshot.gyro) (now-snapshot.sensorUs).coerceAtLeast(0) else 0
        val healthyUi = snapshot.producedUs > 0 && now >= snapshot.producedUs && uiAge <= 50_000
        val healthySensor = snapshot.sensorReady && snapshot.sensorUs > 0 && now >= snapshot.sensorUs && sensorAge <= 50_000
        var flags = (if (armed) 1 else 0) or (if (healthyUi) 2 else 0) or
            (if (snapshot.gyro && healthySensor) 4 else 0) or (if (snapshot.gyro) 8 else 0) or (if (stop) 16 else 0) or (if (suspend) 32 else 0)
        val output = if (armed && !stop && !suspend) snapshot.controls else Controls()
        fun axis(value: Float): Short = (value.takeIf { it.isFinite() } ?: 0f).coerceIn(-1f,1f).times(32767).roundToInt().toShort()
        fun pedal(value: Float): Short = (value.takeIf { it.isFinite() } ?: 0f).coerceIn(0f,1f).times(65535).roundToInt().toShort()
        val b = ByteBuffer.allocate(INPUT_SIZE).order(ByteOrder.LITTLE_ENDIAN)
        b.put(byteArrayOf(0x44,0x52,0x43,0x31)); b.put(1); b.put(1); b.putShort(flags.toShort())
        b.putLong(session); b.putLong(sequence); b.putLong(now)
        b.putLong(snapshot.steeringUs.takeIf { it in 1..now } ?: 1); b.putInt(epoch.toInt()); b.putInt(0)
        b.putShort(axis(output.steering)); b.putShort(0); b.putShort(0); b.putShort(0)
        b.putShort(pedal(output.brake)); b.putShort(pedal(output.throttle))
        b.putInt(sensorAge.coerceAtMost(0xffff_ffffL).toInt()); b.putInt(uiAge.coerceAtMost(0xffff_ffffL).toInt()); b.putInt(0)
        return b.array().also { sign(it,72,key) }
    }
    data class Feedback(val flags: Int, val sequence: Long, val echoed: Long, val echoedUs: Long,
                        val receivedUs: Long, val sentUs: Long, val applied: Long)
    fun feedback(bytes: ByteArray, session: Long, key: ByteArray): Feedback? {
        if (bytes.size != 80 || !bytes.copyOfRange(0,4).contentEquals(byteArrayOf(0x44,0x52,0x43,0x31)) ||
            bytes[4] != 1.toByte() || bytes[5] != 2.toByte() || !authentic(bytes,64,key)) return null
        val b=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN); val flags=b.getShort(6).toInt() and 65535
        if (flags and 63 != flags || b.getLong(8) != session) return null
        val f=Feedback(flags,b.getLong(16),b.getLong(24),b.getLong(32),b.getLong(40),b.getLong(48),b.getLong(56))
        return f.takeIf { it.sequence>0 && it.echoed>0 && it.echoedUs>0 && it.receivedUs>0 && it.sentUs>=it.receivedUs && it.applied>=0 }
    }
}
