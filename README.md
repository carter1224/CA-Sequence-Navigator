# Sequence Navigator

Internal Build 1.3.0 by Carter Smith

Sequence Navigator is an industrial Windows utility for Complete Automation. It provides a focused GUI for engineers to review, edit, and compare SEQ[100] JSON sequence files used in sequence programming. It also supports uploading from PLC and downloading to PLC via bundled helpers.

## Highlights
- Open and browse SEQ ZIP files with per-step navigation.
- Edit boolean/integer values with an edit mode safeguard.
- Compare two ZIPs and generate a timestamped report.
- Upload from PLC and download to PLC (via `seq_exporter.py` and `seq_importer.py`).

## Settings
Settings are stored per-user at:
`%LocalAppData%\Sequence Navigator\settings.json`

## Installation
1) Open the latest release on GitHub.
2) Download `Sequence_Navigator_Windows_x64.zip`.
3) Extract the ZIP to a local folder.
4) Run `SequenceNavigator.exe`.

## Release Package Contents
The release ZIP includes:
- `SequenceNavigator.exe`
- `python/` (portable Python runtime with pycomm3)
- `SEQ_DataType.L5X`
- `seq_exporter.py`
- `seq_importer.py`
