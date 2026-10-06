"""Reads who a controller is, without touching any of its data.

Used by the PLC Connection dialog's "Test connection" so the engineer sees the project
name, model and keyswitch before anything is transferred. Read-only: it opens a
connection, reads the identity pycomm3 collects on connect, and closes.
"""
import argparse
import json
import sys

from pycomm3 import LogixDriver


def parse_args():
    parser = argparse.ArgumentParser(description="Print a controller's identity as JSON.")
    parser.add_argument("--ip", required=True, help="Controller IP address.")
    parser.add_argument("--eth-slot", type=int, required=True, help="Backplane port (always 1).")
    parser.add_argument("--cpu-slot", type=int, required=True, help="Controller slot number.")
    parser.add_argument("--timeout", type=float, default=5.0, help="PLC timeout in seconds.")
    return parser.parse_args()


def main():
    args = parse_args()
    path = f"{args.ip}/{args.eth_slot}/{args.cpu_slot}"

    # init_tags=False skips uploading the whole tag list, which is all this needs to avoid.
    try:
        plc_ctx = LogixDriver(path, init_tags=False, timeout=args.timeout)
    except TypeError:
        plc_ctx = LogixDriver(path, init_tags=False)

    with plc_ctx as plc:
        info = plc.info or {}
        revision = info.get("revision") or {}
        identity = {
            "name": info.get("name"),
            "product_name": info.get("product_name"),
            "revision": (
                f"{revision.get('major')}.{revision.get('minor')}"
                if isinstance(revision, dict) and revision.get("major") is not None
                else None
            ),
            "serial": info.get("serial"),
            "keyswitch": info.get("keyswitch"),
        }
    print(json.dumps(identity))


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
