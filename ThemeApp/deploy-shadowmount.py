"""Stage a verified native app folder for ShadowMount+, then request its documented rescan.

Without --deploy, print the local file plan only. Existing deployments are preserved in a dated
backup. Nothing writes system assets or invokes the old non-launchable /user/app directory route.
With --scan-only, skip the module and FTP steps and only send the rescan request. If ShadowMount
defers the scan (scan_deferred: the app is still running or suspended), the script warns, asks you
to close the app from its tile menu, and exits with status 2; run it again with --scan-only after that.
"""
import argparse
from datetime import datetime, timezone
import ftplib
import hashlib
import io
import json
from pathlib import Path
import re
import sys
import urllib.request


def digest(data):
    return hashlib.sha256(data).hexdigest()


def mkdirs(ftp, path):
    current = ""
    for component in path.strip("/").split("/"):
        current += "/" + component
        try:
            ftp.mkd(current)
        except ftplib.error_perm:
            # Confirm it really is an existing directory; do not ignore a permissions failure.
            ftp.cwd(current)
    ftp.cwd("/")


def exists(ftp, path):
    try:
        ftp.cwd(path)
        ftp.cwd("/")
        return True
    except ftplib.error_perm:
        return False


def find_flag(obj, key):
    """True if any dict nested anywhere in the parsed JSON value has `key` set to a truthy value."""
    if isinstance(obj, dict):
        return bool(obj.get(key)) or any(find_flag(value, key) for value in obj.values())
    if isinstance(obj, list):
        return any(find_flag(item, key) for item in obj)
    return False


def request_scan(ip, api_port):
    request = urllib.request.Request(f"http://{ip}:{api_port}/api/v1/scan",
        data=json.dumps(dict(reset_attempts=True)).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=10) as response:
        result = json.load(response)
    print(json.dumps(result, indent=2))
    if find_flag(result, "scan_deferred"):
        lines = ["WARNING: ShadowMount deferred the scan, so it did not run."]
        if find_flag(result, "game_active"):
            lines.append("ShadowMount reports game_active: the app is still running or suspended on the console.")
        else:
            lines.append("This is probably because the app is still running or suspended on the console.")
        lines += [
            "The app files are deployed, but starting the app before the scan completes can fail.",
            "Fix: on the console, highlight the app's tile, press Options, and choose Close App.",
            "Then run this script again with --scan-only:",
            f"  python3 {sys.argv[0]} --scan-only --ip {ip} --api-port {api_port}",
        ]
        print("\n".join(lines), file=sys.stderr)
        sys.exit(2)
    if result.get("status") != 0:
        raise RuntimeError("ShadowMount rejected the scan request; deployed app is preserved.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ip", help="the console's address (needed for --deploy and --scan-only)")
    parser.add_argument("--api-port", type=int, default=10101)
    parser.add_argument("--module", type=Path, default=Path(__file__).parent / "out" / "module")
    parser.add_argument("--deploy", action="store_true")
    parser.add_argument("--scan-only", action="store_true",
        help="only send the ShadowMount rescan request; no module read and no FTP")
    args = parser.parse_args()
    if (args.deploy or args.scan_only) and not args.ip:
        parser.error("--deploy and --scan-only need --ip")
    if args.scan_only:
        if args.deploy:
            parser.error("--scan-only cannot be combined with --deploy")
        request_scan(args.ip, args.api_port)
        return
    module = args.module.resolve()
    metadata = json.loads((module / "sce_sys" / "param.json").read_text(encoding="utf-8-sig"))
    title = metadata["titleId"]
    if not re.fullmatch(r"PPSA\d{5}", title):
        raise ValueError("Invalid title id.")
    if (module / "eboot.bin").read_bytes()[:4] == b"\x7fELF":
        raise ValueError("eboot.bin is unsigned; run the full application build.")
    required = ["sce_sys/prospero-themes/blue-hour.p5theme", "sce_sys/prospero-themes/inter-22.p5font", "sce_sys/prospero-themes/inter-28.p5font", "sce_sys/prospero-themes/inter-36.p5font", "sce_sys/prospero-themes/inter-76.p5font", "sce_sys/prospero-themes/inter-48.p5font", "sce_sys/prospero-themes/inter-28-semibold.p5font", "sce_module/libc.prx"]
    for name in required:
        if not (module / name).is_file():
            raise ValueError(f"Missing app file: {name}")
    # Match the packager's source trees; stale SDK iteration folders are not app resources.
    files = sorted(p for p in module.rglob("*") if p.is_file() and
        (p.relative_to(module).parts[0] in {"sce_sys", "sce_module"} or p.relative_to(module).as_posix() == "eboot.bin"))
    target = "/data/homebrew/" + title
    print(json.dumps(dict(title=title, target=target, files=len(files), bytes=sum(p.stat().st_size for p in files), deploy=args.deploy)))
    if not args.deploy:
        return
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    stage = f"/data/prospero-themes/incoming/app-{title}-{timestamp}"
    with ftplib.FTP() as ftp:
        ftp.connect(args.ip, 2121, timeout=30)
        ftp.login("anonymous", "codex")
        mkdirs(ftp, stage)
        for path in files:
            relative = path.relative_to(module).as_posix()
            remote = stage + "/" + relative
            mkdirs(ftp, remote.rsplit("/", 1)[0])
            data = path.read_bytes()
            ftp.storbinary("STOR " + remote, io.BytesIO(data), blocksize=128 * 1024)
            echo = io.BytesIO()
            ftp.retrbinary("RETR " + remote, echo.write, blocksize=128 * 1024)
            if digest(echo.getvalue()) != digest(data):
                raise RuntimeError(f"Read-back verification failed: {relative}")
            print("verified " + relative)
        backup = None
        if exists(ftp, target):
            backup = f"/data/prospero-themes/app-backup/{title}-{timestamp}"
            mkdirs(ftp, backup.rsplit("/", 1)[0])
            ftp.rename(target, backup)
        try:
            ftp.rename(stage, target)
        except Exception:
            if backup is not None:
                ftp.rename(backup, target)
            raise
        print("deployed " + target)
        if backup:
            print("previous app preserved at " + backup)
    request_scan(args.ip, args.api_port)


if __name__ == "__main__":
    main()
