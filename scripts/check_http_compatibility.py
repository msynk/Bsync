"""Run actual published 0.2.0 and exact local preview blob clients/servers in separate processes."""
import argparse
import json
from pathlib import Path
import socket
import subprocess
import time
from urllib.request import urlopen
from urllib.error import URLError
import uuid

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src/Tests/Bsync.Fixtures.Http/Bsync.Fixtures.Http.csproj"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="Unique preview already packed into the local feed")
    parser.add_argument("--packages", type=Path, default=ROOT / "artifacts/final-packages")
    args = parser.parse_args()
    args.packages = args.packages.resolve()
    for package in ["Bsync", "Bsync.Server.AspNetCore"]:
        if not (args.packages / f"{package}.{args.version}.nupkg").is_file():
            parser.error(f"Missing exact local artifact: {package}.{args.version}.nupkg")
    run = ROOT / "artifacts" / ("http-compatibility-" + uuid.uuid4().hex)
    run.mkdir(parents=True)
    builds = {}
    for label, version in [("old", "0.2.0"), ("new", args.version)]:
        output = run / label
        # Exact version ranges and separate intermediates/cache prevent either process loading the other assembly.
        command = ["dotnet", "build", str(PROJECT), "-c", "Release", "-o", str(output),
                   f"-p:BsyncFixtureVersion={version}", f"-p:BaseIntermediateOutputPath={output / 'obj'}/",
                   f"-p:RestorePackagesPath={run / 'packages'}", "--source", "https://api.nuget.org/v3/index.json"]
        if label == "new":
            command += ["--source", str(args.packages)]
        with (run / f"{label}-build.log").open("w", encoding="utf-8") as log:
            subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
        builds[label] = output / "Bsync.Fixtures.Http.dll"
    results = []
    for server in ["old", "new"]:
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        address = f"http://127.0.0.1:{port}/"
        with (run / f"{server}-server.log").open("w", encoding="utf-8") as log:
            process = subprocess.Popen(["dotnet", str(builds[server]), "server", address, str(run / f"{server}-data")],
                                       cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 30
                while True:
                    if process.poll() is not None:
                        raise RuntimeError(f"{server} server exited; see {log.name}")
                    try:
                        with urlopen(address + "health", timeout=1) as response:
                            server_version = response.read().decode()
                        break
                    except (URLError, TimeoutError):
                        if time.monotonic() >= deadline:
                            raise RuntimeError(f"{server} server did not start")
                        time.sleep(0.1)
                cases = [("new", "legacy"), ("new", "unsupported")] if server == "old" else [("old", "legacy"), ("new", "tickets")]
                for client, mode in cases:
                    command = ["dotnet", str(builds[client]), "client", address, str(run / f"{client}-to-{server}-{mode}")]
                    if mode != "legacy":
                        command.append(mode)
                    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=60)
                    (run / f"{client}-to-{server}-{mode}.log").write_text(result.stdout + result.stderr, encoding="utf-8")
                    result.check_returncode()
                    results.append(dict(client=client, server=server, server_assembly=server_version, mode=mode, result=result.stdout.strip()))
            finally:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait(timeout=10)
    (run / "results.json").write_text(json.dumps(dict(preview=args.version, cases=results), indent=2) + "\n", encoding="utf-8")
    print(f"PASS: {len(results)} cross-version cases; evidence: {run}")


if __name__ == "__main__":
    main()
