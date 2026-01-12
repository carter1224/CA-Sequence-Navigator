import json
import os
from pathlib import Path
import time
import tkinter as tk
from tkinter import ttk, messagebox, filedialog

from pycomm3 import LogixDriver


# Required data type for SEQ transfers.
REQUIRED_UDT_NAME = "SEQ"
REQUIRED_ARRAY_LEN = 100

DEFAULT_IP = "192.168.1.11"
DEFAULT_ETHERNET_SLOT = 1
DEFAULT_CONTROLLER_SLOT = 0


def safe_filename(name: str) -> str:
    """Make a Windows-safe filename from a Logix tag name."""
    bad = '<>:"/\\|?*'
    out = name
    for ch in bad:
        out = out.replace(ch, "_")
    out = out.replace("[", "_").replace("]", "_")
    out = out.strip().strip(".")
    return out or "tag"


def is_seq_100(tag_def: dict) -> bool:
    """
    Expected dimensions format:
      - SEQ[100] => [100, 0, 0]
      - scalar SEQ => [0, 0, 0]
    """
    if tag_def.get("data_type_name") != REQUIRED_UDT_NAME:
        return False
    dims = tag_def.get("dimensions") or [0, 0, 0]
    while len(dims) < 3:
        dims.append(0)
    return dims[0] == REQUIRED_ARRAY_LEN and dims[1] == 0 and dims[2] == 0


def ensure_snapshot_dict(value) -> dict:
    """For your SEQ elements, pycomm3 returns a dict snapshot (nested dicts/values)."""
    if value is None:
        raise ValueError("value is None")
    if not isinstance(value, dict):
        raise ValueError(f"unexpected element value type {type(value).__name__} (expected dict snapshot)")
    return value


class TagToolGUI(tk.Tk):
    """
    Export mode:
      - Load tags matching SEQ[100] (dims [100,0,0]).
      - Read base_tag[i] for i=0..99 and write one JSON file per base tag:
          value: [ {elem0}, {elem1}, ... {elem99} ]

    Import mode:
      - Load destination tags matching SEQ[100].
      - Choose a folder of JSON files.
      - Select one JSON file and one or more destination tags.
      - Import writes Tag[i] = value[i] for i=0..99.
    """

    def __init__(self):
        super().__init__()
        self.title("Sequence Transfer Utility")
        self._set_app_icon()
        self.geometry("1150x820")
        self.minsize(1000, 760)

        # Connection inputs.
        self.ip_var = tk.StringVar(value=DEFAULT_IP)
        self.eth_slot_var = tk.IntVar(value=DEFAULT_ETHERNET_SLOT)
        self.cpu_slot_var = tk.IntVar(value=DEFAULT_CONTROLLER_SLOT)

        # Mode.
        self.mode_var = tk.StringVar(value="export")  # export | import

        # Advanced settings.
        self.pretty_json_var = tk.BooleanVar(value=True)
        self.chunk_size_var = tk.IntVar(value=20)  # requests per batch
        self.include_program_tags_var = tk.BooleanVar(value=False)
        self.advanced_open = tk.BooleanVar(value=False)
        self.retry_count_var = tk.IntVar(value=3)
        self.retry_delay_ms_var = tk.IntVar(value=500)

        self.status_var = tk.StringVar(value="Ready.")
        self.search_var = tk.StringVar(value="")
        self.dest_search_var = tk.StringVar(value="")
        self.progress_var = tk.IntVar(value=0)

        # Export selection state.
        self.tag_vars: dict[str, tk.BooleanVar] = {}

        # Import selection state.
        self.import_folder_var = tk.StringVar(value="")
        self.selected_json_path_var = tk.StringVar(value="")
        self.dest_tag_vars: dict[str, tk.BooleanVar] = {}
        self.dest_tag_names: list[str] = []
        self.selected_json_display_var = tk.StringVar(value="None")
        self.selected_tags_display_var = tk.StringVar(value="None")
        self._scroll_regions = []
        self._scroll_bound = False

        self._build_ui()
        self._apply_styles()
        self.bind("<Configure>", lambda _e: self._position_advanced_overlay())
        self._apply_mode()

    # -------- connection helpers --------
    def plc_path(self) -> str:
        ip = self.ip_var.get().strip()
        eth = int(self.eth_slot_var.get())
        cpu = int(self.cpu_slot_var.get())
        return f"{ip}/{eth}/{cpu}"

    # ---------------- UI ----------------
    def _build_ui(self):
        pad = {"padx": 10, "pady": 6}

        top = ttk.Frame(self)
        top.pack(fill="x", **pad)

        ttk.Label(top, text="IP:").grid(row=0, column=0, sticky="w")
        ttk.Entry(top, textvariable=self.ip_var, width=18).grid(row=0, column=1, sticky="w", padx=(8, 16))

        ttk.Label(top, text="Ethernet Slot:").grid(row=0, column=2, sticky="w")
        ttk.Spinbox(top, from_=0, to=31, textvariable=self.eth_slot_var, width=4).grid(
            row=0, column=3, sticky="w", padx=(8, 16)
        )

        ttk.Label(top, text="Controller Slot:").grid(row=0, column=4, sticky="w")
        ttk.Spinbox(top, from_=0, to=31, textvariable=self.cpu_slot_var, width=4).grid(
            row=0, column=5, sticky="w", padx=(8, 16)
        )

        self.path_label = ttk.Label(top, text=f"Path: {self.plc_path()}")
        self.path_label.grid(row=0, column=6, sticky="w")

        def _refresh_path(*_):
            self.path_label.configure(text=f"Path: {self.plc_path()}")

        self.ip_var.trace_add("write", _refresh_path)
        self.eth_slot_var.trace_add("write", _refresh_path)
        self.cpu_slot_var.trace_add("write", _refresh_path)

        ttk.Label(top, text="Mode:").grid(row=1, column=0, sticky="w", pady=(8, 0))
        mode = ttk.Combobox(top, textvariable=self.mode_var, values=["export", "import"], state="readonly", width=10)
        mode.grid(row=1, column=1, sticky="w", pady=(8, 0))
        mode.bind("<<ComboboxSelected>>", lambda _e: self._apply_mode())

        btns = ttk.Frame(self)
        btns.pack(fill="x", **pad)

        ttk.Button(btns, text="Test Connection", command=self.test_connection).pack(side="left")

        ttk.Button(btns, text="Advanced Settings…", command=self.toggle_advanced).pack(side="right")

        self.progress_row = ttk.Frame(self)
        self.progress_row.pack(fill="x", padx=10, pady=(0, 6))
        self.status_label = ttk.Label(self.progress_row, textvariable=self.status_var)
        self.status_label.pack(fill="x")
        self.progress_bar = ttk.Progressbar(
            self.progress_row, variable=self.progress_var, maximum=100, style="App.Horizontal.TProgressbar"
        )
        self.progress_bar.pack(fill="x")


        # Advanced (collapsed)
        self.advanced_frame = ttk.LabelFrame(self, text="Advanced Settings")
        adv = ttk.Frame(self.advanced_frame)
        adv.pack(fill="x", padx=10, pady=8)

        ttk.Label(adv, text=f"Required tag definition: {REQUIRED_UDT_NAME} dims [{REQUIRED_ARRAY_LEN},0,0]").grid(
            row=0, column=0, sticky="w", padx=(0, 18)
        )
        ttk.Checkbutton(adv, text="Pretty JSON (Export)", variable=self.pretty_json_var).grid(
            row=0, column=1, sticky="w", padx=(0, 18)
        )
        ttk.Checkbutton(
            adv, text="Include Program Tags (Program:XYZ.*)", variable=self.include_program_tags_var
        ).grid(row=0, column=2, sticky="w")

        ttk.Label(adv, text="Chunk Size (requests per batch):").grid(row=1, column=0, sticky="w", pady=(8, 0))
        ttk.Spinbox(adv, from_=1, to=200, textvariable=self.chunk_size_var, width=6).grid(
            row=1, column=1, sticky="w", pady=(8, 0)
        )
        ttk.Label(adv, text="Lower = more reliable, higher = faster.").grid(
            row=1, column=2, sticky="w", padx=(18, 0), pady=(8, 0)
        )
        ttk.Label(adv, text="Retries:").grid(row=3, column=0, sticky="w", pady=(8, 0))
        ttk.Spinbox(adv, from_=1, to=10, textvariable=self.retry_count_var, width=6).grid(
            row=3, column=1, sticky="w", pady=(8, 0)
        )
        ttk.Label(adv, text="Retry Delay (ms):").grid(row=4, column=0, sticky="w", pady=(8, 0))
        ttk.Spinbox(adv, from_=0, to=5000, increment=100, textvariable=self.retry_delay_ms_var, width=6).grid(
            row=4, column=1, sticky="w", pady=(8, 0)
        )
        ttk.Label(adv, text="Sequence Transfer Utility by Carter Smith").grid(
            row=5, column=0, columnspan=3, sticky="w", pady=(10, 0)
        )
        ttk.Label(adv, text="Build 1.0.1").grid(
            row=6, column=0, columnspan=3, sticky="w", pady=(4, 0)
        )

        self.main = ttk.Frame(self)
        self.main.pack(fill="both", expand=True, padx=10, pady=10)

        # Export panel
        self.export_panel = ttk.Frame(self.main)
        self.export_panel.pack(fill="both", expand=True)

        self.export_action_row = ttk.Frame(self.export_panel)
        self.export_action_row.pack(fill="x", padx=10, pady=(10, 0))
        ttk.Button(self.export_action_row, text="Load Tags", command=self.load_tags).pack(side="left")
        self.export_btn = ttk.Button(
            self.export_action_row, text="Export Selected Tags", command=self.export_selected
        )
        self.export_btn.pack(side="left", padx=(10, 0))

        exp_top = ttk.Frame(self.export_panel)
        exp_top.pack(fill="x", padx=10, pady=(6, 0))
        ttk.Label(exp_top, text="Filter:").pack(side="left")
        exp_search = ttk.Entry(exp_top, textvariable=self.search_var, style="ExportFilter.TEntry")
        exp_search.pack(side="left", fill="x", expand=True, padx=(8, 0))
        exp_search.bind("<KeyRelease>", lambda _e: self.render_export_checkboxes())

        exp_menu_btn = ttk.Menubutton(exp_top, text="...", style="ExportMenu.TMenubutton")
        exp_menu = tk.Menu(exp_menu_btn, tearoff=0)
        exp_menu.add_command(label="Select All", command=self.select_all_export)
        exp_menu.add_command(label="Select None", command=self.select_none_export)
        exp_menu_btn.configure(menu=exp_menu)
        exp_menu_btn.pack(side="right")

        self.export_canvas = tk.Canvas(self.export_panel, highlightthickness=0)
        self.export_canvas.pack(side="left", fill="both", expand=True, padx=(10, 0), pady=10)
        exp_scroll = ttk.Scrollbar(self.export_panel, orient="vertical", command=self.export_canvas.yview)
        exp_scroll.pack(side="right", fill="y", padx=(0, 10), pady=10)
        self._setup_autohide_scrollbar(
            self.export_panel, exp_scroll, {"side": "right", "fill": "y", "padx": (0, 10), "pady": 10}
        )
        self.export_canvas.configure(yscrollcommand=exp_scroll.set)

        self.export_checkbox_frame = ttk.Frame(self.export_canvas)
        self.export_window = self.export_canvas.create_window((0, 0), window=self.export_checkbox_frame, anchor="nw")
        self.export_checkbox_frame.bind(
            "<Configure>", lambda _e: self.export_canvas.configure(scrollregion=self.export_canvas.bbox("all"))
        )
        self.export_canvas.bind(
            "<Configure>", lambda e: self.export_canvas.itemconfigure(self.export_window, width=e.width)
        )
        self._bind_canvas_scroll(self.export_panel, self.export_canvas, self.export_checkbox_frame)

        # Import panel.
        self.import_panel = ttk.Frame(self.main)

        imp_top = ttk.Frame(self.import_panel)
        imp_top.pack(fill="x", pady=(0, 8))
        imp_top.columnconfigure(0, weight=1, uniform="import_halves")
        imp_top.columnconfigure(2, weight=1, uniform="import_halves")

        left = ttk.LabelFrame(imp_top, text="JSON file — select ONE")
        left.grid(row=0, column=0, sticky="nsew", padx=(0, 8))

        ttk.Label(imp_top, text="→", font=("Segoe UI Symbol", 24, "bold")).grid(row=0, column=1, padx=10)

        right = ttk.LabelFrame(imp_top, text="Destination tag(s) (must be SEQ[100])")
        right.grid(row=0, column=2, sticky="nsew", padx=(8, 0))

        dest_top = ttk.Frame(right)
        dest_top.pack(fill="x", padx=10, pady=(10, 0))
        ttk.Button(dest_top, text="Load Tags", command=self.load_tags).pack(side="left")
        dest_search = ttk.Entry(dest_top, textvariable=self.dest_search_var, style="ExportFilter.TEntry")
        dest_search.pack(side="left", fill="x", expand=True, padx=(10, 0))
        dest_search.bind("<KeyRelease>", lambda _e: self._render_dest_checkboxes())
        dest_menu_btn = ttk.Menubutton(dest_top, text="...", style="ExportMenu.TMenubutton")
        dest_menu = tk.Menu(dest_menu_btn, tearoff=0)
        dest_menu.add_command(label="Select All", command=self._select_all_dest)
        dest_menu.add_command(label="Select None", command=self._select_none_dest)
        dest_menu_btn.configure(menu=dest_menu)
        dest_menu_btn.pack(side="right")

        # Destination tag list (checkbox multi-select)
        self.dest_canvas = tk.Canvas(right, highlightthickness=0)
        self.dest_canvas.pack(side="left", fill="both", expand=True, padx=(10, 0), pady=10)
        dest_scroll = ttk.Scrollbar(right, orient="vertical", command=self.dest_canvas.yview)
        dest_scroll.pack(side="right", fill="y", padx=(0, 10), pady=10)
        self._setup_autohide_scrollbar(
            right, dest_scroll, {"side": "right", "fill": "y", "padx": (0, 10), "pady": 10}
        )
        self.dest_canvas.configure(yscrollcommand=dest_scroll.set)

        self.dest_frame = ttk.Frame(self.dest_canvas)
        self.dest_window = self.dest_canvas.create_window((0, 0), window=self.dest_frame, anchor="nw")
        self.dest_frame.bind(
            "<Configure>", lambda _e: self.dest_canvas.configure(scrollregion=self.dest_canvas.bbox("all"))
        )
        self.dest_canvas.bind(
            "<Configure>", lambda e: self.dest_canvas.itemconfigure(self.dest_window, width=e.width)
        )
        self._bind_canvas_scroll(right, self.dest_canvas, self.dest_frame)

        # JSON files + choose folder
        files_top = ttk.Frame(left)
        files_top.pack(fill="x", padx=10, pady=(10, 0))
        ttk.Button(files_top, text="Choose Import Folder…", command=self.choose_import_folder).pack(side="left")
        ttk.Label(files_top, textvariable=self.import_folder_var).pack(side="left", padx=(10, 0))

        self.files_tree = ttk.Treeview(left, show="tree", selectmode="browse", style="Import.Treeview")
        self.files_tree.pack(side="left", fill="both", expand=True, padx=(10, 0), pady=10)
        files_scroll = ttk.Scrollbar(left, orient="vertical", command=self.files_tree.yview)
        files_scroll.pack(side="right", fill="y", padx=(0, 10), pady=10)
        self._setup_autohide_scrollbar(
            left, files_scroll, {"side": "right", "fill": "y", "padx": (0, 10), "pady": 10}
        )
        self.files_tree.configure(yscrollcommand=files_scroll.set)
        self.files_tree.bind("<<TreeviewSelect>>", self._on_file_select)

        self.import_action_row = ttk.Frame(self.import_panel)
        self.import_btn = ttk.Button(
            self.import_action_row, text="Import (selected tag + selected JSON)", command=self.import_one
        )
        self.import_btn.pack(side="left")
        self.import_action_row.pack_forget()

        # Import selections summary
        self.import_summary = ttk.Frame(self.import_panel)
        self.import_summary.pack(fill="x", pady=(0, 8))
        self.import_summary.columnconfigure(0, weight=1, uniform="import_summary")
        self.import_summary.columnconfigure(1, weight=1, uniform="import_summary")

        json_summary = ttk.LabelFrame(self.import_summary, text="Selected JSON")
        json_summary.grid(row=0, column=0, sticky="nsew", padx=(0, 8))
        ttk.Label(json_summary, textvariable=self.selected_json_display_var, wraplength=450).pack(
            fill="x", padx=10, pady=8
        )

        tags_summary = ttk.LabelFrame(self.import_summary, text="Selected Tags")
        tags_summary.grid(row=0, column=1, sticky="nsew", padx=(8, 0))
        ttk.Label(tags_summary, textvariable=self.selected_tags_display_var, wraplength=450, justify="left").pack(
            fill="x", padx=10, pady=8
        )

    # ---------------- Mode + status ----------------
    def _set_app_icon(self):
        try:
            base_dir = Path(__file__).resolve().parent
            ico_path = base_dir / "CA.ico"
            if ico_path.is_file():
                self.iconbitmap(str(ico_path))
            png_path = base_dir / "CA.png"
            if png_path.is_file():
                self._icon_image = tk.PhotoImage(file=str(png_path))
                self.iconphoto(True, self._icon_image)
        except Exception:
            pass

    def _apply_styles(self):
        style = ttk.Style(self)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        base_bg = self.cget("bg")
        style.configure("TFrame", background=base_bg)
        style.configure("TLabelframe", background=base_bg)
        style.configure("TLabelframe.Label", background=base_bg)
        style.configure("TLabel", background=base_bg)
        style.configure("ExportFilter.TEntry", padding=(6, 3))
        style.configure("ExportMenu.TMenubutton", padding=(6, 3))
        style.configure(
            "App.Horizontal.TProgressbar",
            troughcolor=base_bg,
            background="#2f7ee6",
        )
        style.configure(
            "Hidden.Horizontal.TProgressbar",
            troughcolor=base_bg,
            background=base_bg,
        )
        self.export_canvas.configure(bg=base_bg)
        self.dest_canvas.configure(bg=base_bg)

    def _setup_autohide_scrollbar(self, container: tk.Widget, scrollbar: ttk.Scrollbar, pack_opts: dict):
        scrollbar.pack_forget()
        visible = {"value": False}

        def _show():
            if not visible["value"]:
                scrollbar.pack(**pack_opts)
                visible["value"] = True

        def _hide():
            x, y = self.winfo_pointerxy()
            widget = self.winfo_containing(x, y)
            if widget is None:
                scrollbar.pack_forget()
                visible["value"] = False
                return
            if widget == scrollbar or self._is_descendant(widget, container):
                return
            scrollbar.pack_forget()
            visible["value"] = False

        container.bind("<Enter>", lambda _e: _show())
        container.bind("<Leave>", lambda _e: _hide())
        scrollbar.bind("<Enter>", lambda _e: _show())
        scrollbar.bind("<Leave>", lambda _e: _hide())

    def _is_descendant(self, widget, ancestor) -> bool:
        w = widget
        while w:
            if w == ancestor:
                return True
            w = w.master
        return False

    def _bind_canvas_scroll(
        self,
        container: tk.Widget,
        canvas: tk.Canvas,
        frame: ttk.Frame,
        units_per_notch: int = 3,
    ):
        self._scroll_regions.append((container, canvas, units_per_notch))

        if self._scroll_bound:
            return
        self._scroll_bound = True

        def _on_mousewheel(event):
            x, y = event.widget.winfo_pointerxy()
            widget = event.widget.winfo_containing(x, y)
            if widget is None:
                return
            for cont, canv, units in self._scroll_regions:
                if self._is_descendant(widget, cont):
                    if getattr(event, "num", None) == 4:
                        delta = -1
                    elif getattr(event, "num", None) == 5:
                        delta = 1
                    else:
                        if not event.delta:
                            return "break"
                        delta = -1 if event.delta > 0 else 1
                    canv.yview_scroll(delta * units, "units")
                    return "break"
            return

        self.bind_all("<MouseWheel>", _on_mousewheel, add="+")
        self.bind_all("<Button-4>", _on_mousewheel, add="+")
        self.bind_all("<Button-5>", _on_mousewheel, add="+")

    def set_status(self, msg: str):
        self.status_var.set(msg)
        self.update_idletasks()

    def _start_progress(self, total: int):
        self.progress_var.set(0)
        self.progress_bar.configure(maximum=max(1, int(total)), style="App.Horizontal.TProgressbar")
        self.progress_bar.update_idletasks()

    def _step_progress(self, amount: int = 1):
        new_value = self.progress_var.get() + amount
        try:
            max_value = int(self.progress_bar.cget("maximum"))
        except Exception:
            max_value = new_value
        if new_value > max_value:
            new_value = max_value
        self.progress_var.set(new_value)
        self.progress_bar.update_idletasks()

    def _end_progress(self):
        self.progress_var.set(0)
        self.progress_bar.configure(style="Hidden.Horizontal.TProgressbar")
        self.progress_bar.update_idletasks()

    def toggle_advanced(self):
        if self.advanced_open.get():
            self.advanced_frame.place_forget()
            self.advanced_open.set(False)
            self.set_status("Advanced Settings hidden.")
        else:
            self.advanced_open.set(True)
            self._position_advanced_overlay()
            self.set_status("Advanced Settings shown.")

    def _position_advanced_overlay(self):
        if not self.advanced_open.get():
            return
        self.update_idletasks()
        pad_x = 10
        pad_y = 8
        width = max(200, self.winfo_width() - (pad_x * 2))
        height = self.advanced_frame.winfo_reqheight()
        y = max(0, self.winfo_height() - height - pad_y)
        self.advanced_frame.place(x=pad_x, y=y, width=width)
        self.advanced_frame.lift()

    def _apply_mode(self):
        mode = self.mode_var.get().strip().lower()
        if mode == "export":
            self.import_panel.pack_forget()
            self.export_panel.pack(fill="both", expand=True)
            self.import_btn.pack_forget()
            self.import_action_row.pack_forget()
            if not self.export_btn.winfo_ismapped():
                self.export_btn.pack(side="left", padx=(10, 0))
            self.set_status("Export: Load tags → select checkboxes → Export.")
        else:
            self.export_panel.pack_forget()
            self.import_panel.pack(fill="both", expand=True)
            self.export_btn.pack_forget()
            if not self.import_action_row.winfo_ismapped():
                self.import_action_row.pack(fill="x", padx=10, pady=(0, 6), before=self.import_summary)
            if not self.import_btn.winfo_ismapped():
                self.import_btn.pack(side="left")
            self.set_status("Import: Load tags → choose folder → select ONE tag + ONE JSON → Import.")

    # ---------------- Connection / tag browsing ----------------
    def _run_with_retries(self, action_desc: str, func):
        attempts = max(1, int(self.retry_count_var.get()))
        delay_s = max(0, int(self.retry_delay_ms_var.get())) / 1000.0
        last_exc = None
        for attempt in range(1, attempts + 1):
            try:
                if attempt > 1:
                    self.set_status(f"{action_desc} (retry {attempt}/{attempts})...")
                return func()
            except Exception as e:
                last_exc = e
                if attempt < attempts and delay_s > 0:
                    time.sleep(delay_s)
        raise last_exc

    def _with_plc(self, action_desc: str, func):
        def _attempt():
            with LogixDriver(self.plc_path()) as plc:
                return func(plc)

        return self._run_with_retries(action_desc, _attempt)

    def test_connection(self):
        try:
            self.set_status("Testing connection...")
            info = self._with_plc("Testing connection", lambda plc: plc.get_plc_info())
            self.set_status(f"✅ Connected via {self.plc_path()}: {info.get('product_name', 'PLC')}")
        except Exception as e:
            messagebox.showerror("Connection Failed", f"Path used: {self.plc_path()}\n\n{e}")

    def _get_tag_list(self, plc: LogixDriver):
        if self.include_program_tags_var.get():
            return plc.get_tag_list(program="*")
        return plc.get_tag_list(program=None)

    def load_tags(self):
        self.set_status(f"Browsing tags on {self.plc_path()} ...")
        try:
            tags = self._with_plc("Browsing tags", lambda plc: self._get_tag_list(plc))

            matches = [t for t in tags if is_seq_100(t)]
            names = sorted([t.get("tag_name", "") for t in matches if t.get("tag_name")], key=str.lower)

            if self.mode_var.get().strip().lower() == "export":
                self._populate_export_tags(names)
                self.set_status(f"Loaded {len(names)} SEQ[100] tag(s) for export.")
            else:
                self._populate_dest_tags(names)
                self.set_status(f"Loaded {len(names)} SEQ[100] destination tag(s).")

            if not names:
                messagebox.showinfo(
                    "No Matches",
                    f"No tags found with {REQUIRED_UDT_NAME} dims [{REQUIRED_ARRAY_LEN},0,0].\n\n"
                    "If they are program-scoped, enable 'Include Program Tags' in Advanced Settings."
                )

        except Exception as e:
            messagebox.showerror("Load Tags Failed", f"Path used: {self.plc_path()}\n\n{e}")
            self.set_status("❌ Failed to load tags.")

    # ---------------- Export ----------------
    def select_all_export(self):
        for v in self.tag_vars.values():
            v.set(True)

    def select_none_export(self):
        for v in self.tag_vars.values():
            v.set(False)

    def _populate_export_tags(self, tag_names: list[str]):
        for w in self.export_checkbox_frame.winfo_children():
            w.destroy()
        self.tag_vars.clear()
        self.search_var.set("")
        for name in tag_names:
            self.tag_vars[name] = tk.BooleanVar(value=False)
        self.render_export_checkboxes()

    def render_export_checkboxes(self):
        filter_text = self.search_var.get().strip().lower()
        for w in self.export_checkbox_frame.winfo_children():
            w.destroy()
        names = sorted(self.tag_vars.keys(), key=str.lower)
        shown = 0
        for name in names:
            if filter_text and filter_text not in name.lower():
                continue
            ttk.Checkbutton(self.export_checkbox_frame, text=name, variable=self.tag_vars[name]).pack(
                anchor="w", fill="x", padx=4, pady=2
            )
            shown += 1
        selected = sum(1 for v in self.tag_vars.values() if v.get())
        self.set_status(f"Export list: {len(names)} tag(s). Showing {shown}. Selected {selected}.")

    def _read_seq100_elements(self, plc: LogixDriver, base_tag: str, chunk_size: int):
        elems = [None] * REQUIRED_ARRAY_LEN
        element_tags = [f"{base_tag}[{i}]" for i in range(REQUIRED_ARRAY_LEN)]

        for i in range(0, len(element_tags), chunk_size):
            chunk = element_tags[i:i + chunk_size]
            self.set_status(f"Reading {base_tag}: elements {i}..{i+len(chunk)-1}")
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
            raise RuntimeError(f"{base_tag}: missing elements at indices {missing[:10]}" + ("..." if len(missing) > 10 else ""))
        return elems

    def export_selected(self):
        selected_tags = [name for name, v in self.tag_vars.items() if v.get()]
        selected_tags.sort(key=str.lower)
        if not selected_tags:
            messagebox.showwarning("Nothing Selected", "Select one or more tags to export.")
            return

        out_dir = filedialog.askdirectory(title="Choose export folder")
        if not out_dir:
            return

        chunk_size = max(1, int(self.chunk_size_var.get()))
        pretty = bool(self.pretty_json_var.get())

        created = 0
        failures = []

        try:
            self._start_progress(len(selected_tags))
            for base in selected_tags:
                try:
                    def _read_one():
                        with LogixDriver(self.plc_path()) as plc:
                            return self._read_seq100_elements(plc, base, chunk_size)

                    values = self._run_with_retries(f"Reading {base}", _read_one)

                    base_name = safe_filename(base)
                    path = os.path.join(out_dir, base_name + ".json")
                    if os.path.exists(path):
                        n = 2
                        while True:
                            alt = os.path.join(out_dir, f"{base_name}__{n}.json")
                            if not os.path.exists(alt):
                                path = alt
                                break
                            n += 1

                    payload = {
                        "source_tag_name": base,
                        "required_definition": f"{REQUIRED_UDT_NAME} dims [{REQUIRED_ARRAY_LEN},0,0]",
                        "value": values,  # list of 100 dict snapshots
                    }
                    with open(path, "w", encoding="utf-8") as f:
                        json.dump(payload, f, indent=2 if pretty else None)

                    created += 1
                    self._step_progress(1)

                except Exception as e:
                    failures.append(f"{base}: {e}")
                    self._step_progress(1)

            msg = (
                f"Export complete.\n\nPath used:\n{self.plc_path()}\n\nFolder:\n{out_dir}\n\n"
                f"Created: {created}\nFailures: {len(failures)}"
            )
            if failures:
                msg += "\n\nFirst few failures:\n" + "\n".join(failures[:5])
            messagebox.showinfo("Export Completed", msg)
            self._end_progress()
            self.set_status(f"✅ Exported {created} file(s).")

        except Exception as e:
            messagebox.showerror("Export Failed", f"Path used: {self.plc_path()}\n\n{e}")
            self._end_progress()
            self.set_status("❌ Export failed.")

    # ---------------- Import ----------------
    def _on_file_select(self, _event=None):
        selection = self.files_tree.selection()
        self.selected_json_path_var.set(selection[0] if selection else "")
        self._update_selected_summary()

    def _populate_dest_tags(self, tag_names: list[str]):
        self.dest_tag_names = sorted(tag_names, key=str.lower)
        for w in self.dest_frame.winfo_children():
            w.destroy()
        self.dest_tag_vars.clear()
        self.dest_search_var.set("")
        for name in self.dest_tag_names:
            self.dest_tag_vars[name] = tk.BooleanVar(value=False)
        self._render_dest_checkboxes()

    def choose_import_folder(self):
        folder = filedialog.askdirectory(title="Choose folder containing exported JSON files")
        if not folder:
            return
        self.import_folder_var.set(folder)
        self._load_import_files(folder)

    def _load_import_files(self, folder: str):
        self.selected_json_path_var.set("")
        self._update_selected_summary()
        for item in self.files_tree.get_children():
            self.files_tree.delete(item)

        try:
            files = [
                os.path.join(folder, name)
                for name in os.listdir(folder)
                if name.lower().endswith(".json")
            ]
            files.sort(key=lambda p: os.path.basename(p).lower())

            for path in files:
                label = os.path.basename(path)
                self.files_tree.insert("", "end", iid=path, text=label)

            if files:
                self.set_status(f"Loaded {len(files)} JSON file(s). Select ONE and press Import.")
            else:
                self.set_status("No JSON files found in folder.")
                messagebox.showinfo("No JSON Files", "No .json files found in that folder.")

        except Exception as e:
            messagebox.showerror("Folder Load Failed", str(e))

    def _update_selected_summary(self):
        json_path = self.selected_json_path_var.get().strip()
        self.selected_json_display_var.set(os.path.basename(json_path) if json_path else "None")
        selected_tags = [name for name, var in self.dest_tag_vars.items() if var.get()]
        if selected_tags:
            self.selected_tags_display_var.set(", ".join(selected_tags))
        else:
            self.selected_tags_display_var.set("None")

    def _select_all_dest(self):
        for var in self.dest_tag_vars.values():
            var.set(True)
        self._update_selected_summary()
        self._render_dest_checkboxes()

    def _select_none_dest(self):
        for var in self.dest_tag_vars.values():
            var.set(False)
        self._update_selected_summary()
        self._render_dest_checkboxes()

    def _render_dest_checkboxes(self):
        filter_text = self.dest_search_var.get().strip().lower()
        for w in self.dest_frame.winfo_children():
            w.destroy()
        shown = 0
        for name in self.dest_tag_names:
            if filter_text and filter_text not in name.lower():
                continue
            ttk.Checkbutton(self.dest_frame, text=name, variable=self.dest_tag_vars[name],
                            command=self._update_selected_summary).pack(
                anchor="w", fill="x", padx=4, pady=2
            )
            shown += 1
        self.set_status(
            f"Import destinations: {len(self.dest_tag_names)} tag(s). Showing {shown}."
        )

    def _load_json_elements(self, path: str):
        with open(path, "r", encoding="utf-8") as f:
            payload = json.load(f)

        value = payload.get("value")
        if not isinstance(value, list) or len(value) != REQUIRED_ARRAY_LEN:
            raise ValueError(f"JSON 'value' must be a list of length {REQUIRED_ARRAY_LEN}")
        for i, elem in enumerate(value):
            if not isinstance(elem, dict):
                raise ValueError(f"JSON value[{i}] must be an object/dict")
        return value

    def import_one(self):
        json_path = self.selected_json_path_var.get().strip()

        if not json_path:
            messagebox.showwarning("No JSON Selected", "Select a JSON file first.")
            return
        selected_dests = [name for name, var in self.dest_tag_vars.items() if var.get()]
        if not selected_dests:
            messagebox.showwarning("No Destination Tag", "Select one or more destination tags first.")
            return

        try:
            elems = self._load_json_elements(json_path)
        except Exception as e:
            messagebox.showerror("Bad JSON", f"{os.path.basename(json_path)}\n\n{e}")
            return

        # Build element writes (per destination)
        total_writes = len(selected_dests) * REQUIRED_ARRAY_LEN
        chunk_size = max(1, int(self.chunk_size_var.get()))

        wrote = 0
        failed = []

        try:
            self._start_progress(total_writes)
            done = 0
            for dest_base in selected_dests:
                base_offset = done
                def _write_one():
                    local_wrote = 0
                    local_failed = []
                    local_done = 0
                    with LogixDriver(self.plc_path()) as plc:
                        writes = [(f"{dest_base}[{i}]", elems[i]) for i in range(REQUIRED_ARRAY_LEN)]
                        for i in range(0, len(writes), chunk_size):
                            chunk = writes[i:i + chunk_size]
                            self.set_status(
                                f"Import writing {base_offset+local_done+1}-{base_offset+local_done+len(chunk)} "
                                f"of {total_writes} element writes..."
                            )
                            results = plc.write(*chunk)
                            if not isinstance(results, list):
                                results = [results]
                            for r in results:
                                if getattr(r, "error", None):
                                    local_failed.append(f"{getattr(r, 'tag', '?')}: {r.error}")
                                else:
                                    local_wrote += 1
                            local_done += len(chunk)
                            self._step_progress(len(chunk))
                    return local_wrote, local_failed, local_done

                try:
                    local_wrote, local_failed, local_done = self._run_with_retries(f"Writing {dest_base}", _write_one)
                    wrote += local_wrote
                    failed.extend(local_failed)
                    done += local_done
                except Exception as e:
                    failed.append(f"{dest_base}: {e}")
                    done += REQUIRED_ARRAY_LEN
                    self._step_progress(REQUIRED_ARRAY_LEN)

            msg = (
                f"Import complete.\n\nPath used:\n{self.plc_path()}\n\n"
                f"JSON:\n{os.path.basename(json_path)}\n\n"
                f"Destinations:\n{len(selected_dests)} selected\n\n"
                f"Element writes ok: {wrote}\nElement write failures: {len(failed)}"
            )
            if failed:
                msg += "\n\nFirst few failures:\n" + "\n".join(failed[:10])
            messagebox.showinfo("Import Completed", msg)
            self._end_progress()
            self.set_status(f"Import done: {wrote} element(s) written.")

        except Exception as e:
            messagebox.showerror("Import Failed", f"Path used: {self.plc_path()}\n\n{e}")
            self._end_progress()
            self.set_status("Import failed.")


if __name__ == "__main__":
    TagToolGUI().mainloop()
