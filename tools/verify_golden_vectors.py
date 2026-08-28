"""Independent Penghou.Siming v1 golden-vector verifier (Python 3 stdlib)."""

import hashlib
import json
import struct
import sys
import uuid
from pathlib import Path


def sized(value: bytes) -> bytes:
    return struct.pack(">i", len(value)) + value


def main() -> int:
    vector_path = Path(__file__).parents[1] / "vectors" / "ledger-format-v1.json"
    vector = json.loads(vector_path.read_text(encoding="utf-8"))
    previous = hashlib.sha256(vector["genesisText"].encode("utf-8")).digest()
    encoded = b"".join(
        [
            struct.pack(">i", vector["formatVersion"]),
            uuid.UUID(vector["ledgerId"]).bytes,
            struct.pack(">q", vector["sequence"]),
            struct.pack(">q", vector["committedAtUnixMilliseconds"]),
            sized(vector["streamId"].encode("utf-8")),
            sized(vector["eventType"].encode("utf-8")),
            sized(vector["contentType"].encode("utf-8")),
            sized(vector["serializationFormat"].encode("utf-8")),
            struct.pack(">i", vector["serializationVersion"]),
            sized(vector["payloadUtf8"].encode("utf-8")),
            struct.pack(">i", -1)
            if vector["idempotencyKey"] is None
            else sized(vector["idempotencyKey"].encode("utf-8")),
            previous,
        ]
    )
    actual = hashlib.sha256(encoded).hexdigest()
    expected = vector["expectedHashHex"]
    if actual != expected:
        print(f"FAIL expected={expected} actual={actual}")
        return 1
    print(f"PASS Penghou.Siming v1 golden vector: {actual}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
