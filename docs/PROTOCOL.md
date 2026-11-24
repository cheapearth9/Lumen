# Lumen protocol (v2)

Source of truth for packet layout: `Shared/Protocol.swift` (Swift) and `HostCommon/Protocol.cs` (C#). Encryption: `Shared/Crypto.swift` and
`HostCommon/Crypto.cs` (byte-compatible). Keep all four in sync when changing anything.

## Transports
* **Wi-Fi / Ethernet**: UDP, one packet per datagram, host listens on port 47900 (Bonjour `_lumen._udp` / mDNS advertisement).
* **Cable (USB)**: the iOS app listens on TCP 47901; the host reaches it through `usbmuxd` and the stream is length-prefixed
  (`u32 BE length` + packet, see `Deframer`). On a cable the host is the TCP *client* and the iOS app the *server*; the protocol on top is identical.

## Packet header (16 bytes, big endian)
`magic u16 0x4C4D "LM" | type u8 | flags u8 | id u32 | fragIndex u16 | fragCount u16 | timestampMs u32`, then payload.

| type | name | direction | payload |
|---|---|---|---|
| 1 | hello | C->H | `w u16, h u16, fps u8, bitrateMbps u16, flags u8` (flags: bit0 audio wanted, bit1 extend wanted; flags optional) |
| 2 | welcome | H->C | `w u16, h u16, fps u8, caps u8, srcW u16, srcH u16, mode u8` (caps: bit0 native touch, bit1 audio; mode 0 mirror 1 extend; fields after fps optional) |
| 3 | video | H->C | fragmented HEVC access unit (see below) |
| 4 | input | C->H | input event (see below); `id` = sequence number, host de-duplicates |
| 5 | keyframeRequest | C->H | none |
| 6 | ping | both | host echoes, `id` = client timestamp ms |
| 7 | bye | C->H | none |
| 8 | status | H->C | `level u8` (0 info, 1 error) + UTF-8 text. An error means: show the text and disconnect. |
| 9 | discover | C->H | none. **Unencrypted.** Sent by the iOS app to every address of its subnet (unicast scan, no multicast entitlement needed) |
| 10 | announce | H->C | `platform u8` (1 mac, 2 windows, 3 linux) + UTF-8 host name. **Unencrypted.** Reply to discover |
| 11 | handshake | both | key exchange, **unencrypted** (below) |
| 12 | audio | H->C | 240 frames of 48 kHz stereo signed-16-bit little-endian interleaved PCM (960 bytes). `id` = sequence number |

Video payload: for a keyframe `u8 count, (u16 len, bytes)*count` (VPS, SPS, PPS) followed by the AVCC access unit (4-byte length prefixed NALs);
for other frames just the AVCC access unit. Max payload per datagram is 1150 bytes (`Wire.maxPayload`) so sealed packets fit a 1280 byte MTU.

## Input events (payload of type 4)
`kind u8` then: 1 mouseRel `dx i16, dy i16` | 2 mouseAbs `x u16, y u16` (0..65535 over the video) | 3 button `index u8 (0 left,1 right,2 middle), down u8` |
4 scroll `dx i16, dy i16` (positive dy = content moves down, i.e. natural scrolling) | 5 key `macVirtualKeyCode u16, down u8` |
6 touch `id u8, phase u8 (0 down,1 move,2 up,3 cancel), x u16, y u16` (0..65535 over the video) |
7 gamepad `index u8 (0..3), buttons u16 (XInput bits: dpad 1/2/4/8, start 0x10, back 0x20, LS 0x40, RS 0x80, LB 0x100, RB 0x200, guide 0x400, A 0x1000, B 0x2000, X 0x4000, Y 0x8000), lt u8, rt u8, lx i16, ly i16, rx i16, ry i16` (stick up = positive) | 8 gamepad presence `index u8, present u8`.
The client resends the full gamepad state ~4x/s so a lost datagram heals. Hosts: Windows = ViGEmBus virtual Xbox 360, Linux = uinput (045e:028e), macOS ignores them.
Touch events are only sent when the host reported caps bit0 (native touch).

## Pairing and encryption
Everything except discover / announce / handshake / pair is encrypted. Unknown or unauthenticated packets are dropped silently.
* **Pairing** (type 13, once per computer, Sunshine style). The iPad shows a 5-digit code, the user types it on the computer. Steps:
  1 client->host `[1][commit = SHA256(pkC||nonceC) (32)][device name]`; 2 host->client `[2][pkH (65)][nonceH (16)]`; 3 client->host `[3][pkC][nonceC]`
  (host checks the commitment); 4 host->client `[4][result: 0 paired, 1 wrong code, 2 busy/locked, 3 error]`.
  Code = `u32BE(SHA256("lumen-sas-v1"||pkC||pkH||nonceC||nonceH)[0..4]) % 100000`. Both sides compute it, so a man in the middle sees different codes;
  the commitment stops the client side from grinding keys. The host locks pairing for 60 s after 3 wrong codes.
  Long-term secret = `HKDF-SHA256(ECDH secret, salt nonceC||nonceH, "lumen-pair-v1", 32)`, id = `SHA256("lumen-keyid"||secret)[0..8]`. Stored in the host's `paired.json` and the iOS Keychain.
* **Handshake** (type 11): client `[0][version 2][pk (65)][nonce (16)][keyId (8)]`; host `[1][2][pk][nonce]`, or `[2][2]` = pairing required (unknown keyId).
* **Keys**: `ikm = ECDH secret || long-term secret`, `salt = clientNonce || hostNonce`, `c2s = HKDF-SHA256(ikm, salt, "lumen-v1 c2s", 32)`, `s2c` likewise with "lumen-v1 s2c".
* **Sealed packet**: `0x4C 0x45 | seq u64 BE | ciphertext | tag(16)`. AES-256-GCM, nonce = 4 zero bytes || seq, AAD = first 10 bytes. Plaintext = a normal Lumen packet.
  Receivers drop replays (`ReplayWindow`). Over TCP the sealed packet is what gets length-prefixed.
* **Flow**: handshake -> (pairing if required) -> sealed hello -> sealed welcome.

## Mirror / Extend
`hello.flags & 2` asks for **extend**: the host creates a virtual display with the client's resolution and streams that, instead of mirroring the existing display.
The host may also have its own Mirror/Extend buttons (host override). Effective mode = hostOverride ?? hello's request; a hello whose extend flag differs from the
previous hello clears the host override ("last writer wins"). Switching mode restarts the session on the host and the client just sends a new sealed hello;
the next welcome's `mode` byte tells the client what the host is really doing (extend can fall back to mirror when no virtual display is possible; a status packet with level 0 explains why).

## Touch / pointer semantics (client side)
Touchpad mode: relative cursor. Touchscreen mode: real multi-touch forwarded to hosts with native-touch support (Windows, Linux), otherwise emulated with mouse + 1-finger scroll.
