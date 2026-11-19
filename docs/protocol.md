# Protocol v1

Transport: local IPv4, default port 49732. **Production requires authenticated TLS 1.2; no plaintext input.** Windows listens; Safari (or the optional native iOS client) connects. The same listener supports HTTPS, WSS and TLS-wrapped newline-delimited JSON. Plain HTTP only serves public certificate setup; old plaintext commands and WebSocket upgrades are rejected. Test-only server instances may use plaintext with fake input. Incomplete frames expire; idle reads time out after 8 seconds. Clients send a ping every 2 seconds and disconnect if replies stop for more than 7 seconds.

## Browser transport

`GET /` serves the embedded mobile interface. Other static routes: `/index.html`, `/app.css`, `/app.js`, `/viewport.js`, `/keyboard.js`, `/gestures.js`, `/manifest.webmanifest`, `/icon.svg`, `/favicon.ico` (SVG alias). No arbitrary disk files or remote assets. Responses use `Cache-Control: no-store`, CSP, `X-Frame-Options: DENY` and `nosniff`. Assets remain available during authentication cooldown.

HTTP Host must be the numeric IP and port on which the connection was accepted. Domain names are rejected to prevent DNS rebinding. Only same-authority `Origin: https://IP:port` is accepted for `GET /ws` with WebSocket version 13, a valid key and upgrade headers. No cookies or saved pairing code.

Each WebSocket text message contains one JSON object without LF. Limit: 1024 UTF-8 bytes including fragments. Client frames must be masked. Text fragmentation and ping/pong/close control frames are supported. Reserved bits, binary messages, invalid continuations and oversized messages are rejected. Complete text messages are adapted to the shared command parser. TCP and browser clients share authentication, limits and ownership. Closing the socket releases ownership.

## TLS native transport

UTF-8 JSON objects delimited by LF (`\n`), maximum 1024 bytes excluding LF. Retained for the optional SwiftUI client. Both transports use the same JSON objects below.

First client frame:

```json
{"type":"hello","version":1,"code":"012345ABCDEF"}
```

Server accepts the displayed code (case insensitive) or a registered device credential with version 1; only one authenticated client owns input. The code is 6 cryptographically random bytes encoded as 12 hexadecimal characters. Five invalid handshakes cause a 30-second cooldown for untrusted attempts. A valid saved credential bypasses this cooldown, but cannot displace an existing owner.

## Persistent device pairing

First enrollment is authorized only by a valid current code. Add `remember: true` (JSON boolean) and optionally `deviceName` to the hello. Names are trimmed, stripped of controls and bounded to 40 UTF-16 characters. Up to 32 devices may be enrolled.

```json
{"type":"hello","version":1,"code":"012345ABCDEF","remember":true,"deviceName":"My iPhone"}
```

On successful enrollment `ready` additionally returns `remembered: true`, a 32-character lowercase hexadecimal `deviceId` and a 64-character uppercase hexadecimal `token` generated from 32 cryptographically random bytes. The browser saves only `{deviceId, token}` in localStorage under `remote.device.v1` for the current origin. It never persists the short code. Subsequent authentication is:

```json
{"type":"hello","version":1,"deviceId":"<32 lowercase hex>","token":"<64 uppercase hex>"}
```

Saved login returns `remembered: true`, the device ID, and `token: null`: the secret is issued only on enrollment. Code-only access remains compatible. Supplying either credential field selects device authentication exclusively; a wrong saved key never falls back to a simultaneous valid code.

Windows stores only SHA-256 token hashes, IDs, names and creation times in `%LOCALAPPDATA%/RemoteController/devices.json`. Writes use a flushed temporary file and atomic replacement; failed writes cannot create in-memory trusted entries. Corruption fails closed, preserves the original file and keeps code-only sessions usable. Tests and the preview server use isolated stores, never the production user file.

Only the Windows UI exposes device listing and revocation. Forgetting removes the hash on disk before releasing any active gesture and closing the matching connection. It sends `error` with `code: invalid_device`. Code rotation does not revoke saved keys; it sends `code: disconnected` to stop reconnection in the current page until a manual connect or page reopen.

Auth error codes: `invalid_code`, `invalid_device`, `rate_limited`, `busy`, `storage_unavailable`, `disconnected`. Only `invalid_device` deletes the browser key. Busy and cooldown responses preserve credentials and retry after 15 or 30 seconds. Network failures retry with backoff (2, 4, 8, then up to 15 seconds). Hidden/offline pages do not retry. Foreground/pageshow/online resumes saved login; explicit manual disconnect pauses it for that page lifetime. Keys have no automatic expiry and remain valid until forgotten. Possession of the saved key authorizes the phone; TLS with a verified per-PC certificate authenticates the server and protects that key in transit.

Server success:

```json
{"type":"ready","version":1,"name":"LAPTOP","pinch":true,"keyboard":true,"remembered":false,"deviceId":null,"token":null}
```

Server rejection: `{"type":"error","message":"…"}` followed by disconnect. Client must wait for `ready` before sending commands. A valid handshake never ejects an existing owner.

`pinch` advertises whether the server backend exposes native precision-touchpad injection. Missing/false means the browser must not send pinch; it never substitutes Ctrl+wheel. A native pinch error produces `{"type":"notice","feature":"pinch","message":"…"}` and keeps the authenticated connection usable. Pending updates/end of the failed gesture are drained.

| Client command | Fields | Effect |
| --- | --- | --- |
| `move` | Integer `dx`, `dy`, each −500…500 | Relative mouse movement |
| `click` | `button`: `left` or `right` | Atomic down/up pair |
| `scroll` | Integer `delta`, −1200…1200 | Vertical wheel delta (120 = one standard notch) |
| `pinch` | `phase`: `start`, `update`, `end`; `update` requires integer `scale`, 100…4000 | Native precision-touchpad contact sequence; 1000 means starting span, 1500 means 1.5 times that span |
| `volume` | `action`: `up`, `down`, `mute` | Windows volume key down/up pair |
| `text_edit` | Integer `seq` (1…Int32.MaxValue), integer `deletions` (0…128), string `text` (0…128 UTF-16 units) | Delete the changed suffix with Backspace and type its replacement with Unicode input |
| `ping` | None | Server replies `{"type":"pong"}` |
| `disconnect` | None | Server closes connection and releases ownership |

Unknown commands, invalid JSON, oversized frames, non-integer numeric values, out-of-range values and timeouts close the connection. At most 16 simultaneous TCP clients, including HTTP and unauthenticated clients. The Windows stop button closes every client; rotating the code closes the owner and invalidates old credentials. Server input is serialized with revocation to prevent commands racing an explicit disconnect.

The browser accumulates relative movement and sends on each display animation frame; the optional native iOS client uses up to 60 Hz. Browser cursor motion uses an optional 8 ms time-based exponential filter, with fractional deltas preserved across packets. The 2 px start threshold accumulates the initial samples rather than discarding them. Click, pointer release and pinch start flush pending cursor movement immediately; cancellation/disconnect clears it. Backlogged WebSocket sends discard stale cursor motion instead of replaying it after congestion. This improves pacing but does not remove network latency. Scroll and pinch are not smoothed by the cursor filter. Browser disconnects on background, page hide or offline events. Saved devices reconnect automatically on return or after network failure; code-only clients reconnect manually. No user files or shell commands are exposed. Screen images require the separately authorized, opt-in channel below.

Pinch uses a 120 × 80 mm synthetic PT_TOUCHPAD, physical coordinates and gesture-only mode. Two contacts begin 24 mm apart, move symmetrically about a fixed center and end at their last positions. Confidence/contact state flags follow Microsoft's touchpad injection sample. No keyboard modifiers, wheel events or touchscreen contacts are used for pinch. Scale updates are coalesced at the browser's display frame rate, with final scale flushed before end. The cursor determines the Windows target; the target application controls how the gesture is rendered. Extra fingers or pointer cancellation end the gesture. Revocation, socket failure and server stop destroy the device to release any contacts.

## Live keyboard

`keyboard: true` in `ready` advertises `ITextInput` support. The main-screen button synchronously focuses a native textarea inside a dialog during its tap handler so iOS can show its standard keyboard. `input` changes are mirrored; composing IME input waits for `compositionend`. Each opening starts a fresh empty typing segment at the current Windows caret. No clipboard, text persistence or reading the target field is used.

The browser compares acknowledged text with the latest local draft. Appends are chunked at 128 UTF-16 units without splitting surrogate pairs. Corrections/middle edits delete the changed suffix and retype its new value. Deletion counts use grapheme segmentation when available. Editors may differ in Backspace behavior for complex graphemes. CR/CRLF becomes LF; unsupported controls and unpaired surrogates are stripped before sending. The local segment is capped at 8192 UTF-16 units.

Sequence numbers strictly increase by one per authenticated connection, starting at 1. Windows replies `{"type":"text_ack","seq":N}` after `SendInput` accepts the packet sequence; this confirms injection, not application-level rendering. It sends Unicode key-down/up pairs for UTF-16 units, Backspace for deletion, Enter for LF and Tab for tab. Other control characters, oversized values and repeated/out-of-order sequence numbers close the connection. A native failure consumes the sequence and sends `notice` with `feature: keyboard`; the connection remains available for mouse/volume.

Only one edit is in flight. Further phone changes are coalesced until acknowledgement. Closing waits for the latest draft to be acknowledged; a 3.5-second timeout stops the connection. On disconnect or native failure the local draft remains visible but read-only, unsent text is not replayed, and the user must reopen the keyboard to start a new segment. The client never claims to mirror manual changes made on the PC; keep the target caret at the end of the segment during phone editing.

## Tap and drag

`drag: true` in `ready` enables `{"type":"drag","phase":"start"}` and a matching `end`. These hold/release the left mouse button around relative `move` commands. Duplicate starts, unmatched ends and interleaved clicks/pinches are rejected. The backend remembers injected button state and releases it on socket loss, timeout, revocation, code rotation and server stop. A browser without this capability keeps ordinary taps.

The browser preserves a single tap for 330 ms. A second touch within 32 CSS pixels sends button-down immediately; moving then selects/drags. After a moved drag, lifting flushes movement but keeps the left button held for 800 ms. A new touch anywhere before expiry resumes relative movement from its new location without another button-down or cursor jump. A stationary resumed tap drops immediately; otherwise the next lift renews the 800 ms grace period. A quick stationary double tap still releases immediately. Event timestamps also enforce the deadline if the timer is delayed; cancellation clears timers and late callbacks cannot release a newer drag. Two quick stationary touches form a normal double-click sequence. A second finger or cancellation releases the drag and blocks stray clicks. Toolbar actions clear gesture history and release pointer captures. Finger buttons activate synchronously on a completed pointer tap, suppress a following compatibility click and retain native mouse and keyboard activation. Scroll/cancel/outside releases do not activate a button.

## Circular desktop magnifier

`magnifier: true` in `ready` advertises the optional backend. Authenticated clients send `{"type":"magnifier","enabled":true,"radius":160,"zoom":200,"mode":"center"}`. `enabled` must be a JSON boolean; radius is an integer 80–280 desktop pixels, zoom is an integer 150–400 (percent), and mode is `center` or `offset`. The server replies `{"type":"magnifier_state","enabled":true}` after applying the configuration. Native errors send `notice` with `feature: magnifier` and disable the lens without ending the connection. Heartbeats also report a lens that Windows stopped between commands.

The browser permits one configuration in flight, coalesces subsequent changes and disconnects after 3.5 seconds without an acknowledgement. Radius, zoom and mode are stored locally; enabled state is never persisted or automatically restored. Disconnect, timeout, revocation, code rotation and server stop hide the lens. Windows renders a circular, topmost, non-activating, click-through Magnification API control on its own STA thread. Its source follows the OS cursor and is clamped to the current monitor, including negative monitor coordinates. No application zoom, global Windows Magnifier settings or screen transmission is involved.


## Desktop region stream

`screen: true` in `ready` advertises streaming to WebSocket clients. The authenticated owner sends `screen_start` with a positive integer `id`. `screen_ready` echoes `id` and includes an ephemeral random 256-bit ticket. A separate same-origin WebSocket sends `screen_hello` with that ticket and receives `screen_connected`. There is at most one screen connection per owner. Tickets never appear in URLs or persistent storage. `screen_stop` revokes the ticket, closes the screen connection and replies `screen_stopped`; owner disconnect, timeout, code rotation, device revocation and server stop do the same.

The viewer requests `screen_next` with sequential `seq` values starting at 1, integer width 160–960, height 120–1280, JPEG quality 45–85 and FPS 10–60. One binary WebSocket message carries the JPEG reply (at most 2 MiB). The server captures only on demand, rate-limits requests with a high-resolution waitable timer, disposes its image source on termination, and performs capture/encoding/writes outside the input lock. Large WebSocket output uses 64-bit framing and a single socket write; incoming JSON limits remain 1024 bytes. Invalid screen input only closes the screen channel.

The browser allows one requested frame at a time and waits for image load before requesting the next, so it cannot queue stale images. It adapts FPS and quality using an exponentially smoothed request-to-display duration and shows measured FPS. The stream pauses in controls and the keyboard dialog, obtains a new ticket on return, and stays off after disconnect. Object URLs and image content are released when stopped. `img-src blob:` permits these in-memory JPEGs; no recording or disk snapshots are used.

Desktop crop size follows the touchpad aspect ratio. A central 50% zone stays stationary; near an edge the crop follows with a 65 ms smoothing response. Crop and cursor are kept on the current monitor, including negative coordinates; a move to another monitor resets the view. The image is non-interactive and cannot intercept pointer events. All input remains relative touchpad input; it never becomes direct tapping on screen coordinates.

## TLS identity and bootstrap

Production MainForm always supplies TransportSecurity. SslStream negotiates TLS 1.2 with encryption, integrity and at least 128-bit cipher strength. There is no production switch to allow plaintext control. HTTPS origin validation, incoming JSON bounds, screen tickets, pairing and revocation remain enforced after TLS. HTTP may only serve `/`, `/index.html`, `/setup`, `/setup.css`, `/remote.mobileconfig` and `/remote-ca.cer` for public onboarding; it never serves the controller, keys, commands or screen content. HTTPS `/` serves the actual client. All phone WebSocket URLs derive `wss:` from the HTTPS origin.

A unique RSA-3072 SHA-256 certificate authority is created on the PC (not installed into Windows trust stores). Its CA constraint has path length zero. The RSA-2048 server certificate has server-auth EKU, IP SANs and a 90-day lifetime, and is reissued for current IPv4 addresses without changing the authority. Authority lifetime is five years. Corrupt/expired authority data fails closed instead of silently replacing the trust anchor. Private PKCS#12 contents are stored only inside current-user Windows DPAPI envelopes; the public iPhone profile contains only the root DER certificate. The phone user must verify the profile certificate's full SHA-256 fingerprint against the Windows dialog, then explicitly install and trust it. No certificate-validation bypass, global trust-store installation or third-party relay is used.

Pairing storage is also protected with current-user DPAPI. Valid legacy JSON devices migrate atomically while preserving their IDs and token hashes; damaged encrypted stores are preserved and fail closed. The browser's HTTPS storage is a new origin, requiring one new pairing; old HTTP tokens are never exported over plaintext to migrate them.