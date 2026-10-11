// Bsync IndexedDB store: a deliberately small bridge. All protocol logic stays in .NET.
//
// Layout (schema version 4), one database per account namespace:
//   records: keyPath ["collection", "id"]; values carry serialized documents as strings and every 64-bit
//            number as a decimal string (JavaScript numbers lose precision above 2^53).
//            Sparse index keys (present only when the record qualifies):
//              pendingKey [collection, updatedAt, id]   dirty and not rejected (push queue order)
//              dirtyKey   [collection, id]              dirty (count)
//              staleKey   [collection, id]              clean and not missing (resnapshot sweep)
//              visibleKey [collection, id]              not missing (queries)
//              liveKey    [collection, id]              not missing and not deleted (queries)
//              conflictKey [collection, id]             an unresolved conflict is kept (schema 2)
//              ixKeys      [[collection, name, key, id], ...] declared secondary indexes (schema 4, ADR-018; one
//                          multiEntry index serves them all). Keys are computed in .NET (record.indexKeys) and kept
//                          only for live, visible records, and only while meta [collection, "indexes"] names the
//                          writer's index set; a writer with another set stores "!" there and queries fall back.
//   meta:    keyPath ["collection", "key"]
//
// Writes are optimistic: .NET reads records with their write stamps, computes new states, and commits in
// one readwrite transaction that aborts if any stamp changed. No transaction spans a .NET await.

const SCHEMA_VERSION = 4;
const databases = new Map();
let nextHandle = 1;

function fail(code, message) {
  const error = new Error(`Bsync:${code}:${message}`);
  error.name = "BsyncStoreError";
  return error;
}

function classify(error) {
  if (error && typeof error.message === "string" && error.message.startsWith("Bsync:")) {
    return error;
  }
  const name = error && error.name;
  if (name === "QuotaExceededError") return fail("quota", "Storage quota exceeded.");
  if (name === "VersionError") return fail("outdated", "The database was upgraded by a newer version of the application.");
  if (name === "InvalidStateError" || name === "SecurityError" || name === "UnknownError") {
    return fail("unavailable", `IndexedDB is unavailable (${name}).`);
  }
  return fail("error", `${name || "Error"}: ${(error && error.message) || error}`);
}

function request(req) {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(classify(req.error));
  });
}

function openDatabase(name, blockedTimeoutMs) {
  return new Promise((resolve, reject) => {
    if (typeof indexedDB === "undefined" || indexedDB === null) {
      reject(fail("unavailable", "IndexedDB is not available in this browser context."));
      return;
    }
    let req;
    try {
      req = indexedDB.open(name, SCHEMA_VERSION);
    } catch (error) {
      reject(classify(error));
      return;
    }
    let blockedTimer = null;
    req.onupgradeneeded = (event) => {
      const db = req.result;
      if (event.oldVersion < 1) {
        const records = db.createObjectStore("records", { keyPath: ["collection", "id"] });
        records.createIndex("pending", "pendingKey");
        records.createIndex("dirty", "dirtyKey");
        records.createIndex("stale", "staleKey");
        records.createIndex("visible", "visibleKey");
        records.createIndex("live", "liveKey");
        db.createObjectStore("meta", { keyPath: ["collection", "key"] });
      }
      if (event.oldVersion < 2) {
        // 1 -> 2: unresolved conflicts. Additive; existing records are untouched.
        req.transaction.objectStore("records").createIndex("conflicts", "conflictKey");
      }
      // 2 -> 3: dependency groups are plain record fields (no index). The version still changes so that tabs running
      // an older application, which would drop the group fields when writing, are closed ("outdated").
      if (event.oldVersion < 4) {
        // 3 -> 4: secondary indexes. Existing records get their keys when a store declaring indexes opens.
        req.transaction.objectStore("records").createIndex("ix", "ixKeys", { multiEntry: true });
      }
    };
    req.onblocked = () => {
      blockedTimer = setTimeout(() => reject(fail("blocked", "Another tab keeps an older version of the database open.")), blockedTimeoutMs);
    };
    req.onsuccess = () => {
      if (blockedTimer) clearTimeout(blockedTimer);
      resolve(req.result);
    };
    req.onerror = () => {
      if (blockedTimer) clearTimeout(blockedTimer);
      reject(classify(req.error));
    };
  });
}

async function connection(handle) {
  const entry = databases.get(handle);
  if (!entry) throw fail("closed", "The store has been disposed.");
  if (entry.closedReason) throw fail(entry.closedReason, "The database was closed because another tab upgraded it; reload the page.");
  return entry.db;
}

export async function open(name, blockedTimeoutMs, keyBase64) {
  const db = await openDatabase(name, blockedTimeoutMs);
  let key;
  try {
    key = await setUpEncryption(db, keyBase64);
  } catch (error) {
    db.close();
    throw classify(error);
  }
  const handle = nextHandle++;
  const entry = { db, name, closedReason: null, key };
  // Another tab wants a newer schema: step aside instead of blocking it, and fail later calls explicitly.
  db.onversionchange = () => {
    db.close();
    entry.closedReason = "outdated";
  };
  db.onclose = () => {
    entry.closedReason = entry.closedReason || "unavailable";
  };
  databases.set(handle, entry);

  // Replica identity is created once per database.
  const tx = db.transaction("meta", "readwrite");
  const meta = tx.objectStore("meta");
  const existing = await request(meta.get(["", "replicaId"]));
  if (!existing) {
    meta.put({ collection: "", key: "replicaId", value: crypto.randomUUID().replaceAll("-", "") });
    meta.put({ collection: "", key: "incarnation", value: crypto.randomUUID().replaceAll("-", "") });
  }
  await transactionDone(tx);
  return handle;
}

export function close(handle) {
  const entry = databases.get(handle);
  if (entry) {
    entry.db.close();
    databases.delete(handle);
  }
}

function transactionDone(tx) {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(classify(tx.error));
    tx.onabort = () => reject(classify(tx.error || new DOMException("Transaction aborted", "AbortError")));
  });
}

function decorate(collection, record, indexed) {
  const id = record.id;
  const rejected = record.rejectionCode !== null && record.rejectionCode !== undefined;
  const stored = { ...record, collection };
  delete stored.pendingKey;
  delete stored.dirtyKey;
  delete stored.staleKey;
  delete stored.visibleKey;
  delete stored.liveKey;
  delete stored.conflictKey;
  delete stored.ixKeys;
  delete stored.indexKeys;
  if (indexed && record.indexKeys && !record.missing && !record.deleted) {
    stored.ixKeys = Object.entries(record.indexKeys).map(([name, key]) => [collection, name, key, id]);
  }
  if (record.isDirty && !rejected) stored.pendingKey = [collection, record.updatedAt, id];
  if (record.isDirty) stored.dirtyKey = [collection, id];
  if (!record.isDirty && !record.missing) stored.staleKey = [collection, id];
  if (!record.missing) stored.visibleKey = [collection, id];
  if (!record.missing && !record.deleted) stored.liveKey = [collection, id];
  if (record.conflictLocal !== null && record.conflictLocal !== undefined) stored.conflictKey = [collection, id];
  return stored;
}

function strip(stored) {
  if (!stored) return null;
  const { collection, pendingKey, dirtyKey, staleKey, visibleKey, liveKey, conflictKey, ixKeys, ...record } = stored;
  return record;
}

// ---- Encryption at rest (ADR-016): AES-GCM over every serialized document of a record, with a random 96-bit nonce per
// value and the collection, id and field as additional data, so a value cannot be moved to another record or field.
// The key comes from the application at open and is kept in memory only (non-extractable). Ids, flags, timestamps,
// versions and declared index keys stay readable: the store needs them for its indexes.
const SEALED = ["current", "base", "pendingPayload", "observed", "conflictServer", "conflictLocal", "conflictBase"];
const PREFIX = "enc1:";

function base64(bytes) {
  let text = "";
  for (const b of bytes) text += String.fromCharCode(b);
  return btoa(text);
}

function unbase64(text) {
  return Uint8Array.from(atob(text), (c) => c.charCodeAt(0));
}

async function seal(key, value, aad) {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const data = new TextEncoder().encode(value);
  const sealed = new Uint8Array(await crypto.subtle.encrypt({ name: "AES-GCM", iv, additionalData: new TextEncoder().encode(aad) }, key, data));
  return PREFIX + base64(iv) + ":" + base64(sealed);
}

async function unseal(key, value, aad) {
  if (typeof value !== "string" || !value.startsWith(PREFIX)) return value; // written before encryption was enabled
  if (!key) throw fail("key", "The database is encrypted and no key was given.");
  const [iv, sealed] = value.substring(PREFIX.length).split(":");
  try {
    const plain = await crypto.subtle.decrypt({ name: "AES-GCM", iv: unbase64(iv), additionalData: new TextEncoder().encode(aad) }, key, unbase64(sealed));
    return new TextDecoder().decode(plain);
  } catch {
    throw fail("key", "A value cannot be decrypted with this key (wrong key, or the data was changed).");
  }
}

function keyOf(handle) {
  const entry = databases.get(handle);
  return entry ? entry.key : null;
}

async function sealRecord(handle, collection, record) {
  const key = keyOf(handle);
  if (!key || !record) return record;
  const sealed = { ...record };
  for (const field of SEALED) {
    if (typeof sealed[field] === "string") sealed[field] = await seal(key, sealed[field], `${collection}\u001f${record.id}\u001f${field}`);
  }
  return sealed;
}

async function unsealRecord(handle, collection, record) {
  if (!record) return record;
  const key = keyOf(handle);
  const plain = { ...record };
  for (const field of SEALED) {
    if (typeof plain[field] === "string") plain[field] = await unseal(key, plain[field], `${collection}\u001f${record.id}\u001f${field}`);
  }
  return plain;
}

function unsealAll(handle, collection, records) {
  return Promise.all(records.map((r) => unsealRecord(handle, collection, r)));
}

// Encryption state of a database: a check value sealed with the key proves later opens use the same key, and refuses
// opening an encrypted database without one (or a plain one with existing data under a key, which would mix states).
async function setUpEncryption(db, keyBase64) {
  const tx = db.transaction("meta", "readonly");
  const check = await request(tx.objectStore("meta").get(["", "encryptionCheck"]));
  if (!keyBase64) {
    if (check) throw fail("key", "The database is encrypted and no key was given.");
    return null;
  }
  const raw = unbase64(keyBase64);
  if (raw.length !== 32) throw fail("key", "An encryption key is 32 bytes.");
  const key = await crypto.subtle.importKey("raw", raw, { name: "AES-GCM" }, false, ["encrypt", "decrypt"]);
  if (check) {
    await unseal(key, check.value, "encryption-check");
  } else {
    const value = await seal(key, "bsync", "encryption-check");
    const write = db.transaction("meta", "readwrite", { durability: "strict" });
    write.objectStore("meta").put({ collection: "", key: "encryptionCheck", value });
    await transactionDone(write);
  }
  return key;
}

export async function readMany(handle, collection, ids) {
  const db = await connection(handle);
  const tx = db.transaction(["records", "meta"], "readonly");
  const records = tx.objectStore("records");
  const meta = tx.objectStore("meta");
  const results = await Promise.all(ids.map((id) => request(records.get([collection, id]))));
  const highWater = await request(meta.get([collection, "clockHighWater"]));
  return JSON.stringify({
    records: await unsealAll(handle, collection, results.map(strip)),
    highWater: highWater ? highWater.value : null,
  });
}

// Whether the stored index keys of a collection were built for `signature` (the writer's or reader's declared set).
// While a rebuild for the same set runs ("!rebuild:<set>:<token>"), writers with that set keep keys too.
async function indexesUsable(meta, collection, signature, writing) {
  const stored = await request(meta.get([collection, "indexes"]));
  const value = (stored && stored.value) || "";
  const set = signature || "";
  return value === set || (writing && value.startsWith(`!rebuild:${set}:`));
}

// writesJson: [{ record, expectedStamp }] for every id in the batch (record null = unchanged).
// Returns "ok" or "conflict" (a stamp changed; the caller re-reads and retries).
export async function commit(handle, collection, entriesJson, metaJson, signature) {
  const db = await connection(handle);
  const entries = JSON.parse(entriesJson);
  // Encrypted before the transaction opens: awaiting WebCrypto inside it would end the transaction.
  for (const entry of entries) {
    if (entry.record) entry.record = await sealRecord(handle, collection, entry.record);
  }
  const metaUpdate = metaJson ? JSON.parse(metaJson) : null;
  // "strict" asks the browser to flush before reporting completion; engines without the option ignore it.
  const tx = db.transaction(["records", "meta"], "readwrite", { durability: "strict" });
  const done = transactionDone(tx);
  const records = tx.objectStore("records");
  const meta = tx.objectStore("meta");
  let outcome = "ok";
  try {
    const current = await Promise.all(entries.map((e) => request(records.get([collection, e.id]))));
    for (let i = 0; i < entries.length; i++) {
      const stamp = current[i] ? current[i].stamp : null;
      if (stamp !== entries[i].expectedStamp) {
        outcome = "conflict";
        break;
      }
    }

    if (outcome === "ok" && metaUpdate && metaUpdate.cursor) {
      const generation = await request(meta.get([collection, "generation"]));
      if (generation && BigInt(generation.value) > BigInt(metaUpdate.cursor.generation)) {
        outcome = "stale-generation";
      }
    }

    if (outcome !== "ok") {
      tx.abort();
      await done.catch(() => {});
      if (outcome === "stale-generation") {
        throw fail("stale-generation", "Another replica session already moved this store to a newer generation.");
      }
      return outcome;
    }

    // A writer that declares other indexes than the stored keys were built for cannot keep them: mark them unusable.
    const indexed = await indexesUsable(meta, collection, signature, true);
    if (!indexed) {
      meta.put({ collection, key: "indexes", value: "!" });
    }

    for (const entry of entries) {
      if (entry.record) {
        const stored = decorate(collection, { ...entry.record, stamp: crypto.randomUUID() }, indexed);
        records.put(stored);
      }
    }

    if (metaUpdate) {
      if (metaUpdate.highWater) {
        const existing = await request(meta.get([collection, "clockHighWater"]));
        if (!existing || existing.value < metaUpdate.highWater) {
          meta.put({ collection, key: "clockHighWater", value: metaUpdate.highWater });
        }
      }
      if (metaUpdate.cursor) {
        if (metaUpdate.cursor.checkpoint === null) {
          meta.delete([collection, "checkpoint"]);
        } else {
          meta.put({ collection, key: "checkpoint", value: metaUpdate.cursor.checkpoint });
        }
        meta.put({ collection, key: "generation", value: metaUpdate.cursor.generation });
        meta.put({ collection, key: "resnapshot", value: metaUpdate.cursor.resnapshot });
        meta.put({ collection, key: "purgeMissing", value: metaUpdate.cursor.purgeMissing });
      }
    }
  } catch (error) {
    try { tx.abort(); } catch { /* already finished */ }
    await done.catch(() => {});
    throw classify(error);
  }
  await done;
  return "ok";
}

async function collect(index, range, limit, accept) {
  const found = [];
  await new Promise((resolve, reject) => {
    const req = index.openCursor(range);
    req.onerror = () => reject(classify(req.error));
    req.onsuccess = () => {
      const cursor = req.result;
      if (!cursor || found.length >= limit) {
        resolve();
        return;
      }
      if (accept(cursor.value)) found.push(strip(cursor.value));
      cursor.continue();
    };
  });
  return found;
}

function prefix(collection) {
  // Every string sorts below an array, so [collection, []] is past every [collection, "..."] key.
  return IDBKeyRange.bound([collection], [collection, []]);
}

// Removes clean records of older generations (not seen by a completed resnapshot); dirty ones are kept.
export async function purge(handle, collection, ids, generation) {
  const db = await connection(handle);
  const g = BigInt(generation);
  const tx = db.transaction("records", "readwrite", { durability: "strict" });
  const done = transactionDone(tx);
  const records = tx.objectStore("records");
  let removed = 0;
  const current = await Promise.all(ids.map((id) => request(records.get([collection, id]))));
  for (const record of current) {
    if (record && !record.isDirty && (record.conflictLocal === null || record.conflictLocal === undefined) && BigInt(record.generation) < g) {
      records.delete([collection, record.id]);
      removed++;
    }
  }
  await done;
  return removed;
}

export async function pending(handle, collection, limit, exclude) {
  const db = await connection(handle);
  const excluded = new Set(exclude);
  const index = db.transaction("records", "readonly").objectStore("records").index("pending");
  return JSON.stringify(await unsealAll(handle, collection, await collect(index, prefix(collection), limit, (r) => !excluded.has(r.id))));
}

export async function stale(handle, collection, generation, limit) {
  const db = await connection(handle);
  const g = BigInt(generation);
  const index = db.transaction("records", "readonly").objectStore("records").index("stale");
  return JSON.stringify(await unsealAll(handle, collection, await collect(index, prefix(collection), limit, (r) => BigInt(r.generation) < g)));
}

export async function conflicts(handle, collection, limit) {
  const db = await connection(handle);
  const index = db.transaction("records", "readonly").objectStore("records").index("conflicts");
  return JSON.stringify(await unsealAll(handle, collection, await collect(index, prefix(collection), limit, () => true)));
}

// Rejected records are dirty, so the sparse dirty index bounds the scan.
export async function rejected(handle, collection, limit) {
  const db = await connection(handle);
  const index = db.transaction("records", "readonly").objectStore("records").index("dirty");
  return JSON.stringify(await unsealAll(handle, collection, await collect(index, prefix(collection), limit, (r) => r.rejectionCode !== null && r.rejectionCode !== undefined)));
}

// One bounded page in id order; string keys compare by UTF-16 code units, which is ordinal order.
export async function queryPage(handle, collection, afterId, limit, includeDeleted) {
  const db = await connection(handle);
  const index = db.transaction("records", "readonly").objectStore("records").index(includeDeleted ? "visible" : "live");
  const range = afterId === null || afterId === undefined
    ? prefix(collection)
    : IDBKeyRange.bound([collection, afterId], [collection, []], true, false);
  const records = await collect(index, range, limit, () => true);
  return JSON.stringify(await Promise.all(records.map((r) => unseal(keyOf(handle), r.current, `${collection}\u001f${r.id}\u001fcurrent`))));
}

function indexRange(collection, name, lower, lowerOpen, upper, upperOpen, afterKey, afterId, descending) {
  // Entries are [collection, name, key, id]. An array sorts after every string, so [.., key, []] follows every id of key.
  let low = lower === null || lower === undefined ? [collection, name] : (lowerOpen ? [collection, name, lower, []] : [collection, name, lower]);
  let high = upper === null || upper === undefined ? [collection, name, []] : (upperOpen ? [collection, name, upper] : [collection, name, upper, []]);
  let lowOpen = false;
  let highOpen = false;
  if (afterKey !== null && afterKey !== undefined) {
    const after = [collection, name, afterKey, afterId];
    if (descending) {
      if (indexedDB.cmp(after, high) <= 0) { high = after; highOpen = true; }
    } else if (indexedDB.cmp(after, low) >= 0) {
      low = after; lowOpen = true;
    }
  }
  const order = indexedDB.cmp(low, high);
  if (order > 0 || (order === 0 && (lowOpen || highOpen))) return null;
  return IDBKeyRange.bound(low, high, lowOpen, highOpen);
}

// One page of an index range, or "null" when the stored keys are not usable for `signature` (the caller evaluates
// the query itself).
export async function queryIndex(handle, collection, signature, name, lower, lowerOpen, upper, upperOpen, afterKey, afterId, descending, limit) {
  const db = await connection(handle);
  const tx = db.transaction(["records", "meta"], "readonly");
  if (!(await indexesUsable(tx.objectStore("meta"), collection, signature, false))) return "null";
  const range = indexRange(collection, name, lower, lowerOpen, upper, upperOpen, afterKey, afterId, descending);
  if (range === null) return "[]";
  const found = [];
  await new Promise((resolve, reject) => {
    const req = tx.objectStore("records").index("ix").openCursor(range, descending ? "prev" : "next");
    req.onerror = () => reject(classify(req.error));
    req.onsuccess = () => {
      const cursor = req.result;
      if (!cursor || found.length >= limit) {
        resolve();
        return;
      }
      found.push({ id: cursor.value.id, current: cursor.value.current });
      cursor.continue();
    };
  });
  return JSON.stringify(await Promise.all(found.map((r) => unseal(keyOf(handle), r.current, `${collection}\u001f${r.id}\u001fcurrent`))));
}

export async function countIndex(handle, collection, signature, name, lower, lowerOpen, upper, upperOpen) {
  const db = await connection(handle);
  const tx = db.transaction(["records", "meta"], "readonly");
  if (!(await indexesUsable(tx.objectStore("meta"), collection, signature, false))) return -1;
  const range = indexRange(collection, name, lower, lowerOpen, upper, upperOpen, null, null, false);
  return range === null ? 0 : await request(tx.objectStore("records").index("ix").count(range));
}

// Rebuilding keys (a store declaring another index set opened): first mark the keys unusable, then page through the
// live records (.NET computes keys), then store them where the record did not change meanwhile, then record the set.
// Returns the rebuild token, or "" when the keys are already built for `signature`.
export async function beginReindex(handle, collection, signature) {
  const db = await connection(handle);
  const tx = db.transaction("meta", "readwrite", { durability: "strict" });
  const meta = tx.objectStore("meta");
  const stored = await request(meta.get([collection, "indexes"]));
  if (((stored && stored.value) || "") === signature) {
    await transactionDone(tx);
    return "";
  }
  const token = `!rebuild:${signature}:${crypto.randomUUID()}`;
  meta.put({ collection, key: "indexes", value: token });
  await transactionDone(tx);
  return token;
}

export async function scanLive(handle, collection, afterId, limit) {
  const db = await connection(handle);
  const index = db.transaction("records", "readonly").objectStore("records").index("live");
  const range = afterId === null || afterId === undefined
    ? prefix(collection)
    : IDBKeyRange.bound([collection, afterId], [collection, []], true, false);
  const records = await collect(index, range, limit, () => true);
  return JSON.stringify(await Promise.all(records.map(async (r) => ({ id: r.id, stamp: r.stamp, current: await unseal(keyOf(handle), r.current, `${collection}\u001f${r.id}\u001fcurrent`) }))));
}

// entriesJson: [{ id, stamp, indexKeys }]. Records whose stamp changed were rewritten meanwhile (with keys); skipped.
export async function reindex(handle, collection, entriesJson) {
  const db = await connection(handle);
  const entries = JSON.parse(entriesJson);
  const tx = db.transaction("records", "readwrite", { durability: "strict" });
  const done = transactionDone(tx);
  const records = tx.objectStore("records");
  const current = await Promise.all(entries.map((e) => request(records.get([collection, e.id]))));
  for (let i = 0; i < entries.length; i++) {
    const stored = current[i];
    if (stored && stored.stamp === entries[i].stamp) {
      records.put(decorate(collection, { ...strip(stored), indexKeys: entries[i].indexKeys }, true));
    }
  }
  await done;
}

export async function endReindex(handle, collection, signature, token) {
  const db = await connection(handle);
  const tx = db.transaction("meta", "readwrite", { durability: "strict" });
  const meta = tx.objectStore("meta");
  const stored = await request(meta.get([collection, "indexes"]));
  // A writer with another set ("!") or another rebuild replaced the token meanwhile: the keys stay unusable.
  const finished = stored && stored.value === token;
  if (finished) meta.put({ collection, key: "indexes", value: signature });
  await transactionDone(tx);
  return !!finished;
}

// The index set the stored keys were built for ("" for none, "!" when unusable); diagnostics and tests.
export async function indexState(handle, collection) {
  const db = await connection(handle);
  const stored = await request(db.transaction("meta", "readonly").objectStore("meta").get([collection, "indexes"]));
  return (stored && stored.value) || "";
}

export async function query(handle, collection, includeDeleted) {
  const db = await connection(handle);
  const index = db.transaction("records", "readonly").objectStore("records").index(includeDeleted ? "visible" : "live");
  const records = await collect(index, prefix(collection), Number.MAX_SAFE_INTEGER, () => true);
  return JSON.stringify(await Promise.all(records.map((r) => unseal(keyOf(handle), r.current, `${collection}\u001f${r.id}\u001fcurrent`))));
}

export async function countDirty(handle, collection) {
  const db = await connection(handle);
  const index = db.transaction("records", "readonly").objectStore("records").index("dirty");
  return await request(index.count(prefix(collection)));
}

export async function getMeta(handle, collection) {
  const db = await connection(handle);
  const store = db.transaction("meta", "readonly").objectStore("meta");
  const read = async (scope, key) => {
    const value = await request(store.get([scope, key]));
    return value ? value.value : null;
  };
  return JSON.stringify({
    checkpoint: await read(collection, "checkpoint"),
    generation: (await read(collection, "generation")) ?? "0",
    resnapshot: (await read(collection, "resnapshot")) ?? false,
    purgeMissing: (await read(collection, "purgeMissing")) ?? false,
    highWater: await read(collection, "clockHighWater"),
    replicaId: await read("", "replicaId"),
    incarnation: await read("", "incarnation"),
  });
}

// Sets the clock high-water mark, also lower (ADR-017 part 2: after re-stamping writes rejected for clock skew).
export async function setHighWater(handle, collection, value) {
  const db = await connection(handle);
  const tx = db.transaction("meta", "readwrite", { durability: "strict" });
  tx.objectStore("meta").put({ collection, key: "clockHighWater", value });
  await transactionDone(tx);
}

export async function newIncarnation(handle) {
  const db = await connection(handle);
  const tx = db.transaction("meta", "readwrite");
  tx.objectStore("meta").put({ collection: "", key: "incarnation", value: crypto.randomUUID().replaceAll("-", "") });
  await transactionDone(tx);
}

export async function deleteDatabase(name) {
  await request(indexedDB.deleteDatabase(name));
}

export async function requestPersistence() {
  return !!(navigator.storage && navigator.storage.persist && (await navigator.storage.persist()));
}

export async function estimate() {
  if (!navigator.storage || !navigator.storage.estimate) return JSON.stringify({ usage: null, quota: null });
  const { usage, quota } = await navigator.storage.estimate();
  return JSON.stringify({ usage: usage ?? null, quota: quota ?? null });
}

// ---- Lifecycle: ask .NET to sync when the network returns or the tab becomes visible again (timers may have
// been throttled or frozen while it was hidden). navigator.onLine is never treated as proof the server is up.
const watches = new Map();
let nextWatch = 1;

export function watchLifecycle(dotnetRef) {
  const wake = () => dotnetRef.invokeMethodAsync("Wake").catch(() => { });
  const onVisibility = () => { if (document.visibilityState === "visible") wake(); };
  window.addEventListener("online", wake);
  document.addEventListener("visibilitychange", onVisibility);
  const id = nextWatch++;
  watches.set(id, () => {
    window.removeEventListener("online", wake);
    document.removeEventListener("visibilitychange", onVisibility);
  });
  return id;
}

export function unwatchLifecycle(id) {
  const stop = watches.get(id);
  if (stop) {
    watches.delete(id);
    stop();
  }
}

// ---- Tab ownership (Web Locks). The lock is released when the holder releases it, or when its tab closes
// or crashes, so a stale owner cannot keep it.
const leases = new Map();
let nextLease = 1;

export async function tryAcquireLease(name) {
  if (!navigator.locks) throw fail("unavailable", "Web Locks are not available in this browser.");
  let release;
  const released = new Promise((resolve) => { release = resolve; });
  const acquired = await new Promise((resolve) => {
    navigator.locks.request(name, { ifAvailable: true }, (lock) => {
      if (!lock) {
        resolve(false);
        return undefined;
      }
      resolve(true);
      return released;
    });
  });
  if (!acquired) return 0;
  const id = nextLease++;
  leases.set(id, release);
  return id;
}

export function releaseLease(id) {
  const release = leases.get(id);
  if (release) {
    leases.delete(id);
    release();
  }
}
