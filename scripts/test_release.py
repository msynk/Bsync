"""Failure tests for the release gate; all repositories and packages are disposable."""

import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import release


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "-q")
        self.git("config", "user.name", "Release test")
        self.git("config", "user.email", "release@example.invalid")
        self.write("src/Directory.Build.props", "<Project><PropertyGroup><VersionPrefix>0.3.0</VersionPrefix></PropertyGroup></Project>")
        self.write("CHANGELOG.md", "# Changelog\n\n## 0.3.0 - 2026-10-16\n\nRelease notes.\n")
        self.write(".gitignore", "artifacts/\nobj/\n")
        self.git("add", ".")
        self.git("commit", "-qm", "baseline")
        self.git("tag", "v0.3.0")
        self.commit = self.git("rev-parse", "HEAD")

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True).strip()

    def test_clean_tag_passes(self):
        self.assertEqual(self.commit, release.preflight(self.root, "v0.3.0")["commit"])

    def test_dirty_and_untracked_files_fail(self):
        for name in ("CHANGELOG.md", "untracked.txt"):
            with self.subTest(name=name):
                self.write(name, "changed")
                with self.assertRaisesRegex(ValueError, "clean checkout"):
                    release.preflight(self.root, "v0.3.0")
                if name == "CHANGELOG.md":
                    self.git("restore", name)

    def test_wrong_version_and_old_tag_fail(self):
        with self.assertRaisesRegex(ValueError, "VersionPrefix"):
            release.preflight(self.root, "v0.2.0")
        self.git("commit", "--allow-empty", "-qm", "later")
        with self.assertRaisesRegex(ValueError, "HEAD"):
            release.preflight(self.root, "v0.3.0")

    def test_unreleased_changelog_fails(self):
        self.write("CHANGELOG.md", "## Unreleased (0.3.0)\n")
        self.git("commit", "-qam", "not released")
        self.git("tag", "-f", "v0.3.0")
        with self.assertRaisesRegex(ValueError, "Changelog"):
            release.preflight(self.root, "v0.3.0")

    def packages(self, version="0.3.0-test.1", commit=None):
        directory = self.root / "artifacts/packages"
        directory.mkdir(parents=True, exist_ok=True)
        for name in release.PACKAGES:
            for extension in ("nupkg", "snupkg"):
                with zipfile.ZipFile(directory / f"{name}.{version}.{extension}", "w") as archive:
                    archive.writestr(f"{name}.nuspec", f'''<package xmlns="urn:test"><metadata>
                        <id>{name}</id><version>{version}</version>
                        <repository type="git" url="{release.REPOSITORY}" commit="{commit or self.commit}"/>
                        </metadata></package>''')
            self.write(f"src/Bsync/{name}/obj/Release/net10.0/{name}.sourcelink.json",
                       json.dumps({"documents": {"/_/*": f"https://raw.githubusercontent.com/msynk/Bsync/{self.commit}/*"}}))
        return directory

    def record(self, directory, version="0.3.0-test.1", tag=None):
        real_run = release.run
        def run(root, *args):
            return "test SDK" if args[0] == "dotnet" else real_run(root, *args)
        with patch.object(release, "run", side_effect=run):
            return release.manifest(self.root, directory, version, tag)

    def test_preview_records_hashes_and_dirty_state(self):
        directory = self.packages()
        self.write("untracked.txt", "working tree")
        result = self.record(directory)
        self.assertTrue(result["source"]["dirty"])
        self.assertEqual(12, len(result["artifacts"]))
        for artifact in result["artifacts"]:
            self.assertEqual(release.digest(directory / artifact["file"]), artifact["sha256"])

    def test_stable_artifacts_require_tag(self):
        directory = self.packages("0.3.0")
        with self.assertRaisesRegex(ValueError, "require --tag"):
            self.record(directory, "0.3.0")
        self.assertEqual("v0.3.0", self.record(directory, "0.3.0", "v0.3.0")["source"]["tag"])

    def test_wrong_repository_commit_fails(self):
        with self.assertRaisesRegex(ValueError, "commit differs"):
            self.record(self.packages(commit="0" * 40))

    def test_missing_symbols_and_mixed_artifacts_fail(self):
        directory = self.packages()
        (directory / "Bsync.0.3.0-test.1.snupkg").unlink()
        with self.assertRaisesRegex(ValueError, "Missing"):
            self.record(directory)
        self.packages()
        (directory / "Bsync.0.2.0.nupkg").write_bytes(b"stale")
        with self.assertRaisesRegex(ValueError, "unexpected"):
            self.record(directory)

    def test_stale_source_link_fails(self):
        directory = self.packages()
        self.write("src/Bsync/Bsync/obj/Release/net10.0/Bsync.sourcelink.json",
                   json.dumps({"documents": {"/_/*": "https://example.invalid/old/*"}}))
        with self.assertRaisesRegex(ValueError, "Source Link"):
            self.record(directory)


if __name__ == "__main__":
    unittest.main()
