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


def main() -> int:
    password = os.environ.get("RE_ADMIN_WEB_PASSWORD", "")
    if not password:
        print("webdash_password_hash: RE_ADMIN_WEB_PASSWORD is empty", file=sys.stderr)
        return 2
    digest = hashlib.md5(password.encode("utf-8")).digest()
    sys.stdout.write(base64.b64encode(digest).decode())
    return 0


if __name__ == "__main__":
    sys.exit(main())
