"""Independently verify Penghou.Siming v1 and canonical JSON v2 vectors.

The v2 implementation intentionally does not use Python's floating-point
types. JSON numbers are retained as their source tokens and normalized with
integer/string operations matching the documented contract.
"""

from __future__ import annotations

import hashlib
import json
import re
import struct
import sys
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any


MAX_NUMBER_LENGTH = 1_000_000
MAX_EXPONENT_LENGTH = 128
NUMBER_RE = re.compile(r"-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\Z")


@dataclass(frozen=True)
class JsonNumber:
    raw: str


def sized(value: bytes) -> bytes:
    return struct.pack(">i", len(value)) + value


def reject_constant(value: str) -> Any:
    raise ValueError(f"non-JSON number constant: {value}")


def pairs_without_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for name, value in pairs:
        if name in result:
            raise ValueError(f"duplicate JSON property: {name!r}")
        result[name] = value
    return result


def parse_json(text: str) -> Any:
    return json.loads(
        text,
        parse_int=JsonNumber,
        parse_float=JsonNumber,
        parse_constant=reject_constant,
        object_pairs_hook=pairs_without_duplicates,
    )


def utf16_sort_key(value: str) -> bytes:
    return value.encode("utf-16-be", errors="surrogatepass")


def escape_string(value: str) -> str:
    """Match Utf8JsonWriter's default JavaScriptEncoder output."""

    escaped: list[str] = ['"']
    for char in value:
        codepoint = ord(char)
        if char == '"':
            escaped.append("\\u0022")
        elif char == "\\":
            escaped.append("\\\\")
        elif char == "\b":
            escaped.append("\\b")
        elif char == "\f":
            escaped.append("\\f")
        elif char == "\n":
            escaped.append("\\n")
        elif char == "\r":
            escaped.append("\\r")
        elif char == "\t":
            escaped.append("\\t")
        elif 0x20 <= codepoint <= 0x7E and char not in "&'<>":
            escaped.append(char)
        elif codepoint <= 0xFFFF:
            escaped.append(f"\\u{codepoint:04X}")
        else:
            codepoint -= 0x10000
            high = 0xD800 + (codepoint >> 10)
            low = 0xDC00 + (codepoint & 0x3FF)
            escaped.append(f"\\u{high:04X}\\u{low:04X}")
    escaped.append('"')
    return "".join(escaped)


def canonicalize_number(raw: str) -> str:
    if not 1 <= len(raw) <= MAX_NUMBER_LENGTH or NUMBER_RE.fullmatch(raw) is None:
        raise ValueError("invalid or oversized JSON number")

    cursor = 1 if raw.startswith("-") else 0
    mantissa_end = len(raw)
    for index in range(cursor, len(raw)):
        if raw[index] in "eE":
            mantissa_end = index
            break

    exponent = 0
    if mantissa_end < len(raw):
        exponent_text = raw[mantissa_end + 1 :]
        exponent_digits = exponent_text.lstrip("+-")
        if len(exponent_digits) > MAX_EXPONENT_LENGTH:
            raise ValueError("JSON number exponent is too long")
        exponent = int(exponent_text)

    mantissa = raw[cursor:mantissa_end]
    point = mantissa.find(".")
    integer_length = len(mantissa) if point < 0 else point
    digits = mantissa if point < 0 else mantissa[:point] + mantissa[point + 1 :]
    first = 0
    while first < len(digits) and digits[first] == "0":
        first += 1
    if first == len(digits):
        return "0"

    decimal_position = integer_length + exponent - first
    digits = digits[first:]
    trailing = len(digits)
    while trailing > 1 and digits[trailing - 1] == "0":
        trailing -= 1
    digits = digits[:trailing]

    scientific_exponent = decimal_position - 1
    if -6 <= scientific_exponent < 21:
        if decimal_position <= 0:
            result = "0." + "0" * (-decimal_position) + digits
        elif decimal_position >= len(digits):
            result = digits + "0" * (decimal_position - len(digits))
        else:
            result = digits[:decimal_position] + "." + digits[decimal_position:]
    else:
        exponent_text = str(scientific_exponent)
        negative_exponent = exponent_text.startswith("-")
        magnitude = exponent_text[1:] if negative_exponent else exponent_text
        if len(magnitude) == 1:
            magnitude = "0" + magnitude
        mantissa_text = digits if len(digits) == 1 else digits[0] + "." + digits[1:]
        result = mantissa_text + "E" + ("-" if negative_exponent else "+") + magnitude

    if len(result) > MAX_NUMBER_LENGTH:
        raise ValueError("canonical JSON number is oversized")
    return ("-" if raw.startswith("-") else "") + result


def canonicalize(value: Any) -> str:
    if isinstance(value, JsonNumber):
        return canonicalize_number(value.raw)
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, str):
        return escape_string(value)
    if isinstance(value, list):
        return "[" + ",".join(canonicalize(item) for item in value) + "]"
    if isinstance(value, dict):
        properties = sorted(value.items(), key=lambda item: utf16_sort_key(item[0]))
        return "{" + ",".join(escape_string(name) + ":" + canonicalize(item) for name, item in properties) + "}"
    raise TypeError(f"unsupported JSON value: {type(value).__name__}")


def verify_v1(root: Path) -> str:
    vector = json.loads((root / "vectors" / "ledger-format-v1.json").read_text(encoding="utf-8"))
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
        raise ValueError(f"v1 expected={expected} actual={actual}")
    return actual


def verify_v2(root: Path) -> int:
    vectors = json.loads((root / "vectors" / "penghou-canonical-json-v2.json").read_text(encoding="utf-8"))
    for vector in vectors:
        value = parse_json(vector["inputJson"])
        actual_json = canonicalize(value)
        actual_hash = hashlib.sha256(actual_json.encode("utf-8")).hexdigest()
        if actual_json != vector["canonicalJson"]:
            raise ValueError(f"v2 {vector['name']} canonical JSON mismatch: {actual_json}")
        if actual_hash != vector["sha256"]:
            raise ValueError(f"v2 {vector['name']} expected={vector['sha256']} actual={actual_hash}")
    return len(vectors)


def main() -> int:
    root = Path(__file__).parents[1]
    try:
        v1_hash = verify_v1(root)
        v2_count = verify_v2(root)
    except (OSError, ValueError, TypeError, json.JSONDecodeError) as error:
        print(f"FAIL {error}")
        return 1
    print(f"PASS Penghou.Siming v1 golden vector: {v1_hash}")
    print(f"PASS Penghou.Siming v2 canonical JSON vectors: {v2_count}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
