// Installed before runtime startup. Observe exposed memory and live crypto inputs/results;
// these overlapping counters must never be summed into a process-memory estimate.
(() => {
    const memories = new Set();
    const observe = value => {
        if (value instanceof WebAssembly.Memory) memories.add(value);
    };
    for (const method of ['instantiate', 'instantiateStreaming']) {
        const original = WebAssembly[method];
        WebAssembly[method] = async function (...args) {
            for (const module of Object.values(args[1] || {}))
                for (const value of Object.values(module)) observe(value);
            const result = await original.apply(this, args);
            const instance = result instanceof WebAssembly.Instance ? result : result.instance;
            for (const value of Object.values(instance.exports)) observe(value);
            return result;
        };
    }
    let stage = 'startup', activeCryptoBytes = 0, cryptoPeak = 0, samples = 0;
    const stages = {};
    const sample = () => {
        const current = stages[stage] ||= { samples: 0, wasmCapacityBytes: 0, jsUsedHeapBytes: null, cryptoVisibleBytes: 0 };
        current.samples++; samples++;
        current.wasmCapacityBytes = Math.max(current.wasmCapacityBytes,
            [...memories].reduce((sum, memory) => sum + memory.buffer.byteLength, 0));
        if (performance.memory) current.jsUsedHeapBytes = Math.max(current.jsUsedHeapBytes || 0, performance.memory.usedJSHeapSize);
        current.cryptoVisibleBytes = Math.max(current.cryptoVisibleBytes, activeCryptoBytes);
        cryptoPeak = Math.max(cryptoPeak, activeCryptoBytes);
    };
    for (const method of ['encrypt', 'decrypt']) {
        const original = SubtleCrypto.prototype[method];
        SubtleCrypto.prototype[method] = async function (...args) {
            const inputBytes = args[2].byteLength;
            activeCryptoBytes += inputBytes; sample();
            try {
                const result = await original.apply(this, args);
                activeCryptoBytes += result.byteLength; sample();
                activeCryptoBytes -= result.byteLength;
                return result;
            } finally { activeCryptoBytes -= inputBytes; }
        };
    }
    window.bsyncQualificationSample = name => {
        if (stage !== name) console.debug('bsync-qualification:' + name);
        stage = name; sample();
    };
    const timer = setInterval(sample, 10);
    window.bsyncQualificationFinish = () => {
        clearInterval(timer); sample();
        return { samples, observedWasmMemories: memories.size, cryptoPeak, stages,
            limitations: 'Sampled JS heap, reserved WASM capacity and visible crypto buffers overlap; no process/native/IndexedDB allocator peak or physical-disk write measurement.' };
    };
})();
