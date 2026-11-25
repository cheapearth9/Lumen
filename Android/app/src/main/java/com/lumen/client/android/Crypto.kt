package com.lumen.client.android

import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.PublicKey
import java.security.SecureRandom
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import java.util.Locale
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

// Must stay byte-compatible with Shared/Crypto.swift and HostCommon/Crypto.cs (see the header of Shared/Crypto.swift).

/** P-256 helpers: public keys travel as 65-byte uncompressed X9.63 points. */
object Ec {
    private val lock = Any()
    private var params: ECParameterSpec? = null

    fun generate(): KeyPair {
        val g = KeyPairGenerator.getInstance("EC")
        g.initialize(ECGenParameterSpec("secp256r1"))
        val kp = g.generateKeyPair()
        synchronized(lock) {
            if (params == null) params = (kp.public as ECPublicKey).params
        }
        return kp
    }

    private fun curve(): ECParameterSpec {
        val p = synchronized(lock) { params }
        if (p != null) return p
        generate()
        return synchronized(lock) { params }!!
    }

    private fun fixed32(v: BigInteger): ByteArray {
        val b = v.toByteArray()
        val out = ByteArray(32)
        if (b.size >= 32) System.arraycopy(b, b.size - 32, out, 0, 32)
        else System.arraycopy(b, 0, out, 32 - b.size, b.size)
        return out
    }

    fun x963(pub: PublicKey): ByteArray {
        val w = (pub as ECPublicKey).w
        val out = ByteArray(65)
        out[0] = 4
        System.arraycopy(fixed32(w.affineX), 0, out, 1, 32)
        System.arraycopy(fixed32(w.affineY), 0, out, 33, 32)
        return out
    }

    fun publicFromX963(b: ByteArray): PublicKey? {
        if (b.size != 65 || b[0].toInt() != 4) return null
        return try {
            val x = BigInteger(1, b.copyOfRange(1, 33))
            val y = BigInteger(1, b.copyOfRange(33, 65))
            KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(ECPoint(x, y), curve()))
        } catch (e: Exception) {
            null
        }
    }

    /** Raw ECDH shared secret (32 bytes, the x coordinate). */
    fun agree(priv: PrivateKey, peer: PublicKey): ByteArray? {
        return try {
            val ka = KeyAgreement.getInstance("ECDH")
            ka.init(priv)
            ka.doPhase(peer, true)
            ka.generateSecret()
        } catch (e: Exception) {
            null
        }
    }
}

object PairingCrypto {
    private val rng = SecureRandom()

    fun ascii(s: String): ByteArray = s.toByteArray(Charsets.US_ASCII)

    fun sha256(vararg parts: ByteArray): ByteArray {
        val md = MessageDigest.getInstance("SHA-256")
        for (p in parts) md.update(p)
        return md.digest()
    }

    fun keyId(secret: ByteArray): ByteArray = sha256(ascii("lumen-keyid"), secret).copyOfRange(0, 8)

    fun random(n: Int): ByteArray {
        val b = ByteArray(n)
        rng.nextBytes(b)
        return b
    }

    fun code(pkC: ByteArray, pkH: ByteArray, nonceC: ByteArray, nonceH: ByteArray): String {
        val d = sha256(ascii("lumen-sas-v1"), pkC, pkH, nonceC, nonceH)
        val v = ((d[0].toLong() and 0xFF) shl 24) or ((d[1].toLong() and 0xFF) shl 16) or
            ((d[2].toLong() and 0xFF) shl 8) or (d[3].toLong() and 0xFF)
        return String.format(Locale.US, "%05d", v % 100000L)
    }

    fun hmac(key: ByteArray, data: ByteArray): ByteArray {
        val m = Mac.getInstance("HmacSHA256")
        m.init(SecretKeySpec(key, "HmacSHA256"))
        return m.doFinal(data)
    }

    /** HKDF-SHA256 (RFC 5869) for outputs of up to 32 bytes (a single expand block). */
    fun hkdf(ikm: ByteArray, salt: ByteArray, info: ByteArray, length: Int): ByteArray {
        val prk = hmac(salt, ikm)
        val block = hmac(prk, info + byteArrayOf(1))
        return block.copyOf(length)
    }

    fun secret(ecdh: ByteArray, nonceC: ByteArray, nonceH: ByteArray): ByteArray =
        hkdf(ecdh, nonceC + nonceH, ascii("lumen-pair-v1"), 32)
}

/** Client side of pairing. The 5-digit [code] is shown on this device and typed on the computer. */
class PairingClient {
    private val kp: KeyPair = Ec.generate()
    private val nonce: ByteArray = PairingCrypto.random(16)
    val publicKey: ByteArray = Ec.x963(kp.public)

    var code: String? = null
        private set
    var secret: ByteArray? = null
        private set

    /** Payload of the first pair packet. */
    fun start(deviceName: String): ByteArray {
        var name = deviceName.toByteArray(Charsets.UTF_8)
        if (name.size > 40) name = name.copyOf(40)
        return byteArrayOf(1) + PairingCrypto.sha256(publicKey, nonce) + name
    }

    /** Process the host's step 2; returns the step-3 payload, with [code] and [secret] then available. */
    fun handleStep2(payload: ByteArray): ByteArray? {
        if (payload.size != 1 + 65 + 16 || payload[0].toInt() != 2) return null
        val pkH = payload.copyOfRange(1, 66)
        val nonceH = payload.copyOfRange(66, 82)
        val peer = Ec.publicFromX963(pkH) ?: return null
        val shared = Ec.agree(kp.private, peer) ?: return null
        code = PairingCrypto.code(publicKey, pkH, nonce, nonceH)
        secret = PairingCrypto.secret(shared, nonce, nonceH)
        return byteArrayOf(3) + publicKey + nonce
    }
}

/** Duplicate / replay filter for sealed packets (applied only after a packet authenticated). */
class ReplayWindow {
    private var highest = 0L
    private val seen = HashSet<Long>()
    private val span = 1024L

    fun accept(seq: Long): Boolean {
        if (seq + span <= highest) return false
        if (seen.contains(seq)) return false
        seen.add(seq)
        if (seq > highest) {
            highest = seq
            if (seen.size > 2048) {
                val it = seen.iterator()
                while (it.hasNext()) {
                    if (it.next() + span <= highest) it.remove()
                }
            }
        }
        return true
    }
}

/** Client side of an encrypted session (handshake v2 + AES-256-GCM sealed packets). */
class SecureSession private constructor(private val psk: ByteArray) {
    companion object {
        private const val VERSION = 2

        /** Starts a handshake with the long-term secret. Returns the session and the client-hello payload. */
        fun client(pairingKey: ByteArray): Pair<SecureSession, ByteArray> {
            val s = SecureSession(pairingKey)
            val hello = byteArrayOf(0, VERSION.toByte()) + Ec.x963(s.kp.public) + s.myNonce + PairingCrypto.keyId(pairingKey)
            return Pair(s, hello)
        }

        /** True when a host reply says "this device is not paired". */
        fun isPairingRequired(reply: ByteArray): Boolean = reply.size >= 2 && reply[0].toInt() == 2

        fun isSealed(d: ByteArray): Boolean = d.size >= 2 && d[0].toInt() == 0x4C && d[1].toInt() == 0x45
    }

    private val kp: KeyPair = Ec.generate()
    private val myNonce: ByteArray = PairingCrypto.random(16)
    private var sendKey: SecretKeySpec? = null
    private var recvKey: SecretKeySpec? = null
    private var sendSeq = 0L
    private val window = ReplayWindow()
    private val sendLock = Any()
    private val sendCipher: Cipher = Cipher.getInstance("AES/GCM/NoPadding")
    private val recvCipher: Cipher = Cipher.getInstance("AES/GCM/NoPadding")

    @Volatile
    var isReady: Boolean = false
        private set

    /** Process the host's handshake reply. Returns false if it is malformed. */
    fun finish(serverReply: ByteArray): Boolean {
        if (serverReply.size != 2 + 65 + 16 || serverReply[0].toInt() != 1 || serverReply[1].toInt() != VERSION) return false
        val peer = Ec.publicFromX963(serverReply.copyOfRange(2, 67)) ?: return false
        val shared = Ec.agree(kp.private, peer) ?: return false
        val serverNonce = serverReply.copyOfRange(67, 83)
        val ikm = shared + psk
        val salt = myNonce + serverNonce
        val c2s = PairingCrypto.hkdf(ikm, salt, PairingCrypto.ascii("lumen-v1 c2s"), 32)
        val s2c = PairingCrypto.hkdf(ikm, salt, PairingCrypto.ascii("lumen-v1 s2c"), 32)
        sendKey = SecretKeySpec(c2s, "AES")
        recvKey = SecretKeySpec(s2c, "AES")
        isReady = true
        return true
    }

    private fun nonceFor(seq: Long): ByteArray {
        val n = ByteArray(12)
        for (i in 0 until 8) n[4 + i] = (seq shr (56 - 8 * i)).toByte()
        return n
    }

    /** Encrypts one Lumen packet: [4C 45][seq u64 BE][ciphertext][tag 16]. Null if not ready. */
    fun seal(packet: ByteArray): ByteArray? {
        val key = sendKey ?: return null
        if (!isReady) return null
        return synchronized(sendLock) {
            sendSeq++
            val seq = sendSeq
            val header = ByteArray(10)
            header[0] = 0x4C
            header[1] = 0x45
            for (i in 0 until 8) header[2 + i] = (seq shr (56 - 8 * i)).toByte()
            try {
                sendCipher.init(Cipher.ENCRYPT_MODE, key, GCMParameterSpec(128, nonceFor(seq)))
                sendCipher.updateAAD(header)
                val ct = sendCipher.doFinal(packet)
                header + ct
            } catch (e: Exception) {
                null
            }
        }
    }

    /** Decrypts and authenticates; null for forged, corrupted or replayed packets. */
    fun open(sealed: ByteArray): ByteArray? {
        val key = recvKey ?: return null
        if (!isReady || sealed.size < 10 + 16 || sealed[0].toInt() != 0x4C || sealed[1].toInt() != 0x45) return null
        var seq = 0L
        for (i in 2 until 10) seq = (seq shl 8) or (sealed[i].toLong() and 0xFF)
        val plain: ByteArray = try {
            recvCipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, nonceFor(seq)))
            recvCipher.updateAAD(sealed, 0, 10)
            recvCipher.doFinal(sealed, 10, sealed.size - 10)
        } catch (e: Exception) {
            return null
        }
        if (!window.accept(seq)) return null
        return plain
    }
}
