package com.drivebench.controller

/** Native acquire/release adapters are injected so lifecycle/error behavior is JVM-testable. */
class ScopedWifiLease(private val acquire: ()->Boolean, private val release: ()->Unit) {
    private val gate=Any()
    private var foreground=false;private var connected=false;private var closed=false
    private var held=false;private var attempted=false;private var error: String?=null
    fun foreground(value: Boolean)=synchronized(gate) { foreground=value;update() }
    fun connected(value: Boolean)=synchronized(gate) { connected=value;update() }
    fun refreshConnected(value: ()->Boolean)=synchronized(gate) { connected=value();update() }
    fun close()=synchronized(gate) { closed=true;update() }
    private fun update() {
        val eligible=foreground && connected && !closed
        if(!eligible) {
            if(held) try { release();held=false } catch(e: RuntimeException) { error="release failed: ${e.javaClass.simpleName}" }
            attempted=false
        } else if(!held && !attempted) {
            attempted=true
            try { held=acquire();error=if(held) null else "not available" }
            catch(e: RuntimeException) { error="request failed: ${e.javaClass.simpleName}" }
        }
    }
    fun report(): String=synchronized(gate) {
        "Wi-Fi low-latency request: "+when {
            error!=null -> error
            held -> "held; platform/firmware effect unverified"
            else -> "released (foreground connected sessions only)"
        }
    }
}
