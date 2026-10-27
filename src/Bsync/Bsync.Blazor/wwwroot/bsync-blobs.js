// Bsync browser blob store (task F1): content-addressed attachment bytes in IndexedDB, one database per store
// name ("bsync-blobs-<name>", object store "blobs"):
//   ["object", sha256]                verified content; it appears only after its hash was checked
//   ["partial", sha256, index]        chunks of an upload or download in progress (resumable after a reload)
// New complete objects use immutable Blob handles and bounded slices. Private WebKit configurations that refuse Blob
// persistence retain the ArrayBuffer compatibility path only up to 16 MiB, never an unbounded whole-file fallback.
// Quota failures surface as errors ("Bsync:quota:..."), never as silent success.

const databases = new Map();
const blobPersistence = new WeakMap();
// Immutable reader references live only in this realm; tab/process termination releases them automatically.
const snapshots = new Map();
const verifications = new Map();
const compatibilityLimit = 16 * 1024 * 1024;

export function openSnapshot(name, sha) {
  return run(name, async (db) => {
    const content = await request(db.transaction("blobs", "readonly").objectStore("blobs").get(["object", sha]));
    if (!content) return null;
    const id = crypto.randomUUID();
    snapshots.set(id, content);
    return [id, String(sizeOf(content))];
  });
}

export async function readSnapshot(id, offset, count) {
  const content = snapshots.get(id);
  if (!content) throw fail("missing", "The reader has been released.");
  return new Uint8Array(content instanceof Blob ? await content.slice(offset, offset + count).arrayBuffer() : content.slice(offset, offset + count));
}

export function releaseSnapshot(id) { snapshots.delete(id); verifications.delete(id); }

function fail(code, message) {
  const error = new Error(`Bsync:${code}:${message}`);
  error.name = "BsyncStoreError";
  return error;
}

function classify(error) {
  if (error && typeof error.message === "string" && error.message.startsWith("Bsync:")) return error;
  const name = error && error.name;
  if (name === "QuotaExceededError") return fail("quota", "Storage quota exceeded.");
  if (name === "InvalidStateError" || name === "SecurityError" || name === "UnknownError") return fail("unavailable", `IndexedDB is unavailable (${name}).`);
  return fail("error", `${name || "Error"}: ${(error && error.message) || error}`);
}

function request(req) {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(classify(req.error));
  });
}

function done(tx) {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(classify(tx.error));
    tx.onabort = () => reject(classify(tx.error || new DOMException("Transaction aborted", "AbortError")));
  });
}

async function open(name) {
  if (databases.has(name)) return databases.get(name);
  if (typeof indexedDB === "undefined" || indexedDB === null) throw fail("unavailable", "IndexedDB is not available in this browser context.");
  const db = await new Promise((resolve, reject) => {
    let abandoned = false;
    const req = indexedDB.open(`bsync-blobs-${name}`, 3);
    req.onupgradeneeded = event => {
      if (abandoned) { req.transaction.abort(); return; }
      if (event.oldVersion === 0) req.result.createObjectStore("blobs");
      if (event.oldVersion < 2) {
        const capacity = req.result.createObjectStore("capacity");
        capacity.put({ version: 1, ready: event.oldVersion === 0, total: 0, after: null }, ["state"]);
      }
    };
    req.onblocked = () => { abandoned = true; reject(fail("unavailable", "Close older blob-store tabs before upgrading.")); };
    req.onsuccess = () => { if (abandoned) req.result.close(); else resolve(req.result); };
    req.onerror = () => reject(classify(req.error));
  });
  db.onversionchange = () => { db.close(); databases.delete(name); };
  databases.set(name, db);
  // Legacy upgrades resume bounded reconciliation pages before new writes are admitted.
  while (!await reconcilePage(db, 64, false)) { /* each page is a separate durable transaction */ }
  return db;
}

async function supportsBlobs(db) {
  if (!blobPersistence.has(db)) blobPersistence.set(db, (async () => {
    // A separate transaction cannot strand a real partial when a private browser rejects Blob cloning.
    const tx = db.transaction("capacity", "readwrite");
    const completion = done(tx);
    try {
      const store = tx.objectStore("capacity");
      store.put(new Blob([new Uint8Array(1)]), ["blob-probe"]);
      store.delete(["blob-probe"]);
      await completion; return true;
    } catch {
      try { tx.abort(); } catch { }
      await completion.catch(() => {}); return false;
    }
  })());
  return await blobPersistence.get(db);
}

const sizeOf = (value) => (value instanceof Blob ? value.size : value.byteLength);

const partialRange = (sha) => IDBKeyRange.bound(["partial", sha], ["partial", sha, []]);

async function run(name, action) {
  try {
    return await action(await open(name));
  } catch (error) {
    throw classify(error);
  }
}

export function size(name, sha) {
  return run(name, async (db) => {
    const size = await request(db.transaction("capacity", "readonly").objectStore("capacity").get(["object", sha]));
    return size ?? -1;
  });
}

export function partialLength(name, sha) {
  return run(name, async db => {
    const tx = db.transaction(["blobs", "capacity"], "readonly");
    const payloads = tx.objectStore("blobs"), capacity = tx.objectStore("capacity");
    const state = await request(capacity.get(["state"]));
    validateState(state);
    if (!state.ready) throw fail("accounting-uncertain", "Capacity reconciliation is pending.");
    const keys = await request(payloads.getAllKeys(partialRange(sha)));
    const sizes = await Promise.all(keys.map(key => request(capacity.get(key))));
    if (sizes.some(size => !Number.isSafeInteger(size) || size < 0)) throw fail("accounting-uncertain", "Partial accounting is missing; reconcile before resuming.");
    return sizes.reduce((sum, size) => sum + size, 0);
  });
}

export function appendPartial(name, sha, bytes) {
  return run(name, db => mutate(db, async (payloads, capacity, put) => {
    const last = await request(capacity.openKeyCursor(partialRange(sha), "prev"));
    const index = last ? last.key[2] + 1 : 0;
    if (!Number.isSafeInteger(index)) throw fail("accounting-uncertain", "Partial index exhausted.");
    for (let offset = 0, part = index; offset < bytes.length; offset += 1024 * 1024, part++)
      await put(["partial", sha, part], bytes.slice(offset, offset + 1024 * 1024).buffer);
  }));
}

// Snapshot partial bytes a chunk at a time into an immutable Blob. The Blob constructor owns each bounded copy;
// no getAll(payloads), full-file ArrayBuffer or one-shot hash is used by the managed provider.
export function beginPartialVerification(name, sha) {
  return run(name, async db => {
    const tx = db.transaction(["blobs", "capacity"], "readonly");
    const completion = done(tx);
    const generation = await request(tx.objectStore("capacity").get(["partial-generation", sha]));
    let content = new Blob([]);
    await new Promise((resolve, reject) => {
      const req = tx.objectStore("blobs").openCursor(partialRange(sha));
      req.onerror = () => reject(req.error);
      req.onsuccess = () => {
        const cursor = req.result;
        if (!cursor) { resolve(); return; }
        content = new Blob([content, cursor.value]);
        cursor.continue();
      };
    });
    await completion;
    const id = crypto.randomUUID();
    snapshots.set(id, content);
    verifications.set(id, { name, sha, generation });
    return [id, String(content.size)];
  });
}

export function commitPartialVerification(name, sha, id, verified) {
  return run(name, async db => {
    const snapshot = verifications.get(id), content = snapshots.get(id);
    if (!snapshot || snapshot.name !== name || snapshot.sha !== sha || !content) throw fail("missing", "Unknown verification snapshot.");
    const commit = value => mutate(db, async (payloads, capacity, put, remove) => {
      if (await request(capacity.get(["partial-generation", sha])) !== snapshot.generation)
        throw fail("conflict", "Partial changed during verification; retry without discarding progress.");
      if (verified) await put(["object", sha], value);
      await remove(partialRange(sha));
    });
    if (!verified || await supportsBlobs(db)) await commit(content);
    else {
      if (content.size > compatibilityLimit) throw fail("unavailable", "This browser session refuses Blob persistence; raw content is limited to 16 MiB. Partial bytes retained.");
      await commit(await content.arrayBuffer());
    }
    return verified;
  });
}

// Compatibility entry point for old JS consumers, deliberately bounded. .NET uses streaming verification above.
export function completePartial(name, sha) {
  return (async () => {
    const [id, length] = await beginPartialVerification(name, sha);
    try {
      if (Number(length) > compatibilityLimit) throw fail("unavailable", "Use the streaming managed provider for content above 16 MiB.");
      const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", await snapshots.get(id).arrayBuffer()));
      return await commitPartialVerification(name, sha, id, Array.from(digest, b => b.toString(16).padStart(2, "0")).join("") === sha);
    } finally { releaseSnapshot(id); }
  })();
}

// Moves the chunks of a staged import under its hash, once the hash is known.
export function renamePartial(name, from, to) {
  return run(name, async (db) => {
    if (from === to) return;
    await mutate(db, async (payloads, capacity, put, remove) => {
      await remove(partialRange(to));
      const keys = await request(payloads.getAllKeys(partialRange(from)));
      for (let i = 0; i < keys.length; i++) await put(["partial", to, i], await request(payloads.get(keys[i])));
      await remove(partialRange(from));
    });
  });
}

export function discardPartial(name, sha) {
  return run(name, async (db) => {
    await mutate(db, async (payloads, capacity, put, remove) => await remove(partialRange(sha)));
  });
}

export function readRange(name, sha, offset, count) {
  return run(name, async (db) => {
    const blob = await request(db.transaction("blobs", "readonly").objectStore("blobs").get(["object", sha]));
    if (!blob) throw fail("missing", "The content is not on this device.");
    return new Uint8Array(blob instanceof Blob ? await blob.slice(offset, offset + count).arrayBuffer() : blob.slice(offset, offset + count));
  });
}

export function remove(name, sha) {
  return run(name, async (db) => {
    await mutate(db, async (payloads, capacity, put, remove) => await remove(["object", sha]));
  });
}

export function list(name) {
  return run(name, async (db) => {
    const keys = await request(db.transaction("blobs", "readonly").objectStore("blobs").getAllKeys(IDBKeyRange.bound(["object"], ["object", []])));
    return keys.map((key) => key[1]);
  });
}

export function listPage(name, after, limit) {
  if (!Number.isInteger(limit) || limit < 1 || limit > 1000) throw new RangeError('Invalid page limit.');
  return run(name, async db => {
    const range = IDBKeyRange.bound(after === null ? ['object'] : ['object', after], ['object', []], after !== null, true);
    const keys = await request(db.transaction('blobs', 'readonly').objectStore('blobs').getAllKeys(range, limit));
    return keys.map(key => key[1]);
  });
}

export function storedBytes(name) {
  return run(name, async db => {
    const state = await request(db.transaction("capacity", "readonly").objectStore("capacity").get(["state"]));
    validateState(state);
    if (!state.ready) throw fail("accounting-uncertain", "Capacity reconciliation is pending.");
    return state.total;
  });
}

function validateState(state) {
  if (!state || state.version !== 1 || !Number.isSafeInteger(state.total) || state.total < 0)
    throw fail("accounting-uncertain", "Unsupported or invalid capacity ledger; payloads retained.");
}

// Payload mutations and all charged bytes commit together. Includes staging/partial, complete,
// retired-reader and quarantined ciphertext records; excludes metadata/index/JS snapshot overhead.
async function mutate(db, action) {
  const tx = db.transaction(["blobs", "capacity"], "readwrite", { durability: "strict" });
  const completion = done(tx);
  const payloads = tx.objectStore("blobs"), capacity = tx.objectStore("capacity");
  try {
    const state = await request(capacity.get(["state"]));
    validateState(state);
    if (!state.ready) throw fail("accounting-uncertain", "Capacity reconciliation is pending.");
    const put = async (key, value) => {
      const previous = await request(capacity.get(key)) ?? 0;
      state.total += sizeOf(value) - previous;
      capacity.put(sizeOf(value), key); payloads.put(value, key);
      if (key[0] === "partial") capacity.put(crypto.randomUUID(), ["partial-generation", key[1]]);
    };
    const remove = async range => {
      await new Promise((resolve, reject) => {
        const req = capacity.openCursor(range);
        req.onerror = () => reject(req.error);
        req.onsuccess = () => {
          const cursor = req.result;
          if (!cursor) { resolve(); return; }
          state.total -= cursor.value;
          if (cursor.key[0] === "partial") capacity.delete(["partial-generation", cursor.key[1]]);
          cursor.delete(); cursor.continue();
        };
      });
      payloads.delete(range);
    };
    // Observe transaction failure immediately: some engines never settle pending cursor requests after abort.
    await Promise.all([completion, (async () => {
      await action(payloads, capacity, put, remove);
      validateState(state);
      capacity.put(state, ["state"]);
    })()]);
  } catch (error) {
    try { tx.abort(); } catch { /* already aborted */ }
    await completion.catch(() => {});
    throw error;
  }
}

async function reconcilePage(db, limit, restart) {
  const tx = db.transaction(["blobs", "capacity"], "readwrite", { durability: "strict" });
  const completion = done(tx);
  const capacity = tx.objectStore("capacity");
  try {
    let state = await request(capacity.get(["state"]));
    validateState(state);
    if (restart) {
      state = { version: 1, ready: false, total: 0, after: null };
      capacity.clear();
    }
    if (!state.ready) {
      let count = 0;
      await new Promise((resolve, reject) => {
        const req = tx.objectStore("blobs").openCursor(state.after === null ? null : IDBKeyRange.lowerBound(state.after, true));
        req.onerror = () => reject(req.error);
        req.onsuccess = () => {
          const cursor = req.result;
          if (!cursor) { state.ready = true; resolve(); return; }
          const size = sizeOf(cursor.value);
          state.total += size; state.after = cursor.key;
          capacity.put(size, cursor.key);
          if (++count === limit) resolve(); else cursor.continue();
        };
      });
      validateState(state);
      capacity.put(state, ["state"]);
    }
    await completion;
    return state.ready;
  } catch (error) {
    try { tx.abort(); } catch { }
    await completion.catch(() => {});
    throw error;
  }
}

// Explicit repair after external modification. Writes refuse until all pages finish; readers remain available.
export function reconcileCapacity(name, limit, restart) {
  if (!Number.isInteger(limit) || limit < 1 || limit > 1000) throw new RangeError("Invalid reconciliation page size.");
  return run(name, db => reconcilePage(db, limit, restart));
}

export async function estimateStorage() {
  if (!navigator.storage?.estimate) return [-1, -1];
  const value = await navigator.storage.estimate();
  const safe = number => Number.isSafeInteger(number) && number >= 0 ? number : -1;
  return [safe(value.usage), safe(value.quota)];
}
export async function requestPersistence() {
  return !!(navigator.storage?.persist && await navigator.storage.persist());
}

export async function clear(name) {
  try {
    const db = databases.get(name);
    if (db) {
      db.close();
      databases.delete(name);
    }
    await request(indexedDB.deleteDatabase(`bsync-blobs-${name}`));
  } catch (error) {
    throw classify(error);
  }
}
