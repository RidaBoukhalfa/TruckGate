package dz.truckgate.ticket

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class ConformanceTest {
    @Test
    fun `shared vectors pass`() {
        val suite = ConformanceSuite(ConformanceSuite.defaultVectorsDir()).runAll()
        assertEquals(emptyList<String>(), suite.failures)
        assertTrue(suite.passed > 100, "suspiciously few checks: ${suite.passed}")
    }
}
