#!/usr/bin/env python3
"""Optional: download the exact captured vanilla 26.3 tags packet body.

The server loads this file instead of sending an empty tag update. Run from the
project root, with outbound HTTPS access. The packet ID is supplied by C#.
"""
from pathlib import Path
import hashlib
from urllib.request import Request, urlopen

URL = ("https://raw.githubusercontent.com/ezchr/go-mcjava/"
       "25ac9cf2bbbd/v777/vanilla/35_update_tags.bin")
OUTPUT = Path(__file__).resolve().parents[1] / "src" / "VanillaRegistries" / "35_update_tags.bin"


def main():
    req = Request(URL, headers={"User-Agent": "Amethyst-prototype/1.0"})
    with urlopen(req, timeout=30) as response:
        body = response.read()
    # Verify exact pinned Git blob rather than trusting a changed download.
    blob = b"blob " + str(len(body)).encode("ascii") + b"\0" + body
    digest = hashlib.sha1(blob).hexdigest()
    if digest != "ff949c6ed65cef786ea41d17f993242d89f5d68f":
        raise ValueError("Vanilla tags did not match the pinned 26.3 capture")
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_bytes(body)
    print(f"Wrote {len(body)} bytes to {OUTPUT}. Rebuild the server to copy them into bin/.")


if __name__ == "__main__":
    main()
