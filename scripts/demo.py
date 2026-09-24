#!/usr/bin/env python3
"""Exercise a running API with explicitly synthetic payment records. Stdlib only."""

import argparse
import csv
import io
import json
from pathlib import Path
import sys
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


ROOT = Path(__file__).resolve().parents[1]


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def read_records(path):
    with path.open(newline="", encoding="utf-8") as stream:
        return [{**row, "amountMinor": int(row["amountMinor"])} for row in csv.DictReader(stream)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://localhost:5087")
    parser.add_argument("--key-prefix", default="synthetic-demo-v1",
                        help="Stable import keys; reuse to verify retries (default: synthetic-demo-v1).")
    args = parser.parse_args()
    require(0 < len(args.key_prefix) <= 110 and all(
        char.isascii() and (char.isalnum() or char in "._:-") for char in args.key_prefix),
        "key-prefix must contain 1..110 ASCII letters, digits, dots, underscores, colons or hyphens")

    def request(method, path, payload=None, headers=None, expected=(200,)):
        headers = dict(headers or {})
        if payload is not None and not isinstance(payload, bytes):
            payload = json.dumps(payload).encode("utf-8")
            headers.setdefault("Content-Type", "application/json")
        req = Request(args.base_url.rstrip("/") + path, data=payload, headers=headers, method=method)
        try:
            with urlopen(req, timeout=30) as response:
                status, body = response.status, response.read()
        except HTTPError as error:
            status, body = error.code, error.read()
        require(status in expected, f"{method} {path}: expected {expected}, got {status}: {body.decode('utf-8', errors='replace')}")
        return status, body

    def json_request(*positional, **kwargs):
        status, body = request(*positional, **kwargs)
        return status, json.loads(body)

    for attempt in range(30):
        try:
            request("GET", "/health")
            break
        except (RuntimeError, URLError, TimeoutError, OSError):
            if attempt == 29:
                raise
            time.sleep(1)
    left_records = read_records(ROOT / "samples/ledger.csv")
    right_records = read_records(ROOT / "samples/provider.csv")
    left_headers = {"Idempotency-Key": args.key_prefix + ":ledger"}
    right_headers = {"Idempotency-Key": args.key_prefix + ":provider", "X-Source": "synthetic-provider",
                     "Content-Type": "text/csv"}
    left_payload = {"source": "synthetic-ledger", "records": left_records}
    _, left = json_request("POST", "/api/datasets", left_payload, left_headers, expected=(200, 201))
    right_csv = (ROOT / "samples/provider.csv").read_bytes()
    _, right = json_request("POST", "/api/datasets/csv", right_csv, right_headers, expected=(200, 201))
    require(left["recordCount"] == len(left_records) and right["recordCount"] == len(right_records),
            "Imported record counts differ from fixtures")

    # A normalized retry in a different order must return the same immutable dataset.
    _, left_retry = json_request("POST", "/api/datasets",
                                {"source": "synthetic-ledger", "records": list(reversed(left_records))}, left_headers)
    require(left_retry["id"] == left["id"], "JSON retry created a different dataset")
    _, right_retry = json_request("POST", "/api/datasets/csv", right_csv, right_headers)
    require(right_retry["id"] == right["id"], "CSV retry created a different dataset")
    _, cross_format = json_request("POST", "/api/datasets",
                                  {"source": "synthetic-provider", "records": right_records},
                                  {"Idempotency-Key": args.key_prefix + ":provider"})
    require(cross_format["id"] == right["id"], "CSV/JSON key identity differs")

    changed = [{**record} for record in left_records]
    changed[0]["amountMinor"] += 1
    request("POST", "/api/datasets", {"source": "synthetic-ledger", "records": changed},
            left_headers, expected=(409,))
    invalid_headers = {"Idempotency-Key": args.key_prefix + ":invalid", "X-Source": "synthetic-invalid",
                       "Content-Type": "text/csv"}
    request("POST", "/api/datasets/csv",
            b"sourceRecordId,reference,currency,amountMinor\nBAD,INV-BAD,TRY,12.50\n",
            invalid_headers, expected=(400,))

    pair = {"leftDatasetId": left["id"], "rightDatasetId": right["id"]}
    _, report = json_request("POST", "/api/reconciliations", pair, expected=(200, 201))
    expected = json.loads((ROOT / "samples/expected.json").read_text(encoding="utf-8"))
    require(report["groups"] == expected["groups"], "Report groups differ from samples/expected.json")
    summary = report["summary"]
    summary["totalsByCurrency"] = sorted(summary["totalsByCurrency"], key=lambda item: item["currency"])
    require(summary == expected["summary"], "Report counts or totals differ from samples/expected.json")
    _, retry_report = json_request("POST", "/api/reconciliations", pair)
    require(retry_report["id"] == report["id"], "Repeated pair created a different reconciliation")
    _, fetched = json_request("GET", "/api/reconciliations/" + report["id"])
    require(fetched["groups"] == expected["groups"], "Stored report differs from initial report")
    _, exported = request("GET", "/api/reconciliations/" + report["id"] + "/csv")
    reader = csv.DictReader(io.StringIO(exported.decode("utf-8-sig")))
    require(reader.fieldnames == ["status", "reference", "currency", "side", "sourceRecordId", "amountMinor"],
            "Unexpected export header")
    actual_rows = list(reader)
    expected_rows = [
        {"status": group["status"], "reference": group["reference"], "currency": group["currency"],
         "side": side, "sourceRecordId": record["sourceRecordId"], "amountMinor": str(record["amountMinor"])}
        for group in expected["groups"] for side in ("left", "right") for record in group[side]
    ]
    sort_key = lambda row: (row["side"], row["sourceRecordId"])
    require(sorted(actual_rows, key=sort_key) == sorted(expected_rows, key=sort_key),
            "CSV export lost, duplicated or changed source records")
    print("Synthetic demo verified: 7 ledger records, 6 provider records, 8 groups.")
    print("Verified JSON/CSV retries, shared keys, conflict, invalid row, stored report and CSV export.")
    print("Reconciliation:", report["id"])
    print("Report:", args.base_url.rstrip("/") + "/api/reconciliations/" + report["id"])


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, URLError, TimeoutError, OSError, ValueError, KeyError) as error:
        print("Demo failed:", error, file=sys.stderr)
        sys.exit(1)
