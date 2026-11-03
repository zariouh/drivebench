package com.drivebench.controller

import kotlin.math.max

class LinkTiming {
    private val gate = Any()
    private var wakeCount = 0L
    private var wakeUsSum = 0L
    private var maxWakeUs = 0L
    private var sendCount = 0L
    private var sendUsSum = 0L
    private var maxSendUs = 0L
    private var echoCount = 0L
    private var echoUsSum = 0L
    private var maxEchoUs = 0L
    private var pcCount = 0L
    private var pcUsSum = 0L
    private var maxPcUs = 0L
    private val pauses = ArrayDeque<String>()
    private var arms = 0

    fun beginArm() = synchronized(gate) {
        arms++
    }

    fun markPoll(nowUs: Long, snapshot: PhoneSnapshot) = wake(nowUs, snapshot)

    fun wake(nowUs: Long, snapshot: PhoneSnapshot) = synchronized(gate) {
        // Kept for backward compatibility or direct calls
    }

    fun sent(startUs: Long, endUs: Long) = synchronized(gate) {
        val duration = max(0L, endUs - startUs)
        sendCount++
        sendUsSum += duration
        maxSendUs = max(maxSendUs, duration)
    }

    fun feedback(nowUs: Long, echoedUs: Long, receivedUs: Long, sentUs: Long) = synchronized(gate) {
        val echo = max(0L, nowUs - echoedUs)
        echoCount++
        echoUsSum += echo
        maxEchoUs = max(maxEchoUs, echo)

        val pc = max(0L, sentUs - receivedUs)
        pcCount++
        pcUsSum += pc
        maxPcUs = max(maxPcUs, pc)
    }

    fun pause(nowUs: Long, reason: String, phase: DrivePhase, armGeneration: Long, commandId: Long) = synchronized(gate) {
        if (pauses.size >= 16) pauses.removeFirst()
        pauses.addLast("${nowUs / 1000} ms: $reason (phase=$phase, gen=$armGeneration, cmd=$commandId)")
    }

    fun getElapsedTime(): Long = 0L

    fun report(nowUs: Long): String = synchronized(gate) {
        val avgWake = if (wakeCount > 0) wakeUsSum / wakeCount else 0
        val avgSend = if (sendCount > 0) sendUsSum / sendCount else 0
        val avgEcho = if (echoCount > 0) echoUsSum / echoCount else 0
        val avgPc = if (pcCount > 0) pcUsSum / pcCount else 0
        buildString {
            append("Link Timing Report (arms=$arms):")
            append("\n- wake avg: ${avgWake}us (max: ${maxWakeUs}us)")
            append("\n- send avg: ${avgSend}us (max: ${maxSendUs}us)")
            append("\n- echo elapsed avg: ${avgEcho}us (max: ${maxEchoUs}us)")
            append("\n- PC residence avg: ${avgPc}us (max: ${maxPcUs}us)")
            if (pauses.isNotEmpty()) {
                append("\nPauses and Transitions:")
                for (p in pauses) {
                    append("\n- $p")
                }
            }
        }
    }
}
