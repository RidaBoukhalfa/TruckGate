package dz.truckgate.quarry.security

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import dz.truckgate.ticket.EcdsaP256
import dz.truckgate.ticket.TicketSigner
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec

/**
 * The phone's two keys, both ECDSA P-256 inside Android Keystore (StrongBox when available),
 * never exportable:
 *  - ticket key: every signature needs a strong biometric; adding a fingerprint destroys it;
 *  - auth key: signs background requests to the server (configuration, later sync).
 */
object DeviceKeys {
    private const val TICKET = "truckgate.ticket"
    private const val AUTH = "truckgate.auth"
    private const val ALGORITHM = "SHA256withECDSA"

    private fun keyStore(): KeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    fun exist(): Boolean = keyStore().let { it.containsAlias(TICKET) && it.containsAlias(AUTH) }

    /** Creates fresh keys. The enrollment token is the attestation challenge, binding the keys to this enrollment. */
    fun create(attestationChallenge: ByteArray) {
        keyStore().apply { deleteEntry(TICKET); deleteEntry(AUTH) }
        generate(TICKET, attestationChallenge, userAuth = true)
        generate(AUTH, attestationChallenge, userAuth = false)
    }

    private fun generate(alias: String, challenge: ByteArray, userAuth: Boolean) {
        fun spec(strongBox: Boolean): KeyGenParameterSpec =
            KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
                .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_SHA256)
                .setAttestationChallenge(challenge)
                .apply {
                    if (userAuth) {
                        setUserAuthenticationRequired(true)
                        setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG)
                        setInvalidatedByBiometricEnrollment(true)
                    }
                    if (strongBox) setIsStrongBoxBacked(true)
                }
                .build()

        val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
        try {
            generator.initialize(spec(strongBox = true))
            generator.generateKeyPair()
        } catch (e: StrongBoxUnavailableException) {
            generator.initialize(spec(strongBox = false))
            generator.generateKeyPair()
        }
    }

    val ticketPublicKey: ByteArray get() = keyStore().getCertificate(TICKET).publicKey.encoded
    val authPublicKey: ByteArray get() = keyStore().getCertificate(AUTH).publicKey.encoded

    /** Ticket key certificate chain, each DER certificate prefixed with its 4-byte length. */
    fun attestationChain(): ByteArray {
        val out = ByteArrayOutputStream()
        keyStore().getCertificateChain(TICKET)?.forEach { cert ->
            val der = cert.encoded
            out.write(ByteBuffer.allocate(4).putInt(der.size).array())
            out.write(der)
        }
        return out.toByteArray()
    }

    /** Raw r||s signature with the auth key (no user interaction). */
    fun signWithAuthKey(data: ByteArray): ByteArray {
        val key = keyStore().getKey(AUTH, null) as PrivateKey
        val der = Signature.getInstance(ALGORITHM).run { initSign(key); update(data); sign() }
        return EcdsaP256.derToRaw(der)
    }

    /**
     * A Signature initialised with the ticket key, to be unlocked by [Biometric.unlock].
     * Throws KeyPermanentlyInvalidatedException if fingerprints changed since enrollment.
     */
    fun ticketSignature(): Signature {
        val key = keyStore().getKey(TICKET, null) as PrivateKey
        return Signature.getInstance(ALGORITHM).apply { initSign(key) }
    }
}

/** Wraps a biometric-unlocked Signature as a [TicketSigner] for one ticket. */
class UnlockedTicketSigner(private val signature: Signature, override val publicKeySpki: ByteArray) : TicketSigner {
    override fun signDer(data: ByteArray): ByteArray {
        signature.update(data)
        return signature.sign()
    }
}

object Biometric {
    fun available(activity: FragmentActivity): Boolean =
        BiometricManager.from(activity).canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG) ==
            BiometricManager.BIOMETRIC_SUCCESS

    /** Asks for a fingerprint and hands back the unlocked Signature. */
    fun unlock(
        activity: FragmentActivity,
        signature: Signature,
        title: String,
        subtitle: String,
        cancel: String,
        onUnlocked: (Signature) -> Unit,
        onError: (String) -> Unit,
    ) {
        val prompt = BiometricPrompt(activity, ContextCompat.getMainExecutor(activity),
            object : BiometricPrompt.AuthenticationCallback() {
                override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) {
                    val unlocked = result.cryptoObject?.signature
                    if (unlocked == null) onError("no signature") else onUnlocked(unlocked)
                }

                override fun onAuthenticationError(errorCode: Int, errString: CharSequence) {
                    onError(errString.toString())
                }
            })
        val info = BiometricPrompt.PromptInfo.Builder()
            .setTitle(title)
            .setSubtitle(subtitle)
            .setNegativeButtonText(cancel)
            .setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_STRONG)
            .build()
        prompt.authenticate(info, BiometricPrompt.CryptoObject(signature))
    }
}
