#!/usr/bin/env python3
"""Print the 7DTD webuser password hash for RE_ADMIN_WEB_PASSWORD.

7DTD stores web-dashboard passwords as base64(MD5(utf8(pass))). The password
arrives via the environment, never argv: a password argument would be
ps-visible for the lifetime of this short-lived process.
"""

from __future__ import annotations

import base64
import hashlib
import os
import sys

USAGE = "usage: webdash_password_hash.py (reads RE_ADMIN_WEB_PASSWORD from the environment)"


def main() -> int:
    args = sys.argv[1:]
    if args and args[0] in ("-h", "--help"):
        print(__doc__.strip())
        print(USAGE)
        return 0
    # The password is read from the environment, so argv can only be a mistake
    # (usually the password itself, which must never reach ps). Fail loudly
    # rather than hashing whatever the environment happens to hold.
    if args:
        print("webdash_password_hash: this tool takes no arguments", file=sys.stderr)
        print(USAGE, file=sys.stderr)
        return 2
    password = os.environ.get("RE_ADMIN_WEB_PASSWORD", "")
    if not password:
        print("webdash_password_hash: RE_ADMIN_WEB_PASSWORD is empty", file=sys.stderr)
        return 2
    digest = hashlib.md5(password.encode("utf-8")).digest()
    sys.stdout.write(base64.b64encode(digest).decode())
    return 0


if __name__ == "__main__":
    sys.exit(main())
