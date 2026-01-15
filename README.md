# Sequence Navigator

Internal Build 1.0.0 by Carter Smith

Sequence Navigator is a Windows utility for viewing, editing, and comparing SEQ[100] JSON sequence files. It can also upload/download sequences to a PLC via the included Python helpers.

## Highlights
- Open and browse SEQ ZIP files with per-step navigation.
- Edit boolean/integer values with an edit mode safeguard.
- Compare two ZIPs and generate a timestamped report.
- Upload from PLC and download to PLC (via `seq_exporter.py` and `seq_importer.py`).

## Settings
Settings are stored per-user at:
`%LocalAppData%\Sequence Navigator\settings.json`

## Release Package Contents
The release ZIP includes:
- `SequenceNavigator.exe`
- `SEQ_DataType.L5X`
- `seq_exporter.py`
- `seq_importer.py`
