package com.drivebench.controller

enum class DrivePhase { PAUSED, STOP_PENDING, ARM_PENDING, DRIVING, RECOVERING }
val DrivePhase.hasIntent get()=this==DrivePhase.ARM_PENDING || this==DrivePhase.DRIVING || this==DrivePhase.RECOVERING
private val PauseCause.recoverable get()=this==PauseCause.UI_DELAY || this==PauseCause.GYRO_DELAY || this==PauseCause.FEEDBACK_DELAY
data class LinkCommand(val id: Long = 0, val name: String = "none", val gyro: Boolean = false)
data class ControlAck(val armed: Boolean, val ready: Boolean, val sync: Boolean, val epoch: Long,
    val command: Long, val reason: String, val real: Boolean, val recovering: Boolean = false)
data class SendPlan(val epoch: Long, val armed: Boolean, val stop: Boolean,
    val disconnect: String? = null, val suspend: Boolean = false, val phase: DrivePhase? = null)

/** Serializes intent and authenticated PC state. No socket/UI work under this lock. */
class SessionCoordinator(private val pauses: PauseHistory, private val timing: LinkTiming = LinkTiming()) {
    companion object { const val RECOVERY_WINDOW_US=1_000_000L;const val RECOVERY_STABLE_US=500_000L }
    private val gate=Any()
    @Volatile var view=LinkView(); private set
    private var command=LinkCommand()
    private var lastAck=0L
    private var closed=false
    private var pendingSince=0L
    private var requestedEpoch=0L
    private var grantedSince=0L
    private var recoverySince=0L
    private var challengeObserved=0L
    private var recoveryBaseEpoch=0L
    private var pcRecovering=false
    private var neutralAt=0L;private var neutralEpoch=0L;private var lastEcho=0L
    private data class SentInput(val plan: SendPlan, val sentUs: Long)
    private val sentPlans=LinkedHashMap<Long,SentInput>()
    fun stage(stage: LinkStage, reason: String)=synchronized(gate) {
        if(!closed) view=view.copy(stage=stage,reason=reason)
    }
    fun connected()=synchronized(gate) {
        if(!closed) view=LinkView(connected=true,reason="Paired · preparing the controls…",stage=LinkStage.SYNCHRONIZING)
    }
    fun command(): LinkCommand=synchronized(gate) { command }
    fun canArm(feedbackFresh: Boolean, now: Long)=synchronized(gate) {
        refreshNeutral(now);ConnectionPolicy.canArm(view,feedbackFresh)
    }
    private fun refreshNeutral(now: Long) {
        val confirmed=!view.armed && neutralEpoch==view.epoch && neutralAt>0 && now-neutralAt in 0..100_000
        if(view.neutralConfirmed!=confirmed) view=view.copy(neutralConfirmed=confirmed)
    }
    fun sending(sequence: Long, plan: SendPlan, sentUs: Long)=synchronized(gate) {
        if(!closed && plan.epoch==view.epoch && (plan.phase==null || plan.phase==view.phase)) {
            sentPlans[sequence]=SentInput(plan,sentUs)
            if(sentPlans.size>128) sentPlans.remove(sentPlans.keys.first())
            true
        } else false
    }
    fun feedback(echoed: Long, flags: Int, now: Long)=synchronized(gate) {
        if(closed) return@synchronized
        sentPlans[echoed]?.takeIf { echoed>lastEcho }?.let { input ->
            val sent=input.plan;lastEcho=echoed
            if(!sent.armed && !sent.stop && !sent.suspend && sent.epoch==view.epoch && flags and 33==0 && now-input.sentUs in 0..100_000) {
                neutralEpoch=sent.epoch;neutralAt=input.sentUs
            }
        }
        refreshNeutral(now)
    }
    fun arm(gyro: Boolean, feedbackFresh: Boolean, now: Long): Boolean=synchronized(gate) {
        if(closed || !canArm(feedbackFresh,now)) return@synchronized false
        command=LinkCommand(command.id+1,"arm",gyro);requestedEpoch=view.epoch;pendingSince=now
        pauses.resume();timing.beginArm();pcRecovering=false
        view=view.copy(phase=DrivePhase.ARM_PENDING,releaseConfirmed=false,reason="Arming · keep controls neutral")
        true
    }
    fun stop(cause: PauseCause, now: Long, detail: String="")=synchronized(gate) {
        if(closed) return@synchronized
        if(view.phase.hasIntent || view.armed)
            capture(cause.message,now,detail)
        if(command.name!="stop" || view.phase!=DrivePhase.STOP_PENDING) {
            command=LinkCommand(command.id+1,"stop");pendingSince=now
        }
        pcRecovering=false
        view=view.copy(phase=DrivePhase.STOP_PENDING,releaseConfirmed=false,reason="Releasing controls · waiting for PC acknowledgement")
    }
    fun suspend(cause: PauseCause, now: Long, detail: String="")=synchronized(gate) {
        if(closed || view.phase!=DrivePhase.DRIVING) return@synchronized
        recoverySince=now;recoveryBaseEpoch=view.epoch;challengeObserved=0;pcRecovering=false
        pauses.transient(cause.message,now,detail);timing.pause(now,"Temporary neutral: "+cause.message,view.phase,view.armGeneration,command.id)
        command=LinkCommand(command.id+1,"suspend",command.gyro)
        view=view.copy(phase=DrivePhase.RECOVERING,releaseConfirmed=false,reason="Recovering · output neutral")
    }
    fun ack(ack: ControlAck, now: Long)=synchronized(gate) {
        if(closed) return@synchronized
        require(ack.epoch in 1..0xffff_ffffL && ack.epoch>=view.epoch)
        require(ack.command in lastAck..command.id)
        lastAck=ack.command
        var phase=view.phase;var confirmed=false;var generation=view.armGeneration
        when(phase) {
            DrivePhase.STOP_PENDING -> if(!ack.armed && !ack.recovering && ack.command>=command.id) {
                phase=DrivePhase.PAUSED;confirmed=true
            }
            DrivePhase.ARM_PENDING -> if(ack.command>=command.id) {
                if(ack.armed || ack.recovering) {
                    require(ack.epoch>requestedEpoch)
                    generation++;grantedSince=now
                    if(ack.recovering) {
                        phase=DrivePhase.RECOVERING;recoverySince=now;recoveryBaseEpoch=requestedEpoch
                        challengeObserved=now;pcRecovering=true
                        pauses.transient("PC: "+ack.reason,now);timing.pause(now,"Temporary neutral: "+ack.reason,view.phase,view.armGeneration,command.id)
                    } else phase=DrivePhase.DRIVING
                } else {
                    capture("PC: "+ack.reason,now);phase=DrivePhase.PAUSED;confirmed=true
                }
            }
            DrivePhase.DRIVING -> when {
                ack.recovering -> {
                    require(ack.epoch>view.epoch)
                    recoverySince=now;recoveryBaseEpoch=view.epoch;challengeObserved=now;pcRecovering=true
                    pauses.transient("PC: "+ack.reason,now);timing.pause(now,"Temporary neutral: "+ack.reason,view.phase,view.armGeneration,command.id)
                    phase=DrivePhase.RECOVERING
                }
                !ack.armed -> { capture("PC: "+ack.reason,now);phase=DrivePhase.PAUSED;confirmed=true }
                else -> require(ack.epoch==view.epoch)
            }
            DrivePhase.RECOVERING -> {
                if(ack.recovering && ack.epoch>recoveryBaseEpoch) {
                    if(!pcRecovering || ack.epoch!=view.epoch) challengeObserved=now
                    pcRecovering=true
                } else if(ack.command>=command.id) {
                    if(ack.armed && pcRecovering && ack.epoch>recoveryBaseEpoch) {
                        phase=DrivePhase.DRIVING;pcRecovering=false;pauses.recovered(now)
                    } else if(!ack.armed) {
                        capture("PC: "+ack.reason,now);phase=DrivePhase.PAUSED;confirmed=true;pcRecovering=false
                    }
                }
            }
            DrivePhase.PAUSED -> if(!ack.armed && !ack.recovering) confirmed=ack.command>=command.id
        }
        // Stop/background/cancel wins even against a late recovery grant.
        if(phase==DrivePhase.PAUSED && (ack.armed || ack.recovering)) {
            command=LinkCommand(command.id+1,"stop");pendingSince=now;phase=DrivePhase.STOP_PENDING
        }
        val reason=when(phase) {
            DrivePhase.ARM_PENDING -> "Arming · waiting for PC acknowledgement"
            DrivePhase.STOP_PENDING -> "Releasing controls · waiting for PC acknowledgement"
            DrivePhase.RECOVERING -> "Recovering · output neutral"
            else -> ack.reason
        }
        view=LinkView(true,ack.armed,ack.ready,ack.sync,ack.epoch,generation,reason,ack.real,
            if(phase==DrivePhase.DRIVING) LinkStage.DRIVING else if(ack.ready && ack.sync) LinkStage.READY else LinkStage.SYNCHRONIZING,
            phase,confirmed,false)
        refreshNeutral(now)
    }
    fun plan(snapshot: PhoneSnapshot, now: Long, feedbackUs: Long): SendPlan?=synchronized(gate) {
        if(closed || !view.connected) return@synchronized null
        refreshNeutral(now)
        if((view.phase==DrivePhase.STOP_PENDING || view.phase==DrivePhase.ARM_PENDING) && now-pendingSince>2_000_000) {
            capture("PC control acknowledgement timed out",now)
            return@synchronized SendPlan(view.epoch,false,true,"PC control acknowledgement timed out · Reconnect, then Resume")
        }
        val health=SenderHealth.interruption(snapshot,now,feedbackUs)
        if(view.phase==DrivePhase.DRIVING || view.phase==DrivePhase.RECOVERING) {
            // Lifecycle disable takes precedence over stale UI/feedback classification.
            if(health!=null && !health.recoverable)
                stop(health,now,SenderHealth.detail(health,snapshot,now,feedbackUs))
            else if(view.phase==DrivePhase.DRIVING) {
                if(snapshot.armGeneration!=view.armGeneration) {
                    if(now-grantedSince<=50_000) return@synchronized null
                    suspend(PauseCause.UI_DELAY,now,"fresh control generation not published in 50 ms")
                } else if(health!=null) suspend(health,now,SenderHealth.detail(health,snapshot,now,feedbackUs))
            }
        }
        if(view.phase==DrivePhase.RECOVERING) {
            if(now-recoverySince>=RECOVERY_WINDOW_US) stop(PauseCause.RECOVERY_TIMEOUT,now)
            else {
                // Only the input owner can publish the new epoch. Never relabel a cached snapshot here.
                val fresh=pcRecovering && health==null && snapshot.armGeneration==view.armGeneration &&
                    snapshot.recoveryEpoch==view.epoch && snapshot.producedUs>challengeObserved
                return@synchronized SendPlan(view.epoch,fresh,false,suspend=!fresh,phase=view.phase)
            }
        }
        val active=view.phase==DrivePhase.DRIVING && view.armed
        SendPlan(view.epoch,active,view.phase==DrivePhase.STOP_PENDING,phase=view.phase)
    }
    fun close()=synchronized(gate) {
        closed=true;sentPlans.clear();pcRecovering=false;view=view.copy(connected=false,armed=false,phase=DrivePhase.PAUSED,releaseConfirmed=false)
    }
    private fun capture(message: String, now: Long, detail: String="") {
        if(pauses.capture(message,now,detail)) timing.pause(now,message,view.phase,view.armGeneration,command.id)
    }
}
