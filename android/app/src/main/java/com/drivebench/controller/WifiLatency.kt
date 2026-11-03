package com.drivebench.controller

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build

/** A request, not proof of firmware activation. Local-only Wi-Fi remains usable if it fails. */
class WifiLatency(context: Context, private val isConnected: ()->Boolean) {
    private val manager=runCatching { context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager }.getOrNull()
    private var lock: WifiManager.WifiLock?=null
    private val lease=ScopedWifiLease({
        if(Build.VERSION.SDK_INT<29 || manager==null) false else {
            val value=lock ?: manager.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY,"DriveBench:controller").also {
                it.setReferenceCounted(false);lock=it
            }
            value.acquire();value.isHeld
        }
    }, { lock?.let { if(it.isHeld) it.release() } })
    fun foreground(value: Boolean)=lease.foreground(value)
    fun connectionChanged()=lease.refreshConnected(isConnected)
    fun close()=lease.close()
    fun report()=lease.report()
}
