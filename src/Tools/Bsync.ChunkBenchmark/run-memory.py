"""Run each native memory case in a fresh process against an immutable local package."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import zipfile


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-version", required=True)
    parser.add_argument("--output", required=True, type=Path, help="New evidence directory; never overwritten")
    parser.add_argument("--sizes", default="1,16,17,256,1024")
    parser.add_argument("--samples", type=int, default=3)
    parser.add_argument("--heap-limit-mib", type=int, default=31, help="Whole managed-heap cap, distinct from the plan's cache-only target")
    parser.add_argument("--conserve-memory", type=int, choices=range(10), default=0,
                        help="Explicit child GC memory-conservation setting (0 is the runtime default)")
    args = parser.parse_args()
    sizes = [int(value) for value in args.sizes.split(",")]
    if (not sizes or any(value < 1 or value > 1024 for value in sizes) or not 1 <= args.samples <= 100 or
            not 1 <= args.heap_limit_mib <= 1024):
        parser.error("Sizes and heap limit must be 1..1024 MiB and samples 1..100")
    root = Path(__file__).resolve().parents[3]
    project = Path(__file__).resolve().parent
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    manifest = {"schema": 1, "packageVersion": args.package_version, "heapLimitBytes": args.heap_limit_mib * 1048576,
                "sizes": sizes, "samplesPerSize": args.samples, "passed": False, "runs": []}

    def save():
        (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    save()
    try:
        # Build without the constrained child environment. Environment settings are never changed globally.
        command = ["dotnet", "build", str(project), "-c", "Release",
                   "-p:BsyncPackageVersion=" + args.package_version, "-m:2", "-nodeReuse:false"]
        manifest["buildCommand"] = command
        with (output / "build.log").open("w", encoding="utf-8") as log:
            subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT, check=True)
        binary = project / "bin/Release/net10.0/Bsync.ChunkBenchmark.dll"
        provenance = []
        for package in ("Bsync", "Bsync.Storage.Sqlite"):
            archive = root / "artifacts/packages" / f"{package}.{args.package_version}.nupkg"
            with zipfile.ZipFile(archive) as package_zip:
                entry = f"lib/net10.0/{package}.dll"
                expected = sha256(package_zip.read(entry))
            actual = sha256((binary.parent / (package + ".dll")).read_bytes())
            if actual != expected:
                raise RuntimeError("Loaded output differs from exact archive: " + package)
            provenance.append({"package": package, "archive": str(archive.relative_to(root)),
                               "archiveSha256": sha256(archive.read_bytes()), "assemblySha256": actual})
        manifest["packages"] = provenance
        manifest["toolAssemblySha256"] = sha256(binary.read_bytes())
        manifest["sourceSha256"] = {str(path.relative_to(root)): sha256(path.read_bytes()) for path in
                                    [*sorted(project.glob("*.cs")), project / "Bsync.ChunkBenchmark.csproj", Path(__file__).resolve()]}
        child = os.environ.copy()
        # Per-heap limits take precedence over the total cap. Remove both modern and legacy aliases.
        for name in list(child):
            if name.upper().startswith(("DOTNET_GCHEAPHARDLIMIT", "COMPLUS_GCHEAPHARDLIMIT")) or name.upper() in (
                    "DOTNET_GCCONSERVEMEMORY", "COMPLUS_GCCONSERVEMEMORY"):
                del child[name]
        child["DOTNET_GCHeapHardLimit"] = format(manifest["heapLimitBytes"], "X")
        child["DOTNET_GCConserveMemory"] = str(args.conserve_memory)
        manifest["childEnvironmentOverrides"] = {"DOTNET_GCHeapHardLimit": child["DOTNET_GCHeapHardLimit"], "perHeapHardLimits": "removed",
                                                  "DOTNET_GCConserveMemory": str(args.conserve_memory)}
        manifest["diagnosticPauses"] = {name: child[name] for name in
                                       ("BSYNC_MEMORY_PAUSE_WRITE", "BSYNC_MEMORY_DIAGNOSTIC_PAUSE") if name in child}
        for size in sizes:
            for sample in range(1, args.samples + 1):
                stem = f"memory-{size}-{sample}"
                result = output / (stem + ".json")
                command = ["dotnet", str(binary), "--memory", str(result), str(size), str(sample), str(args.heap_limit_mib)]
                started = time.monotonic()
                with (output / (stem + ".log")).open("w", encoding="utf-8") as log:
                    completed = subprocess.run(command, cwd=root, env=child, stdout=log, stderr=subprocess.STDOUT)
                run = {"command": command, "exitCode": completed.returncode,
                       "elapsedSeconds": time.monotonic() - started, "log": stem + ".log",
                       "logSha256": sha256((output / (stem + ".log")).read_bytes())}
                manifest["runs"].append(run)
                if result.exists():
                    run["result"] = result.name
                    run["resultSha256"] = sha256(result.read_bytes())
                save()  # Preserve failures and partial runs; success is assigned only after the complete matrix.
                if completed.returncode:
                    raise RuntimeError(f"{stem} exited {completed.returncode}; see retained log")
                row = json.loads(result.read_text(encoding="utf-8"))
                if (not row["Passed"] or row["LibraryAssemblySha256"] != provenance[0]["assemblySha256"] or
                        row["EffectiveAvailableMemoryBytes"] != manifest["heapLimitBytes"] or
                        len(row["Stages"]) != 10 or not all(stage["Passed"] for stage in row["Stages"])):
                    raise RuntimeError("Incomplete or mismatched evidence: " + stem)
                print(f"PASS {size} MiB sample {sample}: {run['elapsedSeconds']:.1f}s", flush=True)
        manifest["passed"] = True
        save()
        return 0
    except Exception as error:
        manifest["error"] = str(error)
        save()
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
