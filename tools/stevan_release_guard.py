#!/usr/bin/env python3
"""Fail-closed signer/package/version/SHA guard for STEVAN Play catalog APKs."""
import hashlib, json, os, pathlib, re, subprocess, sys
from urllib.parse import unquote, urlparse

ROOT = pathlib.Path(__file__).resolve().parents[1]
PINNED = {
    "rs.lajkovac.majstori": "beb4ca91afcb90a615dc82ad2a1a5cbc9884283a9cd12681390ee1320f197063",
    "rs.imperijal014.salon.direct": "beb4ca91afcb90a615dc82ad2a1a5cbc9884283a9cd12681390ee1320f197063",
    "rs.stevan.pretrazivac.javni": "8cbcd0c202d7d35ffda188fd0a73c1a0b87848baca359994e98ce0a6130b2ecc",
}
HOST = "014sita-rgb.github.io"
PREFIX = "/stevan-play-site/"

def tool(name):
    sdk = pathlib.Path(os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT") or "")
    matches = sorted((sdk / "build-tools").glob("*/" + name), reverse=True)
    if not matches:
        raise RuntimeError("Missing Android build-tools executable: " + name)
    return str(matches[0])

def run(args):
    result = subprocess.run(args, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if result.returncode != 0:
        raise RuntimeError("Command failed: " + " ".join(args) + "\n" + result.stdout)
    return result.stdout


def signing_cert_sha256(apk):
    """Extract first signer certificate from APK v2/v3 signing block."""
    import struct
    b = apk.read_bytes()
    eocd = b.rfind(b"PK\x05\x06", max(0, len(b) - 65557))
    if eocd < 0:
        raise RuntimeError("No ZIP central directory")
    cd = struct.unpack_from("<I", b, eocd+16)[0]
    if b[cd-16:cd] != b"APK Sig Block 42":
        raise RuntimeError("APK lacks v2/v3 signing block")
    size = struct.unpack_from("<Q", b, cd-24)[0]
    pos = cd - size
    end = cd - 24
    def blob(data):
        if len(data) < 4:
            raise RuntimeError("Invalid APK signing record")
        n = struct.unpack_from("<I", data, 0)[0]
        if n+4 > len(data):
            raise RuntimeError("Invalid signed-data length")
        return data[4:4+n], data[4+n:]
    while pos < end:
        n = struct.unpack_from("<Q", b, pos)[0]
        marker = struct.unpack_from("<I", b, pos+8)[0]
        if marker in (0x7109871a, 0xf05368c0, 0x1b93ad61):
            data = memoryview(b)[pos+12:pos+8+n]
            signers, _ = blob(data)
            first, _ = blob(signers)
            signed, _ = blob(first)
            _, rem = blob(signed)
            certificates, _ = blob(rem)
            first_cert, _ = blob(certificates)
            return hashlib.sha256(first_cert).hexdigest()
        pos += 8+n
    raise RuntimeError("No v2/v3 signer certificate")

def check(app):
    apk_url = urlparse(app["apkUrl"])
    if apk_url.scheme != "https" or apk_url.netloc != HOST or not apk_url.path.startswith(PREFIX):
        raise RuntimeError("APK URL outside trusted site")
    relative = pathlib.PurePosixPath(unquote(apk_url.path[len(PREFIX):]))
    if ".." in relative.parts:
        raise RuntimeError("APK URL contains traversal")
    apk = ROOT.joinpath(*relative.parts)
    if not apk.is_file():
        raise RuntimeError("APK file missing: " + str(apk))
    package = app["packageName"]
    expected_signer = PINNED.get(package)
    if not expected_signer:
        raise RuntimeError("Unknown package; signer must be pinned before publication: " + package)
    if app.get("signerSha256") and app["signerSha256"].replace(":", "").lower() != expected_signer:
        raise RuntimeError("Catalog signer conflicts with pinned historical signer")
    actual_hash = hashlib.sha256(apk.read_bytes()).hexdigest()
    if actual_hash != app.get("sha256", "").lower():
        raise RuntimeError("APK SHA-256 mismatch with catalog")
    run([tool("apksigner"), "verify", "--print-certs", str(apk)])
    signer = signing_cert_sha256(apk)
    if signer.lower() != expected_signer:
        raise RuntimeError("INCOMPATIBLE APK SIGNER, cannot update existing install")
    badge = run([tool("aapt"), "dump", "badging", str(apk)])
    m = re.search(r"^package: name='([^']+)' versionCode='([^']+)' versionName='([^']+)'", badge, re.M)
    if not m:
        raise RuntimeError("No manifest package/version details")
    actual_pkg, actual_code, actual_version = m.groups()
    if actual_pkg != package or int(actual_code) != int(app["versionCode"]) or actual_version != str(app["versionName"]):
        raise RuntimeError(f"APK identity mismatch: {actual_pkg} / {actual_code} / {actual_version}")
    print(f"PASS {app['id']}: {actual_pkg} {actual_version} ({actual_code}), SHA and pinned signer")

def main():
    catalog = json.loads((ROOT / "katalog/catalog.json").read_text(encoding="utf-8"))
    errors = 0
    for app in catalog["apps"]:
        if not app.get("enabled", True) or app.get("type", "apk") != "apk":
            continue
        try:
            check(app)
        except Exception as error:
            errors += 1
            print(f"BLOCKED {app.get('id')}: {error}", file=sys.stderr)
    if errors:
        raise SystemExit(f"BLOCKED: {errors} unsafe or inconsistent publication(s)")
    print("ALL ACTIVE APK ENTRIES VERIFIED")
if __name__ == "__main__":
    main()
