// Same envelope as PlatformBlobEncryption: 12-byte nonce | ciphertext | 16-byte GCM tag.
// Keys are imported non-extractable and never persisted. JS/runtime copies cannot be guaranteed to be erased.
export async function seal(keyBytes, plaintext, additionalData) {
    try {
        if (keyBytes.length !== 32) throw new Error("Expected AES-256 key.");
        const key = await crypto.subtle.importKey("raw", keyBytes, "AES-GCM", false, ["encrypt"]);
        const nonce = crypto.getRandomValues(new Uint8Array(12));
        const ciphertext = new Uint8Array(await crypto.subtle.encrypt(
            { name: "AES-GCM", iv: nonce, additionalData, tagLength: 128 }, key, plaintext));
        const result = new Uint8Array(12 + ciphertext.length);
        result.set(nonce); result.set(ciphertext, 12);
        return result;
    } finally { keyBytes.fill(0); plaintext.fill(0); }
}

export async function open(keyBytes, envelope, additionalData) {
    try {
        if (keyBytes.length !== 32 || envelope.length < 28) throw new Error("Invalid encrypted blob envelope.");
        const key = await crypto.subtle.importKey("raw", keyBytes, "AES-GCM", false, ["decrypt"]);
        return new Uint8Array(await crypto.subtle.decrypt(
            { name: "AES-GCM", iv: envelope.subarray(0, 12), additionalData, tagLength: 128 }, key, envelope.subarray(12)));
    } finally { keyBytes.fill(0); }
}
