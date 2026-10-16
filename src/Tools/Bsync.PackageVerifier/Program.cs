using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;

// Verify symbols actually distributed in NuGet archives, not just intermediate SDK Source Link maps.
// Portable PDB custom-debug identifiers: dotnet/runtime docs/design/specs/PortablePdb-Metadata.md.
if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--verify-sources") || args[1].Length != 40 || args[1].Any(c => !char.IsAsciiHexDigit(c)))
{
    Console.Error.WriteLine("Usage: Bsync.PackageVerifier <package-directory> <full-commit-sha> [--verify-sources]");
    return 2;
}
try
{
    var directory = Path.GetFullPath(args[0]);
    var commit = args[1].ToLowerInvariant();
    var verifyRemote = args.Length == 3;
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    var downloaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var embeddedChecksums = 0; var remoteChecksums = 0;
    // A failed new verification must not leave a previous success report behind.
    File.Delete(Path.Combine(directory, "symbols-verification.json"));
    var results = new List<object>();
    var packages = Directory.GetFiles(directory, "*.nupkg").Order(StringComparer.Ordinal).ToArray();
    Require(packages.Length == 6, "Expected exactly six library packages in the isolated directory.");
    foreach (var package in packages)
    {
        using var archive = ZipFile.OpenRead(package);
        using var symbols = ZipFile.OpenRead(Path.ChangeExtension(package, ".snupkg"));
        var assemblies = archive.Entries.Where(e => e.FullName.StartsWith("lib/", StringComparison.Ordinal) && e.FullName.EndsWith(".dll", StringComparison.Ordinal)).ToArray();
        Require(assemblies.Length > 0, "Package has no library assemblies.");
        foreach (var assembly in assemblies)
        {
            var pdbPath = Path.ChangeExtension(assembly.FullName, ".pdb");
            var pdbEntry = symbols.GetEntry(pdbPath) ?? throw new InvalidDataException($"Missing {pdbPath}.");
            using var peStream = Copy(assembly);
            using var pdbStream = Copy(pdbEntry);
            using var pe = new PEReader(peStream);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            var pdb = provider.GetMetadataReader();
            var contentId = new BlobContentId(pdb.DebugMetadataHeader!.Id);
            var codeViewEntry = pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
            var codeView = pe.ReadCodeViewDebugDirectoryData(codeViewEntry);
            Require(codeView.Guid == contentId.Guid && codeViewEntry.Stamp == contentId.Stamp && codeView.Age == 1,
                $"Assembly/PDB identity mismatch: {assembly.FullName}.");

            var sourceLinks = pdb.CustomDebugInformation.Select(pdb.GetCustomDebugInformation)
                .Where(i => pdb.GetGuid(i.Kind) == new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A")).ToArray();
            Require(sourceLinks.Length == 1 && sourceLinks[0].Parent.Kind == HandleKind.ModuleDefinition, "Expected one module-level Source Link record.");
            using var mapping = JsonDocument.Parse(pdb.GetBlobBytes(sourceLinks[0].Value));
            var documents = mapping.RootElement.GetProperty("documents").EnumerateObject().ToArray();
            Require(documents.Length > 0, "Empty Source Link mapping.");
            var expected = "https://raw.githubusercontent.com/msynk/Bsync/" + commit + "/";
            foreach (var document in documents)
            {
                var url = document.Value.GetString();
                Require(url is not null && url.StartsWith(expected, StringComparison.Ordinal), "Source Link does not identify the expected repository/commit.");
                Require(document.Name.EndsWith('*') && document.Name.Count(c => c == '*') == 1 && url!.EndsWith('*'), "Unexpected Source Link mapping shape.");
            }
            var embedded = 0;
            var embeddedHashes = new List<string>();
            foreach (var handle in pdb.Documents)
            {
                var document = pdb.GetDocument(handle);
                var name = pdb.GetString(document.Name);
                Require(!document.Hash.IsNil && pdb.GetBlobBytes(document.Hash).Length > 0, "Document checksum missing.");
                var embeddedRecords = pdb.GetCustomDebugInformation(handle).Select(pdb.GetCustomDebugInformation)
                    .Where(i => pdb.GetGuid(i.Kind) == new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE")).ToArray();
                Require(embeddedRecords.Length <= 1, "Duplicate embedded source records.");
                var isEmbedded = embeddedRecords.Length == 1;
                if (isEmbedded) embedded++;
                Require(isEmbedded || documents.Any(m => name.StartsWith(m.Name[..^1], StringComparison.Ordinal)),
                    "Document has neither embedded source nor a Source Link mapping.");
                byte[]? source = null;
                if (isEmbedded)
                {
                    source = EmbeddedSource(pdb.GetBlobBytes(embeddedRecords[0].Value));
                    embeddedChecksums++;
                    embeddedHashes.Add(Convert.ToHexStringLower(pdb.GetBlobBytes(document.Hash)));
                }
                else if (verifyRemote)
                {
                    var matches = documents.Where(m => name.StartsWith(m.Name[..^1], StringComparison.Ordinal)).ToArray();
                    Require(matches.Length == 1, "Ambiguous Source Link document mapping.");
                    var relative = name[(matches[0].Name.Length - 1)..].Replace('\\', '/');
                    Require(relative.Split('/').All(part => part is not ("" or "." or "..")), "Invalid Source Link relative path.");
                    var url = matches[0].Value.GetString()![..^1] + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
                    var uri = new Uri(url);
                    Require(uri.AbsoluteUri.StartsWith(expected, StringComparison.Ordinal) && uri.Query.Length == 0 && uri.Fragment.Length == 0,
                        "Source URL escaped the expected repository commit.");
                    if (!downloaded.TryGetValue(url, out source))
                    {
                        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                        Require(response.IsSuccessStatusCode, $"Remote source unavailable ({(int)response.StatusCode}): {uri.AbsoluteUri}");
                        using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
                        source = await ReadBoundedAsync(body, 8 * 1024 * 1024, deadline.Token);
                        downloaded.Add(url, source);
                    }
                    remoteChecksums++;
                }
                if (source is not null)
                    Require(Checksum(source, pdb.GetGuid(document.HashAlgorithm)).AsSpan().SequenceEqual(pdb.GetBlobBytes(document.Hash)),
                        "Source checksum mismatch: " + name);
            }
            Require(pdb.Documents.Count > 0, "PDB contains no documents.");
            results.Add(new { Package = Path.GetFileName(package), Assembly = assembly.FullName, Pdb = pdbPath,
                PdbId = contentId.Guid, Documents = pdb.Documents.Count, EmbeddedDocuments = embedded, EmbeddedSourceHashes = embeddedHashes });
        }
    }
    var report = new { SchemaVersion = 1, Commit = commit, RemoteSourceDownloaded = verifyRemote, EmbeddedChecksumsVerified = embeddedChecksums,
        RemoteChecksumsVerified = remoteChecksums, UniqueSourcesDownloaded = downloaded.Count, Symbols = results };
    File.WriteAllText(Path.Combine(directory, "symbols-verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine($"Verified {results.Count} assembly/PDB pairs, {embeddedChecksums} embedded-source checksums and {remoteChecksums} remote-source checksums.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Symbol verification failed: " + error.Message);
    return 1;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
static MemoryStream Copy(ZipArchiveEntry entry)
{
    using var source = entry.Open();
    var stream = new MemoryStream(); source.CopyTo(stream); stream.Position = 0;
    return stream;
}

static byte[] EmbeddedSource(byte[] blob)
{
    Require(blob.Length >= 4, "Invalid embedded source header.");
    var size = BinaryPrimitives.ReadInt32LittleEndian(blob);
    Require(size is >= 0 and <= 8 * 1024 * 1024, "Embedded source length exceeds bounds.");
    if (size == 0) { Require(blob.Length - 4 <= 8 * 1024 * 1024, "Embedded source exceeds bounds."); return blob[4..]; }
    using var compressed = new MemoryStream(blob, 4, blob.Length - 4);
    using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
    var source = new byte[size];
    deflate.ReadExactly(source);
    Require(deflate.ReadByte() == -1, "Embedded source expanded past its declared length.");
    return source;
}

static byte[] Checksum(byte[] source, Guid algorithm) => algorithm.ToString() switch
{
    "ff1816ec-aa5e-4d10-87f7-6f4963833460" => SHA1.HashData(source),
    "8829d00f-11b8-4213-878b-770e8597ac16" => SHA256.HashData(source),
    "d99cfeb1-8c43-444a-8a6c-b61269d2a0bf" => SHA384.HashData(source),
    "ef2d1afc-6550-46d6-b14b-d70afe9a5566" => SHA512.HashData(source),
    _ => throw new InvalidDataException("Unsupported source checksum algorithm."),
};

static async Task<byte[]> ReadBoundedAsync(Stream source, int maximum, CancellationToken token)
{
    using var target = new MemoryStream();
    var buffer = new byte[81920];
    int count;
    while ((count = await source.ReadAsync(buffer, token)) != 0)
    {
        Require(target.Length + count <= maximum, "Remote source exceeds the verification bound.");
        target.Write(buffer, 0, count);
    }
    return target.ToArray();
}
