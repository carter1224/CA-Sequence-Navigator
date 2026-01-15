import argparse
import json
import sys
import time
import zipfile
from pathlib import Path

from pycomm3 import LogixDriver


REQUIRED_UDT_NAME = "SEQ"
REQUIRED_ARRAY_LEN = 100


def safe_filename(name: str) -> str:
    bad = r'<>:"/\\|?*'
    out = name
    for ch in bad:
        out = out.replace(ch, "_")
    out = out.replace("[", "_").replace("]", "_")
    out = out.strip().strip(".")
    return out or "tag"


def is_seq_100(tag_def: dict) -> bool:
    if tag_def.get("data_type_name") != REQUIRED_UDT_NAME:
        return False
    dims = tag_def.get("dimensions") or [0, 0, 0]
    while len(dims) < 3:
        dims.append(0)
    return dims[0] == REQUIRED_ARRAY_LEN and dims[1] == 0 and dims[2] == 0


def ensure_snapshot_dict(value) -> dict:
    if value is None:
        raise ValueError("value is None")
    if not isinstance(value, dict):
        raise ValueError(f"unexpected element value type {type(value).__name__} (expected dict snapshot)")
    return value


def read_seq100_elements(plc: LogixDriver, base_tag: str, chunk_size: int):
    elems = [None] * REQUIRED_ARRAY_LEN
    element_tags = [f"{base_tag}[{i}]" for i in range(REQUIRED_ARRAY_LEN)]

    for i in range(0, len(element_tags), chunk_size):
        chunk = element_tags[i:i + chunk_size]
        results = plc.read(*chunk)
        if not isinstance(results, list):
            results = [results]

        for r in results:
            if r.error:
                raise RuntimeError(f"{r.tag}: {r.error}")
            try:
                idx = int(r.tag.split("[", 1)[1].split("]", 1)[0])
            except Exception:
                raise RuntimeError(f"Could not parse index from returned tag name: {r.tag}")
            elems[idx] = ensure_snapshot_dict(r.value)

    missing = [i for i, v in enumerate(elems) if v is None]
    if missing:
        raise RuntimeError(
            f"{base_tag}: missing elements at indices {missing[:10]}" + ("..." if len(missing) > 10 else "")
        )
    return elems


def parse_args():
    parser = argparse.ArgumentParser(description="Export SEQ[100] tags to JSON and zip the results.")
    parser.add_argument("--ip", required=True, help="Controller IP address.")
    parser.add_argument("--eth-slot", type=int, required=True, help="Ethernet slot number.")
    parser.add_argument("--cpu-slot", type=int, required=True, help="Controller slot number.")
    parser.add_argument("--out-zip", required=True, help="Output zip file path.")
    parser.add_argument("--include-program-tags", action="store_true", help="Include program-scoped tags.")
    parser.add_argument("--retries", type=int, default=5, help="Retry count on PLC errors.")
    parser.add_argument("--retry-delay", type=float, default=2.0, help="Delay between retries in seconds.")
    parser.add_argument("--timeout", type=float, default=5.0, help="PLC timeout in seconds.")
    return parser.parse_args()


def main():
    args = parse_args()

    out_path = Path(args.out_zip).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)

    path = f"{args.ip}/{args.eth_slot}/{args.cpu_slot}"

    exported = 0
    attempts = max(1, int(args.retries))
    delay = max(0.0, float(args.retry_delay))
    timeout = float(args.timeout)
    for attempt in range(1, attempts + 1):
        try:
            with zipfile.ZipFile(out_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
                try:
                    plc_ctx = LogixDriver(path, timeout=timeout)
                except TypeError:
                    plc_ctx = LogixDriver(path)
                with plc_ctx as plc:
                    tags = plc.get_tag_list(program="*" if args.include_program_tags else None)
                    matches = [t for t in tags if is_seq_100(t)]
                    names = sorted([t.get("tag_name", "") for t in matches if t.get("tag_name")], key=str.lower)

                    for base in names:
                        values = read_seq100_elements(plc, base, chunk_size=20)
                        base_name = safe_filename(base)
                        payload = {
                            "source_tag_name": base,
                            "required_definition": f"{REQUIRED_UDT_NAME} dims [{REQUIRED_ARRAY_LEN},0,0]",
                            "value": values,
                        }
                        json_text = json.dumps(payload, indent=2)
                        zf.writestr(f"{base_name}.json", json_text)
                        exported += 1

            print(f"Exported {exported} tag(s).")
            print(f"ZIP file: {out_path}")
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
