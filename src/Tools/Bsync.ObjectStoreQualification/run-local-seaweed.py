"""Run an isolated real SeaweedFS S3 service on loopback. Requires boto3 and a verified official weed.exe."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import threading
import time
import uuid
import boto3

ROOT = Path(__file__).resolve().parents[3]


def main():
    if len(sys.argv) != 3:
        raise SystemExit("Usage: run-local-seaweed.py <verified-weed-executable> <new-output-directory>")
    executable = Path(sys.argv[1]).resolve(strict=True)
    output = Path(sys.argv[2]).resolve()
    output.mkdir(exist_ok=False)
    data = output / "data"
    data.mkdir()
    access, secret = "local-" + uuid.uuid4().hex, uuid.uuid4().hex + uuid.uuid4().hex
    bucket = "bsync-" + uuid.uuid4().hex
    endpoint = "http://127.0.0.1:59005"
    env = os.environ.copy()
    env.update(AWS_ACCESS_KEY_ID=access, AWS_SECRET_ACCESS_KEY=secret, AWS_REGION="us-east-1",
               S3_BUCKET=bucket, BSYNC_OBJECTSTORE_ENDPOINT=endpoint, BSYNC_OBJECTSTORE_BUCKET=bucket,
               BSYNC_OBJECTSTORE_ORIGIN_PORT="58991")
    env.pop("AWS_SESSION_TOKEN", None)
    command = [str(executable), "mini", "-dir=" + str(data), "-ip=127.0.0.1", "-ip.bind=127.0.0.1",
               "-s3.port=59005", "-s3.allowedOrigins=http://127.0.0.1:58991", "-s3.allowDeleteBucketNotEmpty=false",
               "-admin.ui=false", "-volume.max=2", "-master.volumeSizeLimitMB=64"]
    version = subprocess.run([str(executable), "version"], capture_output=True, text=True, check=True).stdout.strip()
    (output / "server.json").write_text(json.dumps(dict(version=version,
        executableSha256=hashlib.sha256(executable.read_bytes()).hexdigest(), command=command,
        endpoint=endpoint, credentials="random ephemeral test credentials; omitted"), indent=2))
    # SeaweedFS accepts generic environment names as flags (e.g. DEBUG). Isolate inherited developer settings.
    server_env = {key: value for key, value in env.items() if key.upper() in {
        "SYSTEMROOT", "WINDIR", "PATH", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_REGION", "S3_BUCKET"}}
    server = subprocess.Popen(command, env=server_env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              text=True, encoding="utf-8", errors="replace", creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

    def retain_log():
        with (output / "server.log").open("w", encoding="utf-8") as log:
            for line in server.stdout:
                line = line.replace(access, "[test-access]").replace(secret, "[test-secret]")
                line = re.sub(r"(?i)(https?://[^\s?]+)\?[^\s]+", r"\1?[redacted]", line)
                log.write(line)

    logger = threading.Thread(target=retain_log, daemon=True)
    logger.start()
    try:
        s3 = boto3.client("s3", endpoint_url=endpoint, aws_access_key_id=access,
                          aws_secret_access_key=secret, region_name="us-east-1")
        for _ in range(120):
            if server.poll() is not None:
                raise RuntimeError("Local service exited during startup; see redacted server log")
            try:
                s3.head_bucket(Bucket=bucket)
                break
            except Exception:
                time.sleep(.5)
        else:
            raise RuntimeError("Local service did not become ready")
        s3.put_bucket_cors(Bucket=bucket, CORSConfiguration={"CORSRules": [dict(
            AllowedOrigins=["http://127.0.0.1:58991"], AllowedMethods=["PUT", "GET", "HEAD"],
            AllowedHeaders=["Content-Type", "Range"], ExposeHeaders=["Content-Range"])]})
        with (output / "qualification.log").open("w") as log:
            completed = subprocess.run(["dotnet", str(ROOT / "src/Tools/Bsync.ObjectStoreQualification/bin/Release/net10.0/Bsync.ObjectStoreQualification.dll"),
                str(output / "qualification"), "--local-service"], env=env, stdout=log, stderr=subprocess.STDOUT, timeout=660)
        result = json.loads((output / "qualification/result.json").read_text())
        assert result["ExternalServiceQualified"] is False and result["Emulator"] is False
        assert not s3.list_objects_v2(Bucket=bucket).get("Contents"), "Generated objects were not cleaned up"
        s3.delete_bucket(Bucket=bucket)
        print(json.dumps(dict(exitCode=completed.returncode, checks=[(item["Name"], item["Passed"]) for item in result["Checks"]])))
        return completed.returncode
    finally:
        server.terminate()
        try:
            server.wait(timeout=10)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait()
        logger.join(timeout=5)


if __name__ == "__main__":
    raise SystemExit(main())
