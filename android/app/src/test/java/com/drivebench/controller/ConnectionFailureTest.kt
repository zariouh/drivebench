package com.drivebench.controller

import org.junit.Assert.*
import org.junit.Test
import java.io.IOException
import java.net.SocketException

class ConnectionFailureTest {
    @Test fun bindingFailureKeepsActualStageAndNativeCause() {
        val trace=ConnectionTrace()
        val error=IOException("Could not bind",SocketException("Binding socket: EACCES (Permission denied)"))
        val shown=trace.fail(ConnectionStep.WIFI_BIND,error)
        assertTrue(shown.contains("WIFI_BIND"));assertTrue(shown.contains("SocketException"));assertTrue(shown.contains("EACCES"))
    }
    @Test fun reportNeverIncludesBootstrapOrSessionSecrets() {
        val trace=ConnectionTrace();val token="q".repeat(43);val key="R".repeat(44)
        trace.event("drivebench://pair?v=1&token=$token")
        trace.fail(ConnectionStep.SESSION_SETUP,IOException("credential=$token inputKey=$key"))
        val report=trace.report("0.3.2")
        assertFalse(report.contains(token));assertFalse(report.contains(key));assertFalse(report.contains("drivebench://"))
    }
    @Test fun exceptionAndHistorySizesStayBounded() {
        val trace=ConnectionTrace();repeat(100) { trace.event("event-$it "+"abc ".repeat(100)) }
        val shown=trace.fail(ConnectionStep.TLS_HANDSHAKE,IOException("failure ".repeat(1000)))
        assertTrue(shown.length<=220);assertTrue(trace.report("0.3.2").length<=7000)
        assertFalse(trace.report("0.3.2").contains("event-0 "))
    }
    @Test fun startingNewAttemptClearsOldFailure() {
        val trace=ConnectionTrace();trace.fail(ConnectionStep.TCP_CONNECT,IOException("old failure"));trace.reset()
        trace.event(ConnectionStep.TLS_CONTEXT.name)
        assertFalse(trace.report("0.3.2").contains("old failure"));assertTrue(trace.report("0.3.2").contains("TLS_CONTEXT"))
    }
}
