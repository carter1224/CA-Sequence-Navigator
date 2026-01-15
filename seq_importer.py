import argparse
import json
import sys
import time
import zipfile

from pycomm3 import LogixDriver


def parse_args():
    parser = argparse.ArgumentParser(description="Import SEQ[100] JSONs from a ZIP into a PLC.")
    parser.add_argument("--ip", required=True, help="Controller IP address.")
    parser.add_argument("--eth-slot", type=int, required=True, help="Ethernet slot number.")
    parser.add_argument("--cpu-slot", type=int, required=True, help="Controller slot number.")
    parser.add_argument("--zip", required=True, help="ZIP file containing SEQ JSONs.")
    parser.add_argument("--retries", type=int, default=5, help="Retry count on PLC errors.")
    parser.add_argument("--retry-delay", type=float, default=2.0, help="Delay between retries in seconds.")
    parser.add_argument("--timeout", type=float, default=5.0, help="PLC timeout in seconds.")
    return parser.parse_args()


def iter_json_entries(zip_path: str):
    with zipfile.ZipFile(zip_path, "r") as zf:
        for entry in zf.infolist():
            if not entry.filename.lower().endswith(".json"):
                continue
            with zf.open(entry, "r") as handle:
                yield entry.filename, handle.read().decode("utf-8")


def write_seq100_elements(plc: LogixDriver, base_tag: str, values, chunk_size: int = 20):
    if len(values) != 100:
        raise ValueError(f"{base_tag}: expected 100 elements, got {len(values)}")

    for start in range(0, 100, chunk_size):
        batch = []
        for idx in range(start, min(start + chunk_size, 100)):
            batch.append((f"{base_tag}[{idx}]", values[idx]))
        results = plc.write(*batch)
        if not isinstance(results, list):
            results = [results]
        for res in results:
            if res.error:
                raise RuntimeError(f"{res.tag}: {res.error}")


def main():
    args = parse_args()
    path = f"{args.ip}/{args.eth_slot}/{args.cpu_slot}"

    attempts = max(1, int(args.retries))
    delay = max(0.0, float(args.retry_delay))
    timeout = float(args.timeout)
    for attempt in range(1, attempts + 1):
        try:
            try:
                plc_ctx = LogixDriver(path, timeout=timeout)
            except TypeError:
                plc_ctx = LogixDriver(path)
            with plc_ctx as plc:
                for filename, text in iter_json_entries(args.zip):
                    payload = json.loads(text)
                    base_tag = payload.get("source_tag_name") or filename.rsplit(".", 1)[0]
                    values = payload.get("value")
                    if not isinstance(values, list):
                        raise ValueError(f"{filename}: missing 'value' array")
                    write_seq100_elements(plc, base_tag, values)

            print("Export to PLC complete.")
            return
        except Exception as exc:
            if attempt >= attempts:
                raise
            print(f"Retrying PLC export ({attempt}/{attempts}) after error: {exc}", file=sys.stderr)
            time.sleep(delay)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise
