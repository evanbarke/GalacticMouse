import test from 'node:test';
import assert from 'node:assert/strict';
import WebSocket from 'ws';

process.env.NODE_ENV = 'test';
const { server } = await import('./server.js');
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const url = `ws://127.0.0.1:${server.address().port}/ws`;

function connect() { return new Promise(resolve => { const ws = new WebSocket(url); ws.once('open', () => resolve(ws)); }); }
function next(ws) { return new Promise(resolve => ws.once('message', data => resolve(JSON.parse(data.toString())))); }

test('only paired room members receive opaque frames', async () => {
  const room = 'a'.repeat(64), other = 'b'.repeat(64);
  const a = await connect(); a.send(JSON.stringify({ type: 'join', room })); await next(a);
  const b = await connect(); b.send(JSON.stringify({ type: 'join', room }));
  assert.deepEqual(await next(b), { type: 'presence', peers: 1 });
  const c = await connect(); c.send(JSON.stringify({ type: 'join', room: other })); await next(c);
  const got = next(b);
  a.send(JSON.stringify({ type: 'data', payload: Buffer.from('opaque').toString('base64') }));
  assert.deepEqual(await got, { type: 'data', payload: Buffer.from('opaque').toString('base64') });
  a.close(); b.close(); c.close();
});

test.after(() => new Promise(resolve => server.close(resolve)));
