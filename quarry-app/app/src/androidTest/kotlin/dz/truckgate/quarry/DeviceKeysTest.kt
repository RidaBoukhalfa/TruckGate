package dz.truckgate.quarry

import android.app.KeyguardManager
import android.os.Build
import android.util.Log
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import dz.truckgate.quarry.security.DeviceKeys
import dz.truckgate.ticket.EcdsaP256
import org.junit.After
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature

/**
 * Runs the real Android Keystore code of [DeviceKeys] on each Android version (CI emulators,
 * Android 8 to 14). Test keys use their own aliases; the app's keys are not touched.
 */
@RunWith(AndroidJUnit4::class)
class DeviceKeysTest {
    private val challenge = "attestation-challenge-0123456789".toByteArray()
    private val authAlias = "truckgate.test.auth"
    private val ticketAlias = "truckgate.test.ticket"

    private fun keyStore() = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    @After
    fun cleanUp() {
        DeviceKeys.delete(authAlias)
        DeviceKeys.delete(ticketAlias)
    }

    @Test
    fun authKeyIsCreatedInKeystoreAndItsSignaturesVerifyLikeOnTheServer() {
        DeviceKeys.generate(authAlias, challenge, userAuth = false)
        val ks = keyStore()
        val key = ks.getKey(authAlias, null) as PrivateKey
        val data = "TGAUTH1|1|1795000000|GET|/api/devices/1/config|e3b0c442".toByteArray()
        val der = Signature.getInstance("SHA256withECDSA").run { initSign(key); update(data); sign() }
        val spki = ks.getCertificate(authAlias).publicKey.encoded
        assertTrue("raw r||s signature must verify with the public key", EcdsaP256.verify(spki, data, EcdsaP256.derToRaw(der)))
        val chain = ks.getCertificateChain(authAlias)
        assertNotNull(chain)
        Log.i(TAG, "API ${Build.VERSION.SDK_INT}: auth key OK, attestation chain of ${chain.size} certificate(s)")
    }

    @Test
    fun ticketKeyCannotExistWithoutAFingerprintForEveryUse() {
        val keyguard = InstrumentationRegistry.getInstrumentation().targetContext.getSystemService(KeyguardManager::class.java)
        val error = runCatching { DeviceKeys.generate(ticketAlias, challenge, userAuth = true) }.exceptionOrNull()
        if (error == null) {
            // a device with an enrolled fingerprint: the key exists, and it must refuse to sign
            // until a fingerprint unlocks it
            val key = keyStore().getKey(ticketAlias, null) as PrivateKey
            val signError = runCatching {
                Signature.getInstance("SHA256withECDSA").run { initSign(key); update(byteArrayOf(1)); sign() }
            }.exceptionOrNull()
            Log.i(TAG, "API ${Build.VERSION.SDK_INT}: ticket key created; signing without fingerprint -> $signError")
            assertNotNull("signing without a fingerprint must fail", signError)
            return
        }
        val text = generateSequence(error) { it.cause }.joinToString(" | ") { "${it.javaClass.simpleName}: ${it.message}" }
        Log.i(TAG, "API ${Build.VERSION.SDK_INT}, secure lock screen=${keyguard.isDeviceSecure}: $text")
        assertFalse("method missing on this Android version: $text", error is LinkageError)
        if (keyguard.isDeviceSecure) {
            // screen lock present but no fingerprint: the platform must refuse a per-use biometric key
            assertTrue(text, text.contains("enrolled", ignoreCase = true))
        } else {
            assertTrue(text, text.contains("lock screen", ignoreCase = true) || text.contains("secure", ignoreCase = true))
        }
    }

    private companion object {
        const val TAG = "TruckGateTest"
    }
}
