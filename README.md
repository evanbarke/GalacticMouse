# GalacticMouse

Share a mouse and keyboard between two Windows PCs whose networks cannot reach each other. Move the pointer off the configured edge of the primary display to control the other PC; move it back to return. The desktop agent runs in the user's session without admin rights or a driver. Both PCs make outbound WebSocket connections to a small relay.

This is an early working prototype. It has no clipboard or screen streaming. It cannot control the Windows sign-in screen, UAC secure desktop, or elevated applications. Your organization must permit the agent and its outbound connection on managed PCs.

## Windows setup

1. Download the Windows x64 release or build with `dotnet publish client/GalacticMouse/GalacticMouse.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`.
2. Run `GalacticMouse.exe` from a folder in your user profile on both PCs. No installer or admin rights are needed.
3. On the first PC, click **Generate pairing code**, then copy the code to the second PC. The code is a secret: anyone with it could join the pair.
4. The prefilled relay URL is `wss://galactamouse.samix-technology.com/ws`. Set **Other PC is** to **Right** on the left PC and **Left** on the right PC.
5. Click **Connect** on both. Wait for **Peer connected**. Move off the configured edge to switch; move back across the remote edge to return. `Ctrl+Alt+Shift+F12` releases local control in an emergency.

Adjust **Pointer speed** on the PC whose mouse you are using to change how far the remote cursor moves. The default is 1.5×; changes take effect immediately and are saved for that PC.

Only the primary display of each PC is mapped in this version. Close the agent or click Disconnect to restore local input. Keyboard and mouse events are encrypted end to end with AES-256-GCM using the pairing code. The relay sees a room identifier and message timing but cannot decrypt input. Pairing codes are saved under `%LOCALAPPDATA%\GalacticMouse` in the user's profile; protect the profile as you would other credentials.

If the connection drops, the app now shows the relay's close reason and writes connection status to `%LOCALAPPDATA%\GalacticMouse\client.log`. The log does not include the pairing code or input events.

## Relay deployment

`relay/compose.yml` binds the relay to the Mailcow bridge gateway at `172.22.1.1:4786`. `deploy/mailcow-nginx.conf` is the dedicated virtual host with a Let's Encrypt certificate for `galactamouse.samix-technology.com`. `deploy/mailcow-path.conf` also provides the backup route `wss://samix-technology.com/galacticmouse/ws` under the existing site certificate.

The relay is deliberately stateless: at most two sockets per room, no accounts, no input logs, and no database. It handles only authenticated, encrypted messages from agents. `/health` returns a health response.

## Development

Run `cd relay && npm ci && npm test && npm start` for the relay. Run `dotnet build client/GalacticMouse/GalacticMouse.csproj` for the Windows agent. The protocol is in `docs/protocol.md`.
