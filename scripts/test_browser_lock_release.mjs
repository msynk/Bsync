// A Web Lock's callback completion and physical release are separate asynchronous steps.
// The provider's release promise must join the request, including duplicate release callers.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { setImmediate } from 'node:timers/promises';
import { webcrypto } from 'node:crypto';

let occupied = false;
let finishRelease;
Object.defineProperty(globalThis, 'crypto', { value: webcrypto, configurable: true });
Object.defineProperty(globalThis, 'navigator', { value: { locks: {
  async request(name, options, callback) {
    if (occupied) return callback(null);
    occupied = true;
    await callback({ name, mode: options.mode });
    await new Promise(resolve => { finishRelease = resolve; });
    occupied = false;
  }
}}, configurable: true });

const file = new URL('../src/Bsync/Bsync.Blazor/wwwroot/bsync-cache-locks.js', import.meta.url);
const source = await readFile(file, 'utf8');
const locks = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
for (const acquire of [() => locks.tryAcquireAccount('account', true), () => locks.tryAcquire('cache')]) {
  const token = await acquire();
  assert.ok(token);
  assert.equal(await acquire(), null, 'held lifetime excludes another owner');
  let settled = false;
  let duplicateSettled = false;
  const release = Promise.resolve(locks.release(token)).then(() => { settled = true; });
  const duplicate = Promise.resolve(locks.release(token)).then(() => { duplicateSettled = true; });
  await setImmediate();
  assert.equal(settled, false, 'release must wait for physical Web Lock completion');
  assert.equal(duplicateSettled, false, 'duplicate release must join the same completion');
  assert.equal(occupied, true);
  assert.equal(await acquire(), null);
  finishRelease();
  await Promise.all([release, duplicate]);
  assert.equal(occupied, false);
  await locks.release(token); // idempotent after completion
}
console.log('Account/cache lock release joins physical completion and concurrent disposal.');
