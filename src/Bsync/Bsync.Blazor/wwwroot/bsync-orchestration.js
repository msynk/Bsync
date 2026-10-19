// Separate schema from replicated documents and blobs. Never upgrade a newer database implicitly.
function open(name) {
  return new Promise((resolve, reject) => {
    let abandoned = false;
    const request = indexedDB.open(name);
    request.onupgradeneeded = () => request.result.createObjectStore('records', { keyPath: 'key' });
    request.onerror = () => reject(request.error);
    request.onblocked = () => { abandoned = true; reject(new Error('Orchestration upgrade blocked by another tab.')); };
    request.onsuccess = () => {
      if (abandoned) request.result.close();
      else if (request.result.version > 2) { request.result.close(); reject(new DOMException('Unsupported orchestration version.', 'VersionError')); }
      else resolve(request.result);
    };
  });
}

function request(req) {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function transaction(name, mode, action) {
  const db = await open(name);
  try {
    const tx = db.transaction('records', mode, mode === 'readwrite' ? { durability: 'strict' } : {});
    const done = new Promise((resolve, reject) => {
      tx.oncomplete = resolve;
      tx.onabort = () => reject(tx.error || new Error('Orchestration transaction aborted.'));
      tx.onerror = () => {}; // abort owns rejection
    });
    try {
      const value = await action(tx.objectStore('records'), tx);
      await done;
      return JSON.stringify(value);
    } catch (error) {
      try { tx.abort(); } catch { /* already completed/aborted */ }
      await done.catch(() => {});
      throw error;
    }
  } finally { db.close(); }
}

export async function initialize(name) { const db = await open(name); db.close(); return 'true'; }
export async function requireFeatureVersion(name) {
  const existing = await open(name);
  const version = existing.version;
  existing.close();
  if (version >= 2) return 'true';
  return new Promise((resolve, reject) => {
    let abandoned = false;
    const upgrade = indexedDB.open(name, 2);
    upgrade.onblocked = () => { abandoned = true; reject(new Error('Close older tabs before enabling protected formats.')); };
    upgrade.onupgradeneeded = () => { if (abandoned) upgrade.transaction.abort(); };
    upgrade.onerror = () => reject(upgrade.error);
    upgrade.onsuccess = () => { upgrade.result.close(); if (!abandoned) resolve('true'); };
  });
}
export function read(name, key) { return transaction(name, 'readonly', async store => (await request(store.get(key))) || null); }
export function clear(name) { return transaction(name, 'readwrite', async store => { await request(store.clear()); return true; }); }

export function page(name, prefix, afterKey, limit) {
  return transaction(name, 'readonly', store => new Promise((resolve, reject) => {
    const lower = afterKey !== null && afterKey >= prefix ? afterKey : prefix;
    const upper = prefix + '\x7f';
    if (lower >= upper) { resolve([]); return; }
    const range = IDBKeyRange.bound(lower, upper, afterKey !== null && afterKey >= prefix, true);
    const result = [];
    const cursor = store.openCursor(range);
    cursor.onerror = () => reject(cursor.error);
    cursor.onsuccess = () => {
      const current = cursor.result;
      if (!current || result.length === limit) { resolve(result); return; }
      result.push(current.value);
      current.continue();
    };
  }));
}

export function write(name, mutationsJson, fenceKey, fenceToken) {
  const mutations = JSON.parse(mutationsJson);
  return transaction(name, 'readwrite', async (store, tx) => {
    const lease = fenceKey ? await request(store.get(fenceKey)) : null;
    const valid = () => !fenceKey || (lease && lease.payload === fenceToken && lease.expiresAtUnixMilliseconds > Date.now());
    if (!valid()) return false;
    for (const mutation of mutations) {
      const current = await request(store.get(mutation.key));
      if ((current?.revision ?? null) !== mutation.expectedRevision) return false;
    }
    if (!valid()) return false;
    for (const mutation of mutations) {
      if (mutation.payload === null) store.delete(mutation.key);
      else store.put({ key: mutation.key, revision: crypto.randomUUID(), payload: mutation.payload,
        expiresAtUnixMilliseconds: mutation.expiresAtUnixMilliseconds });
    }
    // No await or external I/O between this last fence check and queued writes/commit.
    if (!valid()) { tx.abort(); throw new Error('Execution fence expired before commit.'); }
    return true;
  });
}
