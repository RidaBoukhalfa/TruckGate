package dz.truckgate.quarry.security

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import androidx.annotation.RequiresApi
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
import java.security.ProviderException
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

    /**
     * Android 11+: the ticket key accepts only a strong biometric, for every use
     * (setUserAuthenticationParameters(0, BIOMETRIC_STRONG)).
     * Android 8–10: validity -1 = authentication for every use; before Android 11 such a key can
     * only be unlocked through BiometricPrompt with a CryptoObject, i.e. by a fingerprint — the
     * screen-lock PIN cannot unlock it. Same rule, older API.
     */
    internal fun spec(alias: String, challenge: ByteArray?, userAuth: Boolean, strongBox: Boolean): KeyGenParameterSpec =
        KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)
            .apply {
                if (challenge != null) setAttestationChallenge(challenge)
                if (userAuth) {
                    setUserAuthenticationRequired(true)
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                        setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG)
                    } else {
                        @Suppress("DEPRECATION")
                        setUserAuthenticationValidityDurationSeconds(-1)
                    }
                    setInvalidatedByBiometricEnrollment(true)
                }
                if (strongBox && Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) setIsStrongBoxBacked(true)
            }
            .build()

    internal fun generate(alias: String, challenge: ByteArray, userAuth: Boolean) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P && StrongBox.tryGenerate(alias, challenge, userAuth)) return
        val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
        try {
            generator.initialize(spec(alias, challenge, userAuth, strongBox = false))
            generator.generateKeyPair()
        } catch (e: ProviderException) {
            // Some older phones keep keys in secure hardware but cannot produce an attestation
            // certificate. The key is still created inside Android Keystore, just without the
            // attestation (the server stores it as missing).
            generator.initialize(spec(alias, null, userAuth, strongBox = false))
            generator.generateKeyPair()
        }
    }

    /** Separate class: StrongBoxUnavailableException exists only from Android 9. */
    @RequiresApi(Build.VERSION_CODES.P)
    private object StrongBox {
        fun tryGenerate(alias: String, challenge: ByteArray, userAuth: Boolean): Boolean = try {
            KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").run {
                initialize(spec(alias, challenge, userAuth, strongBox = true))
                generateKeyPair()
            }
            true
        } catch (e: StrongBoxUnavailableException) {
            false
        }
    }

    internal fun delete(alias: String) = keyStore().deleteEntry(alias)

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
