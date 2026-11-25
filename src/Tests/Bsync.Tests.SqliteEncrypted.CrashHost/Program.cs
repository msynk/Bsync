using Bsync.Storage.Sqlite;

// Usage: encrypt <db> <step> <key-hex>
// Runs SqliteEncryption.EncryptAsync and, when it reaches <step> (folded, exported or replaced), prints "reached <step>"
// and waits to be killed.
if (args is not ["encrypt", var path, var stop, var keyHex])
{
    Console.Error.WriteLine("usage: encrypt <db> <step> <key-hex>");
    return 2;
}

await SqliteEncryption.EncryptAsync(path, Convert.FromHexString(keyHex), async step =>
{
    if (step == stop)
    {
        Console.WriteLine("reached " + step);
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.Infinite);
    }
}, CancellationToken.None);
Console.WriteLine("finished");
return 0;
