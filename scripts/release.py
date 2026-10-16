"""Release provenance checks. Python standard library only; never publishes packages."""

import argparse
from datetime import date, datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PACKAGES = (
    "Bsync", "Bsync.Blazor", "Bsync.Server.AspNetCore", "Bsync.Server.PostgreSql",
    "Bsync.Server.SqlServer", "Bsync.Storage.Sqlite",
)
REPOSITORY = "https://github.com/msynk/Bsync"


def run(root, *args):
    return subprocess.check_output(args, cwd=root, text=True, encoding="utf-8").strip()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def source_state(root):
    return {
        "commit": run(root, "git", "rev-parse", "HEAD"),
        "dirty": bool(run(root, "git", "status", "--porcelain", "--untracked-files=all")),
        "versionPrefix": ET.parse(root / "src/Directory.Build.props").findtext(".//VersionPrefix"),
    }


def preflight(root, tag):
    state = source_state(root)
    require(not state["dirty"], "Release requires a clean checkout, including untracked files.")
    require(tag == "v" + state["versionPrefix"], "Tag must be v<VersionPrefix>.")
    require(re.fullmatch(r"v\d+\.\d+\.\d+", tag), "Release tag must be a stable version.")
    require(run(root, "git", "rev-parse", f"refs/tags/{tag}^{{commit}}") == state["commit"],
            "Release tag does not point to HEAD.")
    changelog = (root / "CHANGELOG.md").read_text(encoding="utf-8")
    version = re.escape(state["versionPrefix"])
    heading = re.search(rf"^## {version} - (\d{{4}}-\d{{2}}-\d{{2}})\s*$", changelog, re.M)
    require(heading is not None, "Changelog needs '## <version> - YYYY-MM-DD'.")
    date.fromisoformat(heading[1])
    require(not re.search(rf"^## Unreleased \({version}\)", changelog, re.M),
            "Changelog still marks this version unreleased.")
    return {**state, "tag": tag}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def package_metadata(path):
    with zipfile.ZipFile(path) as archive:
        specs = [name for name in archive.namelist() if name.endswith(".nuspec")]
        require(len(specs) == 1, f"{path.name}: expected one nuspec.")
        spec = ET.fromstring(archive.read(specs[0]))
        # NuGet's namespace differs between historical packages.
        for node in spec.iter():
            node.tag = node.tag.rsplit("}", 1)[-1]
        metadata = spec.find("metadata")
        repository = metadata.find("repository")
        require(repository is not None, f"{path.name}: missing repository metadata.")
        return {
            "id": metadata.findtext("id"), "version": metadata.findtext("version"),
            "repository": dict(repository.attrib),
        }


def manifest(root, directory, version, tag=None):
    state = preflight(root, tag) if tag else source_state(root)
    require(version == state["versionPrefix"] or version.startswith(state["versionPrefix"] + "-"),
            "Package version must match VersionPrefix (with an optional preview suffix).")
    require(tag or "-" in version, "Stable artifacts require --tag and release preflight.")
    if tag:
        require(version == state["versionPrefix"], "Tagged releases cannot use a preview version.")
    expected = {f"{name}.{version}.{extension}"
                for name in PACKAGES for extension in ("nupkg", "snupkg")}
    actual = {p.name for p in directory.iterdir() if p.suffix in (".nupkg", ".snupkg")}
    require(actual == expected, f"Package set mismatch. Missing: {sorted(expected - actual)}; "
            f"unexpected: {sorted(actual - expected)}")
    artifacts = []
    for name in sorted(expected):
        path = directory / name
        metadata = package_metadata(path)
        require(metadata["id"] in PACKAGES and metadata["version"] == version
                and name == f'{metadata["id"]}.{version}{path.suffix}',
                f"{name}: package identity/version mismatch.")
        require(metadata["repository"].get("commit") == state["commit"],
                f"{name}: repository commit differs from HEAD.")
        require(metadata["repository"].get("url", "").removesuffix(".git") == REPOSITORY
                and metadata["repository"].get("type") == "git",
                f"{name}: unexpected repository.")
        artifacts.append({"file": name, "sha256": digest(path), **metadata})

    # Preserve the SDK-generated Source Link maps alongside artifact identity. This checks the
    # maps, not remote source availability or the contents of the portable PDB debug records.
    source_link = {}
    for name in PACKAGES:
        maps = list((root / "src/Bsync" / name / "obj/Release").rglob("*.sourcelink.json"))
        require(len(maps) == 1, f"{name}: expected one Release Source Link map; clean build first.")
        data = json.loads(maps[0].read_text(encoding="utf-8"))
        urls = list(data.get("documents", {}).values())
        require(urls and all(f"/{state['commit']}/" in url for url in urls),
                f"{name}: Source Link map does not identify HEAD.")
        source_link[name] = data
    return {
        "schemaVersion": 1, "createdUtc": datetime.now(timezone.utc).isoformat(),
        "source": state, "packageVersion": version,
        "dotnetInfo": run(root, "dotnet", "--info"),
        "artifacts": artifacts, "sourceLinkBuildMaps": source_link,
        "apiBaselines": {p.name: digest(p) for p in sorted((root / "src/Tests/api").glob("*.txt"))},
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("preflight")
    check.add_argument("--tag", required=True)
    record = sub.add_parser("manifest")
    record.add_argument("--packages", type=Path, default=ROOT / "artifacts/packages")
    record.add_argument("--version", required=True)
    record.add_argument("--tag")
    args = parser.parse_args()
    try:
        if args.command == "preflight":
            print(json.dumps(preflight(ROOT, args.tag), indent=2))
        else:
            result = manifest(ROOT, args.packages, args.version, args.tag)
            destination = args.packages / "provenance.json"
            destination.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
            print(f"Verified {len(result['artifacts'])} artifacts: {destination}")
    except (ValueError, OSError, subprocess.CalledProcessError, ET.ParseError, zipfile.BadZipFile) as error:
        print(f"Release validation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
