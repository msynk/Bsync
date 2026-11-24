"""Exercise published 0.5.0 -> exact preview -> published-reader refusal with isolated package-only binaries."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    feed = ROOT / "artifacts/packages"
    baseline = json.loads((ROOT / "scripts/baselines/published-0.5.0-packages.json").read_text())
    identities = []
    for name in ("Bsync", "Bsync.Storage.Sqlite"):
        record = next(p for p in baseline["packages"] if p["file"] == f"{name}.0.5.0.nupkg")
        archive = feed / record["file"]
        data = archive.read_bytes() if archive.exists() else urllib.request.urlopen(record["url"], timeout=90).read()
        if digest(data) != record["sha256"]:
            raise RuntimeError("Published archive digest differs from the preserved baseline: " + name)
        if not archive.exists():
            archive.write_bytes(data)
        for version in ("0.5.0", args.version):
            candidate = feed / f"{name}.{version}.nupkg"
            identities.append(dict(package=name, version=version, sha256=digest(candidate.read_bytes())))
    env = os.environ.copy()
    env["NUGET_PACKAGES"] = str(output / "package-cache")
    project = ROOT / "src/Tests/PackageConsumers/Bsync.PackageConsumer.Migration"

    def run(label, command):
        with (output / (label + ".log")).open("w", encoding="utf-8") as log:
            subprocess.run(command, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=300)

    for label, version, features in (("old", "0.5.0", "false"), ("new", args.version, "true")):
        run(label + "-build", ["dotnet", "publish", str(project), "-c", "Release", "-p:BsyncPackageVersion=" + version,
            "-p:BsyncNewFeatures=" + features, "--configfile", str(project.parent / "NuGet.Config"), "-o", str(output / label)])
        for name in ("Bsync", "Bsync.Storage.Sqlite"):
            with zipfile.ZipFile(feed / f"{name}.{version}.nupkg") as archive:
                expected = digest(archive.read(f"lib/net10.0/{name}.dll"))
            if digest((output / label / (name + ".dll")).read_bytes()) != expected:
                raise RuntimeError("Published executable did not consume exact archive: " + name)
    fixture = output / "fixture"
    binary = "Bsync.PackageConsumer.Migration.dll"
    run("create", ["dotnet", str(output / "old" / binary), "create", str(fixture)])
    shutil.copytree(fixture, output / "published-fixture")
    run("upgrade", ["dotnet", str(output / "new" / binary), "upgrade", str(fixture)])
    before = {str(p.relative_to(fixture)): digest(p.read_bytes()) for p in fixture.rglob("*") if p.is_file()}
    run("downgrade-refusal", ["dotnet", str(output / "old" / binary), "refuse", str(fixture)])
    after = {str(p.relative_to(fixture)): digest(p.read_bytes()) for p in fixture.rglob("*") if p.is_file()}
    if before != after:
        raise RuntimeError("Unsupported old-reader open modified the retained fixture")
    run("new-reader-recheck", ["dotnet", str(output / "new" / binary), "upgrade", str(fixture)])
    (output / "result.json").write_text(json.dumps(dict(passed=True, candidate=args.version, packages=identities,
        publishedFixture={str(p.relative_to(output / "published-fixture")): digest(p.read_bytes())
            for p in (output / "published-fixture").rglob("*") if p.is_file()}, refusedFixture=after), indent=2))
    print("Exact published migration, byte-preserving downgrade refusal and new-reader recheck passed.")


if __name__ == "__main__":
    main()
