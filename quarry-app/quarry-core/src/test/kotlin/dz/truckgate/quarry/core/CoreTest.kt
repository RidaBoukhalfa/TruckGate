package dz.truckgate.quarry.core

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class CoreTest {
    @Test
    fun `quarry core checks pass`() {
        val c = CoreChecks().runAll()
        assertEquals(emptyList<String>(), c.failures)
    }
}
