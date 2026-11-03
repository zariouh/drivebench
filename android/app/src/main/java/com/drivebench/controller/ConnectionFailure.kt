package com.drivebench.controller

enum class ConnectionStep(val label: String) {
    WIFI_SELECTION("Wi-Fi selection"), TLS_CONTEXT("Secure setup"), WIFI_BIND("Wi-Fi socket binding"),
    TCP_CONNECT("PC connection"), TLS_WRAP("Secure socket"), TLS_HANDSHAKE("Secure handshake"),
    PAIR_SEND("Pairing request"), PC_APPROVAL("PC approval"), SESSION_SETUP("Session setup"),
    UDP_BIND("Input socket binding"), INPUT_CHANNEL("Input channel"), CONTROL_READ("PC control reply"),
    CONTROL_WRITE("Phone control reply"), UDP_SEND("Input send"), UDP_FEEDBACK("Input feedback")
}

/** Only explicit stage labels and scrubbed exception text enter this report. Never log packets/QRs. */
class ConnectionTrace {
    private val gate=Any()
    private val events=ArrayDeque<String>()
    private var failure=""
    fun reset()=synchronized(gate) { events.clear();failure="" }
    fun event(text: String)=synchronized(gate) {
        if(events.size>=24) events.removeFirst()
        events.addLast(scrub(text).take(220))
    }
    fun fail(step: ConnectionStep,error: Throwable): String = synchronized(gate) {
        val causes=mutableListOf<Throwable>();var next: Throwable?=error
        while(next!=null && causes.size<6 && causes.none { it===next }) { causes+=next;next=next.cause }
        val deepest=causes.last()
        val detail=scrub(deepest.message ?: "No error text supplied")
        failure="${step.name}: ${deepest.javaClass.simpleName}: $detail".take(220)
        if(events.size>=24) events.removeFirst()
        events.addLast("FAILED ${step.name}")
        failure
    }
    fun report(version: String): String = synchronized(gate) {
        buildString {
            append("DriveBench Android ").append(scrub(version)).append('\n')
            append("Connection steps:\n");events.forEach { append("- ").append(it).append('\n') }
            if(failure.isNotEmpty()) append("Error: ").append(failure).append('\n')
        }.take(7000)
    }
    companion object {
        fun scrub(value: String): String = value
            .replace(Regex("drivebench://[^\\s]+",RegexOption.IGNORE_CASE),"[pairing text removed]")
            .replace(Regex("[A-Za-z0-9_+/=-]{40,}"),"[credential removed]")
            .replace(Regex("[\\r\\n\\t]+")," ")
    }
}
