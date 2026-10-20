import http from 'node:http';
import { WebSocketServer } from 'ws';

const MAX_FRAME = 4096;
const rooms = new Map();
const server = http.createServer((req, res) => {
  if (req.url === '/health') {
    res.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' });
    res.end(JSON.stringify({ status: 'ok' }));
  } else {
    res.writeHead(404); res.end();
  }
});
const wss = new WebSocketServer({ noServer: true, maxPayload: MAX_FRAME });

server.on('upgrade', (req, socket, head) => {
  if (req.url !== '/ws') { socket.destroy(); return; }
  wss.handleUpgrade(req, socket, head, ws => wss.emit('connection', ws));
});

function tellPresence(room) {
  for (const peer of room) {
    if (peer.readyState === 1) peer.send(JSON.stringify({ type: 'presence', peers: room.size - 1 }));
  }
}

wss.on('connection', ws => {
  let roomId = null;
  let count = 0;
  let windowStart = Date.now();
  ws.joinTimer = setTimeout(() => { console.info('join timeout'); ws.close(1008, 'Join timeout'); }, 5000);
  ws.on('error', err => console.warn(`socket error: ${err.message}`));
  ws.on('message', (bytes, isBinary) => {
    if (isBinary || bytes.length > MAX_FRAME) { ws.close(1009, 'Invalid frame'); return; }
    const now = Date.now();
    if (now - windowStart >= 1000) { windowStart = now; count = 0; }
    if (++count > 500) { ws.close(1008, 'Rate limited'); return; }
    let msg;
    try { msg = JSON.parse(bytes.toString()); } catch { ws.close(1007, 'Invalid JSON'); return; }
    if (!roomId) {
      if (msg.type !== 'join' || typeof msg.room !== 'string' || !/^[a-f0-9]{64}$/.test(msg.room)) {
        ws.close(1008, 'Invalid room'); return;
      }
      const room = rooms.get(msg.room) ?? new Set();
      if (room.size >= 2) {
        console.info(`join rejected room-full room=${msg.room.slice(0, 12)}`);
        ws.close(1008, 'Room full'); return;
      }
      roomId = msg.room;
      room.add(ws); rooms.set(roomId, room);
      console.info(`join room=${roomId.slice(0, 12)} peers=${room.size}`);
      clearTimeout(ws.joinTimer);
      tellPresence(room);
      return;
    }
    if (msg.type !== 'data' || typeof msg.payload !== 'string' || msg.payload.length > 4000 ||
        !/^[A-Za-z0-9+/]+={0,2}$/.test(msg.payload)) {
      ws.close(1008, 'Invalid data'); return;
    }
    for (const peer of rooms.get(roomId) ?? []) {
      if (peer !== ws && peer.readyState === 1) {
        if (peer.bufferedAmount >= 65536) peer.close(1013, 'Backpressure');
        else peer.send(bytes.toString('utf8'));
      }
    }
  });
  ws.on('close', (code, reason) => {
    console.info(`close room=${roomId?.slice(0, 12) ?? 'none'} code=${code} reason=${reason.toString().slice(0, 80)}`);
    clearTimeout(ws.joinTimer);
    if (!roomId) return;
    const room = rooms.get(roomId);
    room?.delete(ws);
    if (!room?.size) rooms.delete(roomId);
    else tellPresence(room);
  });
});

const timer = setInterval(() => {
  for (const ws of wss.clients) {
    if (ws.isAlive === false) { ws.terminate(); continue; }
    ws.isAlive = false; ws.ping();
  }
}, 30000);
timer.unref();
wss.on('connection', ws => { ws.isAlive = true; ws.on('pong', () => { ws.isAlive = true; }); });

const port = Number(process.env.PORT || 8080);
if (process.env.NODE_ENV !== 'test') server.listen(port, '0.0.0.0', () => console.log(`GalacticMouse relay listening on ${port}`));
export { server, wss, rooms };
