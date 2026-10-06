import argparse
import json
import os
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


def read_all_seq_tags(plc: LogixDriver, include_program_tags: bool) -> dict:
    """Reads every SEQ[100] tag into memory as {zip entry name: JSON text}.

    Nothing touches the disk here. A backup holding only some of the tags looks like a
    complete one, so the file is written only once this has returned every tag.
    """
    tags = plc.get_tag_list(program="*" if include_program_tags else None)
    matches = [t for t in tags if is_seq_100(t)]
    names = sorted([t.get("tag_name", "") for t in matches if t.get("tag_name")], key=str.lower)
    if not names:
        raise RuntimeError("No SEQ[100] tags were found on the controller; nothing was saved.")

    entries = {}
    for base in names:
        values = read_seq100_elements(plc, base, chunk_size=20)
        payload = {
            "source_tag_name": base,
            "required_definition": f"{REQUIRED_UDT_NAME} dims [{REQUIRED_ARRAY_LEN},0,0]",
            "value": values,
        }
        entries[f"{safe_filename(base)}.json"] = json.dumps(payload, indent=2)

    # Two tag names that clean up to the same file name would silently overwrite one.
    if len(entries) != len(names):
        raise RuntimeError(
            f"Read {len(names)} SEQ[100] tags but they map to only {len(entries)} file names; nothing was saved."
        )
    return entries


def write_zip_atomically(out_path: Path, entries: dict) -> None:
    """Writes the ZIP beside its destination, checks it, then swaps it into place.

    An existing file at out_path stays untouched until the new one is complete and
    verified, and a failure part-way leaves nothing behind.
    """
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = out_path.with_name(out_path.name + ".partial")
    try:
        with zipfile.ZipFile(tmp_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
            for name, text in entries.items():
                zf.writestr(name, text)

        with zipfile.ZipFile(tmp_path, "r") as zf:
            bad = zf.testzip()
            if bad is not None:
                raise RuntimeError(f"The written backup failed its check at {bad}; nothing was saved.")
            if len(zf.namelist()) != len(entries):
                raise RuntimeError(
                    f"The written backup holds {len(zf.namelist())} of {len(entries)} tags; nothing was saved."
                )

        os.replace(tmp_path, out_path)
    except BaseException:
        tmp_path.unlink(missing_ok=True)
        raise


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
    path = f"{args.ip}/{args.eth_slot}/{args.cpu_slot}"

    attempts = max(1, int(args.retries))
    delay = max(0.0, float(args.retry_delay))
    timeout = float(args.timeout)
    entries = None
    for attempt in range(1, attempts + 1):
        try:
            try:
                plc_ctx = LogixDriver(path, timeout=timeout)
            except TypeError:
                plc_ctx = LogixDriver(path)
            with plc_ctx as plc:
                entries = read_all_seq_tags(plc, args.include_program_tags)
            break
        except Exception as exc:
            if attempt >= attempts:
                raise
            print(f"Retrying PLC export ({attempt}/{attempts}) after error: {exc}", file=sys.stderr)
            time.sleep(delay)

    # Only now, with every tag read, does anything get written to disk.
    write_zip_atomically(out_path, entries)
    print(f"Exported {len(entries)} tag(s).")
    print(f"ZIP file: {out_path}")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise
