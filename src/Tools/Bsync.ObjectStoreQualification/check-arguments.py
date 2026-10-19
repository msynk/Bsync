"""Check qualification argument guards without contacting any service or exposing test credentials."""
import json
import os
from pathlib import Path
import subprocess
import sys

binary = Path(sys.argv[1]).resolve(strict=True)
output = Path(sys.argv[2]).resolve()
output.mkdir(exist_ok=False)
unused = output / "must-not-be-created"
env = {key: value for key, value in os.environ.items() if not key.startswith(("AWS_", "BSYNC_OBJECTSTORE_"))}
env.update(BSYNC_OBJECTSTORE_ENDPOINT="http://127.0.0.1:59005", BSYNC_OBJECTSTORE_BUCKET="unused",
           AWS_REGION="us-east-1", AWS_ACCESS_KEY_ID="DO_NOT_PRINT_ACCESS", AWS_SECRET_ACCESS_KEY="DO_NOT_PRINT_SECRET")
cases = [
    ("arguments", [], {}, "Usage:"),
    ("unknown-mode", [str(unused), "--unknown"], {}, "Usage:"),
    ("external-requires-https", [str(unused)], {}, "invalid-service-endpoint"),
    ("local-requires-loopback", [str(unused), "--local-service"], {"BSYNC_OBJECTSTORE_ENDPOINT": "https://example.invalid"}, "local-service-must-be-loopback"),
    ("emulator-requires-loopback", [str(unused), "--emulator"], {"BSYNC_OBJECTSTORE_ENDPOINT": "https://example.invalid"}, "emulator-must-be-loopback"),
    ("credentials-required", [str(unused), "--local-service"], {"AWS_ACCESS_KEY_ID": ""}, "missing-AWS_ACCESS_KEY_ID"),
    ("userinfo-rejected", [str(unused), "--local-service"], {"BSYNC_OBJECTSTORE_ENDPOINT": "http://DO_NOT_PRINT_ACCESS:DO_NOT_PRINT_SECRET@127.0.0.1"}, "invalid-service-endpoint"),
    ("invalid-port", [str(unused), "--local-service"], {"BSYNC_OBJECTSTORE_ORIGIN_PORT": "80"}, "invalid-origin-port"),
    ("existing-evidence", [str(output), "--local-service"], {}, "Evidence path already exists."),
]
rows = []
for name, arguments, overrides, expected in cases:
    result = subprocess.run(["dotnet", str(binary), *arguments], env=env | overrides, text=True, capture_output=True, timeout=20)
    text = result.stdout + result.stderr
    assert "DO_NOT_PRINT" not in text
    rows.append(dict(name=name, passed=result.returncode == 2 and expected in text, exitCode=result.returncode, output=text))
assert not unused.exists()
(output / "result.json").write_text(json.dumps(rows, indent=2) + "\n")
assert all(row["passed"] for row in rows), rows
print(f"{len(rows)} configuration/evidence guards passed.")
