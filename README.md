# Sequence Navigator

Internal Build 2.0.1 by Carter Smith

Sequence Navigator is an industrial Windows utility for Complete Automation. Mechanical engineers use it to view and back up the SEQ[100] sequences of the paint system, step by step, in the same card layout as the plant HMI. It can also edit values, compare backups, and upload from or download to a PLC through bundled helpers.

## Highlights
- Start screen with recent backups; open a backup by double-clicking it in Explorer ("Open with") or dropping it on the window.
- Step through a sequence, jump to step 1 or the sequence's end (its ES step), and keep your step when switching sequences.
- Find where any field is on across every sequence, by description or tag name (Ctrl+F).
- Upload from PLC: reads every sequence first, then asks where to save, so an interrupted upload never leaves a partial backup.
- Test the PLC connection before any transfer: shows the controller's name, model, firmware and keyswitch.
- Download to PLC with a confirmation that names the controller and lists the changes being written.
- Compare two backups, or a backup with what is on the PLC now, in a window that opens any difference in the cards.
- Edit Mode with a count of unsaved changes, and an info bar and status bar instead of pop-ups.

## Keyboard shortcuts
| Action | Keys |
| --- | --- |
| Open backup / Upload from PLC | Ctrl+O / Ctrl+U |
| Save changes / Toggle Edit Mode | Ctrl+S / Ctrl+E |
| Next / previous step | Page Down / Page Up |
| End of sequence (ES) / step 1 | Ctrl+Page Down / Ctrl+Page Up |
| Go to step / Find | Ctrl+G / Ctrl+F |
| Switch tab (C1 to Setpoints) | Ctrl+1 to Ctrl+7 |
| Compare | Ctrl+Shift+C |

## Settings
Settings, recent files and the optional debug log are stored per-user in:
`%LocalAppData%\Sequence Navigator\`

## Installation
1) Open the latest release on GitHub.
2) Download `Sequence_Navigator_v<version>_Windows_x64.zip`.
3) Extract the ZIP to a local folder.
4) Run `SequenceNavigator.exe`.

Requires 64-bit Windows 10 or 11. Everything else, including .NET 10 and Python, is bundled.

## Release Package Contents
The release ZIP includes:
- `SequenceNavigator.exe`
- `python/` (portable Python runtime with pycomm3)
- `SEQ_DataType.L5X`
- `seq_exporter.py` (upload)
- `seq_importer.py` (download)
- `plc_identity.py` (connection test; read-only)
