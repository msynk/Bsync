"""Integration tests against already packed artifacts. Run after building Bsync.PackageVerifier."""
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
import json

ROOT = pathlib.Path(__file__).resolve().parents[1]
PACKAGES = pathlib.Path(sys.argv.pop(1)).resolve() if len(sys.argv) > 1 else ROOT / "artifacts/packages"
VERIFIER = ROOT / "src/Tools/Bsync.PackageVerifier/bin/Release/net10.0/Bsync.PackageVerifier.dll"
COMMIT = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()


class PackagedSymbolTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="bsync-symbol-test-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = pathlib.Path(self.temporary.name)
        for source in PACKAGES.glob("*.?nupkg"):
            shutil.copy2(source, self.directory / source.name)
        # *.?nupkg matches snupkg; nupkg has no extra prefix character.
        for source in PACKAGES.glob("*.nupkg"):
            shutil.copy2(source, self.directory / source.name)

    def run_verifier(self, commit=COMMIT):
        return subprocess.run(["dotnet", str(VERIFIER), str(self.directory), commit],
                              text=True, capture_output=True, timeout=30)

    def test_exact_archives_pass(self):
        result = self.run_verifier()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue((self.directory / "symbols-verification.json").exists())

    def test_wrong_commit_fails(self):
        result = self.run_verifier("0" * 40)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("expected repository/commit", result.stderr)

    def test_tampered_embedded_checksum_fails_and_removes_previous_report(self):
        result = self.run_verifier()
        self.assertEqual(0, result.returncode, result.stderr)
        report_path = self.directory / "symbols-verification.json"
        report = json.loads(report_path.read_text(encoding="utf-8"))
        self.assertGreater(report["EmbeddedChecksumsVerified"], 0)
        entry = next(item for item in report["Symbols"] if item["EmbeddedSourceHashes"])
        symbol = self.directory / entry["Package"].replace(".nupkg", ".snupkg")
        with zipfile.ZipFile(symbol) as archive:
            entries = {name: archive.read(name) for name in archive.namelist()}
        checksum = bytes.fromhex(entry["EmbeddedSourceHashes"][0])
        self.assertIn(checksum, entries[entry["Pdb"]])
        entries[entry["Pdb"]] = entries[entry["Pdb"]].replace(checksum, bytes([checksum[0] ^ 1]) + checksum[1:], 1)
        with zipfile.ZipFile(symbol, "w") as archive:
            for name, data in entries.items():
                archive.writestr(name, data)
        result = self.run_verifier()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Source checksum mismatch", result.stderr)
        self.assertFalse(report_path.exists())

    def test_missing_pdb_fails(self):
        symbol = next(self.directory.glob("Bsync.0*.snupkg"))
        with zipfile.ZipFile(symbol, "w"):
            pass
        result = self.run_verifier()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Missing lib/", result.stderr)

    def test_pdb_from_another_assembly_fails(self):
        symbol = next(self.directory.glob("Bsync.0*.snupkg"))
        other = next(self.directory.glob("Bsync.Blazor.*.snupkg"))
        with zipfile.ZipFile(other) as archive:
            replacement = archive.read("lib/net10.0/Bsync.Blazor.pdb")
        with zipfile.ZipFile(symbol, "w") as archive:
            archive.writestr("lib/net10.0/Bsync.pdb", replacement)
        result = self.run_verifier()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Assembly/PDB identity mismatch", result.stderr)


if __name__ == "__main__":
    unittest.main()
