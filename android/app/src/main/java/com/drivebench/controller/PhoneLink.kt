package com.drivebench.controller

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.LinkProperties
import android.os.Build
import android.os.SystemClock
import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.*
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.Base64
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import javax.net.ssl.*

/** Lifecycle-scoped sockets. The worker never renews a stale UI/sensor snapshot. */
class PhoneLink(context: Context) {
    private val connectivity = context.applicationContext.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    @Volatile private var connectionView = LinkView()
    val view get() = current?.state?.view ?: connectionView
    @Volatile var snapshot = PhoneSnapshot()
    @Volatile private var current: Session? = null
    @Volatile private var remembered: PairInfo? = null // process memory only; never backed up or logged
    val canReconnect get() = remembered != null
    val wantsDriving get() = view.phase==DrivePhase.ARM_PENDING || view.phase==DrivePhase.DRIVING || view.phase==DrivePhase.RECOVERING
    private val attempt = AtomicLong()
    private val connectionGate = Any()
    private val wifiChanged = Object()
    @Volatile private var trace = ConnectionTrace()
    private val pauses=PauseHistory()
    private val wifi=WifiLatency(context) { current?.state?.view?.connected==true }
    @Volatile private var lastTiming=""
    @Volatile private var pcVersion="not reported by this receiver"
    fun foreground(value: Boolean) = wifi.foreground(value)
    val pauseReason get()=pauses.reason
    fun connectionReport(): String {
        val history=pauses.report()
        val timing="Timing scope: current traffic session; pause history can include earlier sessions\n"+(current?.timing?.report(now()) ?: lastTiming)
        return (trace.report(BuildConfig.VERSION_NAME)+"\nPC receiver version: $pcVersion\n"+wifi.report()+"\n"+history+"\n"+timing).take(12_000)
    }
    private class Session(val info: PairInfo, val network: Network, val trace: ConnectionTrace, pauses: PauseHistory) {
        @Volatile var step=ConnectionStep.TLS_CONTEXT
        @Volatile var alive = true
        @Volatile var raw: Socket? = null
        @Volatile var tls: SSLSocket? = null
        @Volatile var udp: DatagramSocket? = null
        val timing=LinkTiming()
        val state=SessionCoordinator(pauses,timing)
        @Volatile var lastFeedbackUs = 0L
        var inputKey = ByteArray(0); var statusKey = ByteArray(0); var id = 0L
        val sender = Executors.newSingleThreadScheduledExecutor()
        fun close() { alive=false; state.close(); sender.shutdownNow(); try { udp?.close() } catch (_: Exception) {}; try { raw?.close() } catch (_: Exception) {} }
    }
    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) { synchronized(wifiChanged) { wifiChanged.notifyAll() } }
        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) { synchronized(wifiChanged) { wifiChanged.notifyAll() } }
        override fun onLinkPropertiesChanged(network: Network, properties: LinkProperties) { synchronized(wifiChanged) { wifiChanged.notifyAll() } }
        override fun onLost(network: Network) { current?.takeIf { it.network == network }?.let { end(it,"Wi-Fi changed · Reconnect, then Resume") } }
    }
    init {
        connectivity.registerNetworkCallback(NetworkRequest.Builder()
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_TRUSTED)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED)
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(),callback)
    }
    private fun feedbackFresh(s: Session) = s.lastFeedbackUs>0 && now()-s.lastFeedbackUs in 0..150_000
    val feedbackFresh get() = current?.let { feedbackFresh(it) } ?: false
    val canRequestArm get() = current?.let { it.state.canArm(feedbackFresh(it),now()) } ?: false
    fun publish(value: PhoneSnapshot) { snapshot=value }
    fun connect(info: PairInfo) {
        disconnect("Finding the Wi-Fi connection to your PC…")
        val attemptTrace=ConnectionTrace();trace=attemptTrace;pauses.resume()
        attemptTrace.event("PC ${info.host}:${info.port}; ${if(info.resume) "reconnect" else "new pairing"}")
        val generation=attempt.get()
        connectionView=LinkView(reason="Finding the Wi-Fi connection to your PC…",stage=LinkStage.FINDING_WIFI)
        Thread({
            try {
                val activeCapabilities=runCatching { connectivity.activeNetwork?.let { connectivity.getNetworkCapabilities(it) } }.getOrNull()
                attemptTrace.event("Default route: Wi-Fi=${activeCapabilities?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)==true}, cellular=${activeCapabilities?.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)==true}, VPN=${activeCapabilities?.hasTransport(NetworkCapabilities.TRANSPORT_VPN)==true}")
                val until=now()+5_000_000
                var network: Network?=null
                while(attempt.get()==generation && now()<until) {
                    network=selectWifi(info.host)
                    if(network!=null) break
                    synchronized(wifiChanged) { wifiChanged.wait(200) }
                }
                synchronized(connectionGate) {
                    if(attempt.get()!=generation) return@Thread
                    if(network==null) {
                        connectionView=LinkView(reason="Android has not made a Wi-Fi connection available to this app yet. Tap Connect to retry.",stage=LinkStage.FAILED)
                        return@Thread
                    }
                    val addresses=runCatching { connectivity.getLinkProperties(network)?.linkAddresses.orEmpty().filter { it.address is Inet4Address }.joinToString { "${it.address.hostAddress}/${it.prefixLength}" } }.getOrDefault("unavailable")
                    attemptTrace.event("Selected Wi-Fi; local IPv4=$addresses")
                    val s=Session(info,network!!,attemptTrace,pauses);current=s;snapshot=PhoneSnapshot()
                }
                current?.takeIf { attempt.get()==generation }?.let { run(it) }
            } catch (error: Exception) {
                synchronized(connectionGate) { if(attempt.get()==generation)
                    connectionView=LinkView(reason=attemptTrace.fail(ConnectionStep.WIFI_SELECTION,error),stage=LinkStage.FAILED) }
            }
        },"DriveBenchControl").start()
    }
    @Suppress("DEPRECATION") // One snapshot per selection attempt, with callback-driven change notifications.
    private fun selectWifi(pc: String): Network? {
        val active=connectivity.activeNetwork
        val candidates=connectivity.allNetworks.mapNotNull { network ->
            val capabilities=connectivity.getNetworkCapabilities(network) ?: return@mapNotNull null
            val prefixes=connectivity.getLinkProperties(network)?.linkAddresses.orEmpty().mapNotNull {
                if(it.address is Inet4Address) Ipv4Prefix(it.address.hostAddress ?: return@mapNotNull null,it.prefixLength) else null
            }
            WifiCandidate(network,capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI),
                capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN),network==active,prefixes)
        }
        return WifiSelection.choose(candidates,pc)
    }
    fun reconnect() { remembered?.let { connect(it) } }
    fun arm(gyro: Boolean): Boolean {
        val s=current ?: return false
        return s.alive && current===s && s.state.arm(gyro,feedbackFresh(s),now())
    }
    fun stop(cause: PauseCause=PauseCause.STOP_BUTTON) { current?.let { stop(it,cause) } }
    private fun stop(s: Session, cause: PauseCause, detail: String="") {
        if(current===s && s.alive) s.state.stop(cause,now(),detail)
    }
    fun suspend(cause: PauseCause) { current?.state?.suspend(cause,now()) }
    fun disconnect(reason: String = "Disconnected · Tap Reconnect", stage: LinkStage = LinkStage.DISCONNECTED) {
        val old=synchronized(connectionGate) { attempt.incrementAndGet();val previous=current;previous?.state?.close();current=null;connectionView=LinkView(reason=reason,stage=stage);previous }
        synchronized(wifiChanged) { wifiChanged.notifyAll() };old?.let { lastTiming=it.timing.report(now());it.close() };wifi.connectionChanged()
    }
    fun destroy() { disconnect();wifi.close();connectivity.unregisterNetworkCallback(callback) }
    private fun end(s: Session, reason: String) {
        s.close()
        synchronized(connectionGate) { if (current === s) { lastTiming=s.timing.report(now());current=null; connectionView=LinkView(reason=reason,stage=LinkStage.FAILED) } };wifi.connectionChanged()
    }
    private fun stage(s: Session, value: LinkStage, reason: String) {
        synchronized(connectionGate) { if(current===s && s.alive) s.state.stage(value,reason) }
    }
    private fun step(s: Session,value: ConnectionStep) {
        s.step=value
        if(current===s && s.alive) s.trace.event(value.name)
    }
    private fun now()=SystemClock.elapsedRealtimeNanos()/1000
    private fun run(s: Session) {
        try {
            step(s,ConnectionStep.TLS_CONTEXT)
            val trust=object : X509TrustManager {
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
                override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) { throw java.security.cert.CertificateException("Client certificates are not used") }
                override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                    val leaf=chain?.firstOrNull() ?: throw java.security.cert.CertificateException("Missing receiver certificate")
                    if (!LanWire.certificateMatches(leaf.encoded,s.info.pin))
                        throw java.security.cert.CertificateException("Receiver identity changed · Scan a fresh QR")
                    leaf.checkValidity()
                }
            }
            val ssl=SSLContext.getInstance("TLS").apply { init(null,arrayOf(trust),SecureRandom()) }
            val raw=Socket(); s.raw=raw; if (!s.alive) throw IOException("Cancelled")
            stage(s,LinkStage.CONNECTING,"Preparing the Wi-Fi connection to your PC…")
            step(s,ConnectionStep.WIFI_BIND)
            s.network.bindSocket(raw); raw.tcpNoDelay=true; raw.soTimeout=30_000
            stage(s,LinkStage.CONNECTING,"Connecting to PC ${s.info.host}…")
            step(s,ConnectionStep.TCP_CONNECT)
            raw.connect(InetSocketAddress(s.info.host,s.info.port),5000)
            stage(s,LinkStage.SECURING,"Verifying the PC connection…")
            step(s,ConnectionStep.TLS_WRAP)
            val tls=ssl.socketFactory.createSocket(raw,s.info.host,s.info.port,true) as SSLSocket; s.tls=tls
            tls.enabledProtocols=tls.supportedProtocols.filter { it=="TLSv1.2" || it=="TLSv1.3" }.toTypedArray()
            step(s,ConnectionStep.TLS_HANDSHAKE)
            tls.startHandshake()
            val input=DataInputStream(tls.inputStream); val output=DataOutputStream(tls.outputStream)
            step(s,ConnectionStep.PAIR_SEND)
            write(output,JSONObject().put("v",1).put("recovery",2).put("token",s.info.token).put("resume",s.info.resume).put("name",Build.MODEL))
            stage(s,LinkStage.APPROVAL,if(s.info.resume) "Reconnecting to your PC…" else "Waiting for approval on the PC · choose Yes in its pairing prompt")
            step(s,ConnectionStep.PC_APPROVAL)
            val welcome=read(input)
            step(s,ConnectionStep.SESSION_SETUP)
            require(welcome.getString("type")=="welcome" && welcome.getInt("v")==1 &&
                welcome.getInt("inputBytes")==88 && welcome.getInt("statusBytes")==80 && welcome.getInt("inputHz")==60 && welcome.optInt("recovery")==2 &&
                welcome.getLong("recoveryWindowUs")==SessionCoordinator.RECOVERY_WINDOW_US &&
                welcome.getLong("recoveryStableUs")==SessionCoordinator.RECOVERY_STABLE_US) { "Install the matching PC receiver with automatic recovery support" }
            s.id=welcome.getLong("session"); require(s.id>0)
            pcVersion=welcome.optString("receiverVersion").takeIf { it.matches(Regex("[0-9]+[.][0-9]+[.][0-9]+")) } ?: "not reported by this receiver"
            s.inputKey=Base64.getDecoder().decode(welcome.getString("inputKey")); s.statusKey=Base64.getDecoder().decode(welcome.getString("statusKey"))
            require(s.inputKey.size==32 && s.statusKey.size==32)
            val port=welcome.getInt("inputPort"); require(port in 1024..65535)
            val credential=welcome.getString("credential"); require(credential.matches(Regex("[A-Za-z0-9_-]{43}")))
            if (!s.alive || current !== s) throw IOException("Cancelled")
            remembered=s.info.copy(token=credential,resume=true)
            step(s,ConnectionStep.UDP_BIND)
            val udp=DatagramSocket(); s.udp=udp; s.network.bindSocket(udp)
            udp.connect(InetSocketAddress(s.info.host,port)); udp.soTimeout=250
            synchronized(connectionGate) {
                if(current!==s || !s.alive) throw IOException("Cancelled")
                s.state.connected()
            }
            wifi.connectionChanged()
            tls.soTimeout=2500
            step(s,ConnectionStep.INPUT_CHANNEL)
            startDatagrams(s)
            while (s.alive) {
                s.step=ConnectionStep.CONTROL_READ
                val tick=read(input); val phone2=now(); require(tick.getString("type")=="tick")
                val command=s.state.command()
                s.step=ConnectionStep.CONTROL_WRITE
                write(output,JSONObject().put("phone2",phone2).put("phone3",now()).put("command",command.name).put("request",command.id).put("gyro",command.gyro))
                s.step=ConnectionStep.CONTROL_READ
                val ack=read(input); require(ack.getString("type")=="ack")
                if (!s.alive || current !== s) break
                s.state.ack(ControlAck(ack.getBoolean("armed"),ack.getBoolean("ready"),ack.getBoolean("sync"),
                    ack.getLong("epoch"),ack.getLong("command"),ack.getString("reason"),ack.getBoolean("real"),ack.getBoolean("recovering")),now())
            }
        } catch (error: Exception) {
            if (s.alive) {
                end(s,s.trace.fail(s.step,error))
            }
        } finally { s.close(); s.inputKey.fill(0); s.statusKey.fill(0) }
    }
    private fun startDatagrams(s: Session) {
        var sequence=0L
        s.sender.scheduleWithFixedDelay({
            try {
                if (!s.alive || current !== s) return@scheduleWithFixedDelay
                val p=snapshot; val time=now();s.timing.wake(time,p)
                val plan=s.state.plan(p,time,s.lastFeedbackUs) ?: return@scheduleWithFixedDelay
                plan.disconnect?.let { end(s,it);return@scheduleWithFixedDelay }
                val bytes=LanWire.input(s.id,++sequence,plan.epoch,s.inputKey,p,time,plan.armed,plan.stop,plan.suspend)
                if(!s.state.sending(sequence,plan,time)) return@scheduleWithFixedDelay
                if(!s.alive || current!==s) return@scheduleWithFixedDelay
                val start=now();s.udp!!.send(DatagramPacket(bytes,bytes.size));s.timing.sent(start,now())
            } catch (error: Exception) { if (s.alive) end(s,s.trace.fail(ConnectionStep.UDP_SEND,error)) }
        },0,16_667,TimeUnit.MICROSECONDS)
        Thread({
            var last=0L
            val buffer=ByteArray(256)
            while (s.alive) {
                try {
                    val datagram=DatagramPacket(buffer,buffer.size); s.udp!!.receive(datagram)
                    val feedback=LanWire.feedback(buffer.copyOf(datagram.length),s.id,s.statusKey) ?: continue
                    if (feedback.sequence<=last) continue
                    if(!s.alive || current!==s) continue
                    last=feedback.sequence; val time=now();s.lastFeedbackUs=time
                    s.timing.feedback(time,feedback.echoedUs,feedback.receivedUs,feedback.sentUs)
                    s.state.feedback(feedback.echoed,feedback.flags,time)
                } catch (_: SocketTimeoutException) { /* sender enforces feedback freshness while armed */ }
                catch (error: Exception) { if (s.alive) end(s,s.trace.fail(ConnectionStep.UDP_FEEDBACK,error)); break }
            }
        },"DriveBenchFeedback").start()
    }
    private fun read(input: DataInputStream): JSONObject {
        val length=input.readUnsignedShort(); require(length in 2..4096)
        val bytes=ByteArray(length); input.readFully(bytes)
        return JSONObject(String(bytes,Charsets.UTF_8))
    }
    private fun write(output: DataOutputStream, value: JSONObject) {
        val bytes=value.toString().toByteArray(Charsets.UTF_8); require(bytes.size in 2..4096)
        output.writeShort(bytes.size); output.write(bytes); output.flush()
    }
}
