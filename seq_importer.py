import argparse
import json
import sys
import time
import zipfile

from pycomm3 import LogixDriver


REQUIRED_ARRAY_LEN = 100


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


class WriteProgress:
    """Tracks how far a download got, so a failure can say what the controller holds."""

    def __init__(self):
        self.completed = []
        self.current = None
        self.current_elements = 0

    def start(self, base_tag):
        self.current = base_tag
        self.current_elements = 0

    def finish(self):
        if self.current is not None:
            self.completed.append(self.current)
        self.current = None
        self.current_elements = 0


def iter_json_entries(zip_path: str):
    with zipfile.ZipFile(zip_path, "r") as zf:
        for entry in zf.infolist():
            if not entry.filename.lower().endswith(".json"):
                continue
            with zf.open(entry, "r") as handle:
                yield entry.filename, handle.read().decode("utf-8")


def write_seq100_elements(plc: LogixDriver, base_tag: str, values, chunk_size: int = 20, progress=None):
    if len(values) != REQUIRED_ARRAY_LEN:
        raise ValueError(f"{base_tag}: expected {REQUIRED_ARRAY_LEN} elements, got {len(values)}")

    for start in range(0, REQUIRED_ARRAY_LEN, chunk_size):
        batch = []
        for idx in range(start, min(start + chunk_size, REQUIRED_ARRAY_LEN)):
            batch.append((f"{base_tag}[{idx}]", values[idx]))
        results = plc.write(*batch)
        if not isinstance(results, list):
            results = [results]
        for res in results:
            if res.error:
                raise RuntimeError(f"{res.tag}: {res.error}")
        if progress is not None:
            progress.current_elements = min(start + chunk_size, REQUIRED_ARRAY_LEN)


def report_partial_state(progress: WriteProgress):
    """Writes to a live controller are not transactional, so on a final failure the
    operator needs to know exactly what did and did not land."""
    print("", file=sys.stderr)
    print("=" * 64, file=sys.stderr)
    print("WARNING: the controller may be in a PARTIALLY UPDATED state.", file=sys.stderr)
    print("=" * 64, file=sys.stderr)

    if progress.completed:
        print(f"Fully written ({len(progress.completed)} tag(s)):", file=sys.stderr)
        for tag in progress.completed:
            print(f"  - {tag}", file=sys.stderr)
    else:
        print("Fully written: none", file=sys.stderr)

    if progress.current is not None:
        print(
            f"INTERRUPTED partway through: {progress.current} "
            f"({progress.current_elements} of {REQUIRED_ARRAY_LEN} elements confirmed written)",
            file=sys.stderr,
        )

    print("", file=sys.stderr)
    print("Re-run the download to return every tag to a known state.", file=sys.stderr)
    print("=" * 64, file=sys.stderr)


def main():
    args = parse_args()
    path = f"{args.ip}/{args.eth_slot}/{args.cpu_slot}"

    attempts = max(1, int(args.retries))
    delay = max(0.0, float(args.retry_delay))
    timeout = float(args.timeout)

    for attempt in range(1, attempts + 1):
        # Fresh per attempt: a retry rewrites every tag from the start.
        progress = WriteProgress()
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
                    progress.start(base_tag)
                    write_seq100_elements(plc, base_tag, values, progress=progress)
                    progress.finish()

            print(f"Download to PLC complete. {len(progress.completed)} tag(s) written.")
            return
        except Exception as exc:
            if attempt >= attempts:
                report_partial_state(progress)
                raise
            print(f"Retrying PLC download ({attempt}/{attempts}) after error: {exc}", file=sys.stderr)
            time.sleep(delay)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise
