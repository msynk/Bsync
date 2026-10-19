// Independent test-only format writer. Fixed nonces/keys are NEVER a production encryption policy.
// Run from repository root with Node; does not import any Bsync implementation.
import { createCipheriv, createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const account = hash('7:vectors4:test5:alice-1:');
const key = Buffer.from(Array.from({length:32}, (_, i) => i));
const generation = '00112233445566778899aabbccddeeff';
const plaintext = Buffer.from(Array.from({length:65539}, (_, i) => i % 251));
const sha = hash(plaintext);
let invocation = 0;
function seal(purpose, bytes, index = -1, length = -1) {
  const nonce = Buffer.alloc(12); nonce.writeUInt32BE(++invocation, 8);
  const aad = Buffer.from(['Bsync.chunked/2/AES-256-GCM', purpose, account, sha, generation, 'vector', '1', index, length].join('\n'));
  const cipher = createCipheriv('aes-256-gcm', key, nonce); cipher.setAAD(aad);
  return Buffer.concat([nonce, cipher.update(bytes), cipher.final(), cipher.getAuthTag()]);
}
const chunks = [];
for (let index = 0, start = 0; start < plaintext.length; index++, start += 65536) {
  const part = plaintext.subarray(start, start + 65536);
  const ciphertext = seal('chunk', part, index, part.length);
  chunks.push({ Id: hash(ciphertext), Length: part.length, Ciphertext: ciphertext.toString('base64') });
}
const manifest = {Version:2, Hash:sha, Generation:generation, ChunkSize:65536, Length:plaintext.length, Chunks:chunks.map(({Id,Length})=>({Id,Length}))};
function state(value) {
  const envelope = {Generation:generation, KeyId:'vector', KeyVersion:1, Ciphertext:seal('manifest', Buffer.from(JSON.stringify(value))).toString('base64')};
  return JSON.stringify({Version:2, Complete:envelope, Partial:null, RecoveryAuthorized:false, PartialPurpose:null});
}
const vector = {Generator:'Node node:crypto AES-256-GCM; fixed TEST key/nonces', AccountStorageKey:account, Hash:sha,
  State:state(manifest), InvalidOrderState:state({...manifest, Chunks:[...manifest.Chunks].reverse()}), Chunks:chunks};
writeFileSync('src/Tests/Bsync.Tests/Fixtures/chunked-v2-vector.json', JSON.stringify(vector, null, 2) + '\n');
