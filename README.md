# Sequence Transfer Tool

A Windows GUI utility for exporting and importing SEQ[100] tags to/from JSON using `pycomm3`.

## Features
- Export multiple SEQ[100] tags to JSON (one file per tag)
- Import one JSON file into **multiple** destination tags
- Tag filtering and quick selection
- Retry logic for transient PLC/network drops
- Light/Dark theme selector
- Built-in progress indicator

## Requirements
- Windows
- Python 3.13 (for running from source)
- PLC connection over Ethernet

## Run From Source
```powershell
pip install pycomm3
python seq_tester.py
