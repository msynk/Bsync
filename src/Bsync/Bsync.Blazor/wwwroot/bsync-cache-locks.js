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
    navigator.locks.request(name, { ifAvailable: true, mode }, lock => {
      if (!lock) { resolve(null); return; }
      const token = crypto.randomUUID();
      return new Promise(release => { held.set(token, release); resolve(token); });
    }).catch(reject);
  });
}
export function release(token) { const end = held.get(token); held.delete(token); end?.(); }
