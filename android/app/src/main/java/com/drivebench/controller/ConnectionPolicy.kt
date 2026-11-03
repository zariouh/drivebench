package com.drivebench.controller

enum class LinkStage { DISCONNECTED, FINDING_WIFI, CONNECTING, SECURING, APPROVAL, SYNCHRONIZING, READY, DRIVING, FAILED }

data class LinkView(val connected: Boolean = false, val armed: Boolean = false, val ready: Boolean = false,
    val sync: Boolean = false, val epoch: Long = 1, val armGeneration: Long = 0,
    val reason: String = "Tap Connect to pair with your PC", val real: Boolean = false,
    val stage: LinkStage = LinkStage.DISCONNECTED, val phase: DrivePhase = DrivePhase.PAUSED,
    val releaseConfirmed: Boolean = false, val neutralConfirmed: Boolean = false)

data class Ipv4Prefix(val address: String, val length: Int) {
    fun matches(target: String): Boolean {
        val local=Ipv4.number(address) ?: return false
        val remote=Ipv4.number(target) ?: return false
        if(length !in 0..32) return false
        val mask=if(length==0) 0L else (0xffff_ffffL shl (32-length)) and 0xffff_ffffL
        return (local and mask) == (remote and mask)
    }
}

object Ipv4 {
    fun number(address: String): Long? {
        val parts=address.split('.')
        if(parts.size!=4) return null
        val octets=parts.map { it.toIntOrNull() ?: return null }
        if(octets.any { it !in 0..255 } || octets.joinToString(".")!=address) return null
        return octets.fold(0L) { value,octet -> (value shl 8) or octet.toLong() }
    }
}

data class WifiCandidate<T>(val network: T, val wifi: Boolean, val vpn: Boolean = false,
    val default: Boolean = false, val prefixes: List<Ipv4Prefix> = emptyList())

/** Internet validation/default routing are not requirements for a local Wi-Fi controller. */
object WifiSelection {
    fun <T> choose(candidates: List<WifiCandidate<T>>, pc: String): T? = candidates
        .filter { it.wifi && !it.vpn }
        .maxWithOrNull(compareBy<WifiCandidate<T>> { candidate ->
            candidate.prefixes.filter { it.matches(pc) }.maxOfOrNull { it.length } ?: -1
        }.thenBy { if(it.default) 1 else 0 })?.network
}

object ConnectionPolicy {
    private val resumeInstruction=Regex("\\s*[—·]?\\s*tap Resume",RegexOption.IGNORE_CASE)
    fun pauseMessage(reason: String, presentation: Presentation): String {
        val cause=reason.replace(resumeInstruction,"").trim()
        val next=if(presentation.enabled && presentation.action=="RESUME") "Tap Resume" else presentation.message.replace(resumeInstruction,"").trim()
        return "Paused: $cause · $next"
    }
    fun canArm(view: LinkView, feedbackFresh: Boolean) = view.connected && !view.armed && view.ready && view.sync && feedbackFresh &&
        view.phase==DrivePhase.PAUSED && view.releaseConfirmed && view.neutralConfirmed
    fun heading(view: LinkView): String = when {
        view.connected -> if(view.ready && !view.real) "PC · DRY RUN" else "PC · WI-FI"
        view.stage in setOf(LinkStage.FINDING_WIFI,LinkStage.CONNECTING,LinkStage.SECURING,LinkStage.APPROVAL) -> "CONNECTING"
        else -> "NOT CONNECTED"
    }
    data class Presentation(val heading: String, val message: String, val action: String, val enabled: Boolean)
    fun present(view: LinkView, paused: Boolean, feedbackFresh: Boolean): Presentation {
        val allowed=canArm(view,feedbackFresh)
        val message=when {
            !view.connected -> view.reason
            view.phase==DrivePhase.RECOVERING -> "Recovering · output neutral; keep controlling normally"
            !view.sync || !view.ready -> view.reason
            !feedbackFresh -> "Paired · waiting for the PC input channel…"
            !view.releaseConfirmed || !view.neutralConfirmed -> "Waiting for acknowledged neutral controls…"
            else -> view.reason
        }
        val action=when {
            !view.connected -> "CONNECT FIRST"
            !paused -> "STOP"
            !allowed -> "WAITING…"
            else -> "RESUME"
        }
        return Presentation(heading(view),message,action,view.connected && (!paused || allowed))
    }
}
