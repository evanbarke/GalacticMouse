# Relay protocol v1

Clients connect over TLS WebSocket at `/ws`. The first message is JSON `{"type":"join","room":"<SHA-256 pairing-code hex>"}`. The server accepts two clients per room and sends `{"type":"presence","peers":0|1}`. All subsequent client messages are `{"type":"data","payload":"<base64 of nonce | AES-GCM ciphertext | 16-byte tag>"}`. The relay validates framing and size, then forwards the envelope unchanged to the other socket.

The pairing code is 32 random bytes, encoded with base64url. Clients derive the room id with SHA-256 and the encryption key with SHA-256 of `GalacticMouse v1 key:` followed by the raw pairing bytes. Each encrypted message has a fresh 12-byte random nonce. The receiver rejects duplicate nonces within the current connection; reconnects clear the cache. Message JSON inside the ciphertext includes `hello`, `enter`, `move`, `mouse`, `key`, and `leave` events. Input is accepted only from the one peer present in the room.

No clipboard, file, or screen payload exists in this protocol.
