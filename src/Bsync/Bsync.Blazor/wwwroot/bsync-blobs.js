// Bsync browser blob store (task F1): content-addressed attachment bytes in IndexedDB, one database per store
// name ("bsync-blobs-<name>", object store "blobs"):
//   ["object", sha256]                verified content; it appears only after its hash was checked
//   ["partial", sha256, index]        chunks of an upload or download in progress (resumable after a reload)
// IndexedDB rather than Cache Storage: in the WebKit build used for tests, Cache Storage did not survive a reload while
// IndexedDB did. Values are ArrayBuffers, not Blobs: WebKit refuses Blobs in IndexedDB in ephemeral (private) sessions.
// Quota failures surface as errors ("Bsync:quota:..."), never as silent success.

const databases = new Map();

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
    const req = indexedDB.open(`bsync-blobs-${name}`, 1);
    req.onupgradeneeded = () => req.result.createObjectStore("blobs");
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(classify(req.error));
  });
  db.onversionchange = () => { db.close(); databases.delete(name); };
  databases.set(name, db);
  return db;
}

const sizeOf = (value) => (value instanceof Blob ? value.size : value.byteLength);

const partialRange = (sha) => IDBKeyRange.bound(["partial", sha], ["partial", sha, []]);

async function partials(db, sha) {
  const tx = db.transaction("blobs", "readonly");
  const store = tx.objectStore("blobs");
  const [keys, values] = await Promise.all([request(store.getAllKeys(partialRange(sha))), request(store.getAll(partialRange(sha)))]);
  return keys.map((key, i) => ({ key, blob: values[i] }));
}

async function run(name, action) {
  try {
    return await action(await open(name));
  } catch (error) {
    throw classify(error);
  }
}

export function size(name, sha) {
  return run(name, async (db) => {
    const blob = await request(db.transaction("blobs", "readonly").objectStore("blobs").get(["object", sha]));
    return blob ? sizeOf(blob) : -1;
  });
}

export function partialLength(name, sha) {
  return run(name, async (db) => (await partials(db, sha)).reduce((total, p) => total + sizeOf(p.blob), 0));
}

export function appendPartial(name, sha, bytes) {
  return run(name, async (db) => {
    const count = (await partials(db, sha)).length;
    const tx = db.transaction("blobs", "readwrite", { durability: "strict" });
    tx.objectStore("blobs").put(bytes.slice().buffer, ["partial", sha, count]);
    await done(tx);
  });
}

// Joins the chunks, checks the SHA-256 and makes them the content in one transaction; a mismatch discards them.
export function completePartial(name, sha) {
  return run(name, async (db) => {
    const parts = await partials(db, sha);
    const content = await new Blob(parts.map((p) => p.blob)).arrayBuffer();
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", content));
    const verified = Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join("") === sha;
    const tx = db.transaction("blobs", "readwrite", { durability: "strict" });
    const store = tx.objectStore("blobs");
    if (verified) store.put(content, ["object", sha]);
    store.delete(partialRange(sha));
    await done(tx);
    return verified;
  });
}

// Moves the chunks of a staged import under its hash, once the hash is known.
export function renamePartial(name, from, to) {
  return run(name, async (db) => {
    const parts = await partials(db, from);
    const tx = db.transaction("blobs", "readwrite", { durability: "strict" });
    const store = tx.objectStore("blobs");
    parts.forEach((p, i) => store.put(p.blob, ["partial", to, i]));
    store.delete(partialRange(from));
    await done(tx);
  });
}

export function discardPartial(name, sha) {
  return run(name, async (db) => {
    const tx = db.transaction("blobs", "readwrite");
    tx.objectStore("blobs").delete(partialRange(sha));
    await done(tx);
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
    const tx = db.transaction("blobs", "readwrite");
    tx.objectStore("blobs").delete(["object", sha]);
    await done(tx);
  });
}

export function list(name) {
  return run(name, async (db) => {
    const keys = await request(db.transaction("blobs", "readonly").objectStore("blobs").getAllKeys(IDBKeyRange.bound(["object"], ["object", []])));
    return keys.map((key) => key[1]);
  });
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
