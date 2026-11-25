package com.lumen.client.android

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.util.Collections
import java.util.Locale
import java.util.concurrent.atomic.AtomicBoolean

/** Routes a socket over Wi-Fi / Ethernet even when the phone's default network is cellular (Wi-Fi without internet). */
object NetBind {
    fun bind(ctx: Context, s: DatagramSocket) {
        try {
            val cm = ctx.applicationContext.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            @Suppress("DEPRECATION")
            val nets = cm.allNetworks
            for (n in nets) {
                val caps = cm.getNetworkCapabilities(n) ?: continue
                if (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
                    caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
                ) {
                    n.bindSocket(s)
                    return
                }
            }
        } catch (e: Exception) {
            // best effort
        }
    }
}

class ScanResult(val address: String, val platform: Int, val name: String)

/** Unicast subnet scan: sends an unencrypted Discover to every host address of the local subnet and collects Announces. */
class SubnetScanner(private val ctx: Context) {
    private class Iface(val ip: Long, val mask: Long)

    private val listenMs = 1500L
    private val batchSize = 64

    private fun toLong(b: ByteArray): Long =
        ((b[0].toLong() and 0xFF) shl 24) or ((b[1].toLong() and 0xFF) shl 16) or
            ((b[2].toLong() and 0xFF) shl 8) or (b[3].toLong() and 0xFF)

    private fun skipName(name: String): Boolean {
        val n = name.lowercase(Locale.US)
        val bad = arrayOf("lo", "rmnet", "ccmni", "pdp", "tun", "ppp", "dummy", "v4-", "ifb", "clat", "sit", "ip6", "p2p")
        for (b in bad) if (n.startsWith(b)) return true
        return false
    }

    private fun localInterfaces(): List<Iface> {
        val out = ArrayList<Iface>()
        val en = try { NetworkInterface.getNetworkInterfaces() } catch (e: Exception) { null } ?: return out
        for (ni in Collections.list(en)) {
            try {
                if (!ni.isUp || ni.isLoopback || skipName(ni.name)) continue
                for (ia in ni.interfaceAddresses) {
                    val a = ia.address
                    if (a !is Inet4Address) continue
                    val ip = toLong(a.address)
                    val prefix = ia.networkPrefixLength.toInt()
                    if (prefix <= 0 || prefix > 30) continue
                    if ((ip shr 16) == 0xA9FEL) continue // link-local 169.254/16
                    val mask = (0xFFFFFFFFL shl (32 - prefix)) and 0xFFFFFFFFL
                    out.add(Iface(ip, mask))
                }
            } catch (e: Exception) {
                // skip this interface
            }
        }
        return out
    }

    /** All host addresses of the interface's subnet, capped at a /22 around the device. */
    private fun hostsIn(i: Iface): List<Long> {
        var mask = i.mask
        if (java.lang.Long.bitCount(mask) < 22) mask = 0xFFFFFC00L
        val net = i.ip and mask
        val bcast = net or (mask.inv() and 0xFFFFFFFFL)
        val out = ArrayList<Long>()
        var a = net + 1
        while (a < bcast) {
            if (a != i.ip) out.add(a)
            a++
        }
        return out
    }

    /** Blocking; runs one scan. [onResult] is called from a helper thread once per responding address. */
    fun scan(onResult: (ScanResult) -> Unit) {
        val ifaces = localInterfaces()
        if (ifaces.isEmpty()) return
        val targets = ArrayList<Long>()
        val seenTargets = HashSet<Long>()
        for (i in ifaces) for (a in hostsIn(i)) if (seenTargets.add(a)) targets.add(a)
        if (targets.isEmpty()) return

        val socket = try { DatagramSocket() } catch (e: Exception) { return }
        NetBind.bind(ctx, socket)
        try { socket.receiveBufferSize = 1 shl 18 } catch (e: Exception) { }
        try { socket.soTimeout = 100 } catch (e: Exception) { }

        val done = AtomicBoolean(false)
        val seen = HashSet<String>()
        val rx = Thread {
            while (!done.get()) {
                try {
                    val buf = ByteArray(512)
                    val p = DatagramPacket(buf, buf.size)
                    socket.receive(p)
                    val pkt = Wire.decode(buf.copyOf(p.length)) ?: continue
                    if (pkt.type != PacketType.ANNOUNCE || pkt.payload.isEmpty()) continue
                    val r = ByteReader(pkt.payload)
                    val platform = r.u8()
                    val name = String(r.rest(), Charsets.UTF_8)
                    val address = p.address.hostAddress ?: continue
                    val first = synchronized(seen) { seen.add(address) }
                    if (first) onResult(ScanResult(address, platform, if (name.isEmpty()) address else name))
                } catch (e: SocketTimeoutException) {
                    // keep listening
                } catch (e: IOException) {
                    if (socket.isClosed) break
                }
            }
        }
        rx.isDaemon = true
        rx.start()

        val pkt = Wire.encode(PacketType.DISCOVER)
        var sent = 0
        for (ip in targets) {
            try {
                val b = byteArrayOf((ip shr 24).toByte(), (ip shr 16).toByte(), (ip shr 8).toByte(), ip.toByte())
                socket.send(DatagramPacket(pkt, pkt.size, InetAddress.getByAddress(b), LUMEN_PORT))
            } catch (e: IOException) {
                try { Thread.sleep(3) } catch (ie: InterruptedException) { }
            }
            sent++
            if (sent % batchSize == 0) {
                try { Thread.sleep(4) } catch (ie: InterruptedException) { }
            }
        }
        try { Thread.sleep(listenMs) } catch (ie: InterruptedException) { }
        done.set(true)
        socket.close()
        try { rx.join(500) } catch (ie: InterruptedException) { }
    }
}
