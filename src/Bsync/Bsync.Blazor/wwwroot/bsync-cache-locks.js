const held = new Map();
export function tryAcquire(name) {
  return acquire('bsync-cache/' + name, 'exclusive');
}
export function tryAcquireAccount(name, exclusive) {
  return acquire('bsync-account/' + name, exclusive ? 'exclusive' : 'shared');
}
function acquire(name, mode) {
  if (!navigator.locks) throw new Error('Web Locks are required for managed blob eviction.');
  return new Promise((resolve, reject) => {
    const lifetime = { end: null, completed: null };
    lifetime.completed = navigator.locks.request(name, { ifAvailable: true, mode }, lock => {
      if (!lock) { resolve(null); return; }
      const token = crypto.randomUUID();
      return new Promise(release => { lifetime.end = release; held.set(token, lifetime); resolve(token); });
    });
    lifetime.completed.catch(reject);
  });
}
export async function release(token) {
  const lifetime = held.get(token);
  if (!lifetime) return;
  lifetime.end();
  // Ending the callback requests release; the request promise confirms physical completion.
  // Keep the entry until then so concurrent disposal callers join the same release.
  try { await lifetime.completed; }
  finally { held.delete(token); }
}
