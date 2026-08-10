import io
import json
import os
import socket
import sys
import threading
import tkinter as tk
import urllib.request
import webbrowser

from PIL import Image, ImageDraw, ImageTk

import qrcode

try:
    import pystray
    HAS_TRAY = True
except ImportError:
    HAS_TRAY = False

BG_PRIMARY = "#1a1a2e"
BG_SECONDARY = "#16213e"
BG_TERTIARY = "#0f3460"
ACCENT_START = "#667eea"
ACCENT_END = "#764ba2"
TEXT_PRIMARY = "#ffffff"
TEXT_SECONDARY = "rgba(255,255,255,0.6)"
TEXT_MUTED = "rgba(255,255,255,0.35)"
CARD_BG = "rgba(255,255,255,0.06)"
CARD_BORDER = "rgba(255,255,255,0.1)"
TEXT_HEX_SECONDARY = "#a0a0b8"
TEXT_HEX_MUTED = "#6a6a7a"
SUCCESS_GREEN = "#4ade80"
BTN_HOVER = "rgba(255,255,255,0.12)"
CARD_RADIUS = 12


def hex_to_rgb(hex_color):
    hex_color = hex_color.lstrip("#")
    return tuple(int(hex_color[i : i + 2], 16) for i in (0, 2, 4))


def rgb_to_hex(r, g, b):
    return f"#{r:02x}{g:02x}{b:02x}"


def blend_colors(c1, c2, ratio):
    r1, g1, b1 = hex_to_rgb(c1)
    r2, g2, b2 = hex_to_rgb(c2)
    r = int(r1 + (r2 - r1) * ratio)
    g = int(g1 + (g2 - g1) * ratio)
    b = int(b1 + (b2 - b1) * ratio)
    return rgb_to_hex(r, g, b)


if sys.platform == "win32":
    try:
        from ctypes import windll

        windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        pass


class SMTCGui:
    def __init__(self, flask_host="127.0.0.1", flask_port=8888, local_ip=None, start_flask=None):
        self.flask_host = flask_host
        self.flask_port = flask_port
        self.local_ip = local_ip or self._get_local_ip()
        self.server_url = f"http://{self.flask_host}:{self.flask_port}"
        self.lan_url = f"http://{self.local_ip}:{self.flask_port}"
        self._start_flask_fn = start_flask

        self.root = None
        self.tray_icon = None
        self.tray_thread = None
        self.running = True
        self.minimized_to_tray = False
        self._server_running = False
        self._suppress_volume_cb = False
        self._volume_initialized = False
        self._debug_window = None
        self._debug_text = None

        self.current_status = {
            "title": "未检测到媒体播放",
            "artist": "",
            "is_playing": False,
            "position": 0,
            "duration": 0,
            "volume": 0,
            "muted": False,
            "thumbnail": "",
        }

    def _get_local_ip(self):
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect(("8.8.8.8", 80))
            ip = s.getsockname()[0]
            s.close()
            return ip
        except Exception:
            return "127.0.0.1"

    def _generate_qr(self, url, size=180):
        qr = qrcode.QRCode(
            version=1,
            error_correction=qrcode.constants.ERROR_CORRECT_L,
            box_size=10,
            border=0,
        )
        qr.add_data(url)
        qr.make(fit=True)
        img = qr.make_image(fill_color="#ffffff", back_color=BG_PRIMARY)
        img = img.convert("RGBA")
        img = img.resize((size, size), Image.LANCZOS)
        return ImageTk.PhotoImage(img)

    def _create_tray_icon_image(self, size=64):
        img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        draw = ImageDraw.Draw(img)
        margin = size // 8
        r = size // 2 - margin

        cx, cy = size // 2, size // 2
        for i in range(r * 2, 0, -1):
            ratio = i / (r * 2)
            color = (
                int(102 + (118 - 102) * (1 - ratio)),
                int(126 + (75 - 126) * (1 - ratio)),
                int(234 + (162 - 234) * (1 - ratio)),
            )
            draw.ellipse(
                [cx - i, cy - i, cx + i, cy + i],
                fill=color,
            )

        inner_r = r // 3
        draw.ellipse(
            [cx - inner_r, cy - inner_r, cx + inner_r, cy + inner_r],
            fill=(255, 255, 255, 230),
        )

        line_w = max(2, size // 16)
        line_h = r
        draw.rectangle(
            [cx - line_w // 2, margin + 4, cx + line_w // 2, margin + 4 + line_h],
            fill=(255, 255, 255, 230),
        )

        return img

    def _fetch_status(self):
        try:
            req = urllib.request.Request(f"{self.server_url}/api/status")
            with urllib.request.urlopen(req, timeout=2) as resp:
                return json.loads(resp.read().decode())
        except Exception:
            return None

    def _control(self, action):
        try:
            req = urllib.request.Request(
                f"{self.server_url}/api/{action}",
                method="POST",
                headers={"Content-Type": "application/json"},
            )
            urllib.request.urlopen(req, timeout=2)
        except Exception:
            pass

    def _set_volume(self, volume):
        try:
            data = json.dumps({"volume": volume}).encode()
            req = urllib.request.Request(
                f"{self.server_url}/api/volume",
                data=data,
                method="POST",
                headers={"Content-Type": "application/json"},
            )
            urllib.request.urlopen(req, timeout=2)
        except Exception:
            pass

    def _toggle_mute(self):
        try:
            req = urllib.request.Request(
                f"{self.server_url}/api/volume/toggle_mute",
                method="POST",
                headers={"Content-Type": "application/json"},
            )
            urllib.request.urlopen(req, timeout=2)
        except Exception:
            pass

    def _format_time(self, seconds):
        if not seconds or seconds < 0:
            return "0:00"
        m = int(seconds // 60)
        s = int(seconds % 60)
        return f"{m}:{s:02d}"

    def _draw_rounded_rect(self, canvas, x1, y1, x2, y2, radius, **kwargs):
        points = [
            x1 + radius, y1,
            x2 - radius, y1,
            x2, y1,
            x2, y1 + radius,
            x2, y2 - radius,
            x2, y2,
            x2 - radius, y2,
            x1 + radius, y2,
            x1, y2,
            x1, y2 - radius,
            x1, y1 + radius,
            x1, y1,
        ]
        return canvas.create_polygon(points, smooth=True, **kwargs)

    def _draw_gradient_rect(self, canvas, x1, y1, x2, y2):
        steps = 50
        height = y2 - y1
        for i in range(steps):
            y_top = y1 + int(height * i / steps)
            y_bot = y1 + int(height * (i + 1) / steps)
            color = blend_colors(ACCENT_START, ACCENT_END, i / steps)
            canvas.create_rectangle(x1, y_top, x2, y_bot, fill=color, outline="")

    def _create_button(self, canvas, x, y, size, text, color_start, color_end, command, tag):
        self._draw_rounded_rect(canvas, x, y, x + size, y + size, size // 2, fill="", outline="", tags=tag)
        steps = 50
        for i in range(steps):
            y_top = y + int(size * i / steps)
            y_bot = y + int(size * (i + 1) / steps)
            color = blend_colors(color_start, color_end, i / steps)
            canvas.create_arc(
                x, y_top, x + size, y_bot + size // 2,
                start=90, extent=180, fill=color, outline=color, tags=tag,
            )
            canvas.create_arc(
                x, y_top - size // 2, x + size, y_bot,
                start=270, extent=180, fill=color, outline=color, tags=tag,
            )
        canvas.create_text(
            x + size // 2, y + size // 2,
            text=text, fill=TEXT_PRIMARY, font=("Segoe UI Symbol", size // 4), tags=tag,
        )
        canvas.tag_bind(tag, "<Button-1>", lambda e: command())
        canvas.tag_bind(tag, "<Enter>",
            lambda e: canvas.configure(cursor="hand2"))
        canvas.tag_bind(tag, "<Leave>",
            lambda e: canvas.configure(cursor=""))

    def _start_server(self):
        port_str = self.port_var.get().strip()
        try:
            new_port = int(port_str)
            if new_port < 1 or new_port > 65535:
                self.start_status_label.configure(text="端口范围 1-65535")
                return
        except ValueError:
            self.start_status_label.configure(text="请输入有效端口号")
            return

        self.flask_port = new_port
        self.server_url = f"http://{self.flask_host}:{self.flask_port}"
        self.lan_url = f"http://{self.local_ip}:{self.flask_port}"

        self.start_status_label.configure(text="正在启动服务...")
        self.root.update()

        if self._start_flask_fn:
            self._start_flask_fn(self.flask_port)

        self._server_running = True
        self.start_frame.place_forget()
        self.main_frame.pack(fill="both", expand=True)

        qr_img = self._generate_qr(self.lan_url, 130)
        self.qr_label.configure(image=qr_img)
        self.qr_label.image = qr_img
        self.lan_url_label.configure(text=self.lan_url)
        self.port_label.configure(text=f"端口: {self.flask_port}")

    def _build_ui(self):
        self.root = tk.Tk()
        self.root.title("SMTC Player")
        self.root.geometry("440x680")
        self.root.configure(bg=BG_PRIMARY)
        self.root.resizable(True, True)
        self.root.minsize(380, 600)

        try:
            icon_img = self._create_tray_icon_image(64)
            icon_tk = ImageTk.PhotoImage(icon_img)
            self.root.iconphoto(True, icon_tk)
            self._icon_tk_ref = icon_tk
        except Exception:
            pass

        self.root.protocol("WM_DELETE_WINDOW", self._on_close)

        self.start_frame = tk.Frame(self.root, bg=BG_PRIMARY)
        self.start_frame.place(relx=0, rely=0, relwidth=1, relheight=1)

        spacer_top = tk.Frame(self.start_frame, bg=BG_PRIMARY, height=80)
        spacer_top.pack()

        try:
            logo_img = self._create_tray_icon_image(120)
            logo_tk = ImageTk.PhotoImage(logo_img)
            logo_label = tk.Label(self.start_frame, image=logo_tk, bg=BG_PRIMARY)
            logo_label.image = logo_tk
            logo_label.pack(pady=(0, 20))
        except Exception:
            tk.Label(self.start_frame, text="\u266b", font=("Segoe UI Symbol", 64),
                     fg="#667eea", bg=BG_PRIMARY).pack(pady=(0, 20))

        tk.Label(
            self.start_frame, text="SMTC Player",
            font=("Segoe UI", 26, "bold"), fg=TEXT_PRIMARY, bg=BG_PRIMARY,
        ).pack()
        tk.Label(
            self.start_frame, text="远程遥控电脑播放",
            font=("Segoe UI", 12), fg=TEXT_HEX_SECONDARY, bg=BG_PRIMARY,
        ).pack(pady=(4, 30))

        self.port_entry_frame = tk.Frame(self.start_frame, bg=BG_PRIMARY)
        self.port_entry_frame.pack(pady=(0, 10))
        tk.Label(
            self.port_entry_frame, text="端口:", font=("Segoe UI", 11),
            fg=TEXT_HEX_SECONDARY, bg=BG_PRIMARY,
        ).pack(side="left", padx=(0, 8))
        self.port_var = tk.StringVar(value=str(self.flask_port))
        self.port_entry = tk.Entry(
            self.port_entry_frame, textvariable=self.port_var,
            font=("Segoe UI", 11), width=6, justify="center",
            bg="#2a2a4a", fg=TEXT_PRIMARY, bd=0,
            insertbackground=TEXT_PRIMARY,
        )
        self.port_entry.pack(side="left")

        start_btn = tk.Button(
            self.start_frame, text="启动服务",
            font=("Segoe UI", 15, "bold"),
            fg=TEXT_PRIMARY, bg="#667eea",
            activebackground="#5a6fd8", activeforeground=TEXT_PRIMARY,
            bd=0, padx=40, pady=12, cursor="hand2",
            command=self._start_server,
        )
        start_btn.pack(pady=(10, 20))

        self.start_status_label = tk.Label(
            self.start_frame, text="",
            font=("Segoe UI", 10), fg=TEXT_HEX_MUTED, bg=BG_PRIMARY,
        )
        self.start_status_label.pack()

        self.main_frame = tk.Frame(self.root, bg=BG_PRIMARY)

        main_content = tk.Frame(self.main_frame, bg=BG_PRIMARY)
        main_content.pack(fill="both", expand=True, padx=20, pady=16)

        header_frame = tk.Frame(main_content, bg=BG_PRIMARY)
        header_frame.pack(fill="x", pady=(0, 8))
        tk.Label(
            header_frame,
            text="SMTC Player",
            font=("Segoe UI", 22, "bold"),
            fg=TEXT_PRIMARY,
            bg=BG_PRIMARY,
        ).pack(side="left")
        version_label = tk.Label(
            header_frame,
            text="v1.1.0",
            font=("Segoe UI", 10),
            fg="#667eea",
            bg=BG_PRIMARY,
        )
        version_label.pack(side="left", padx=(6, 0), pady=(8, 0))

        qr_card = tk.Frame(main_content, bg="#1e1e3a", highlightthickness=1, highlightbackground="#2a2a4a")
        qr_card.pack(fill="x", pady=(0, 12))

        qr_card_inner = tk.Frame(qr_card, bg="#1e1e3a")
        qr_card_inner.pack(padx=16, pady=14, fill="x")

        qr_container = tk.Frame(qr_card_inner, bg=BG_PRIMARY, width=140, height=140)
        qr_container.pack(side="left", padx=(0, 16))
        qr_container.pack_propagate(False)

        self.qr_label = tk.Label(qr_container, bg=BG_PRIMARY)
        self.qr_label.place(relx=0.5, rely=0.5, anchor="center")
        qr_img = self._generate_qr(self.lan_url, 130)
        self.qr_label.configure(image=qr_img)
        self.qr_label.image = qr_img

        info_col = tk.Frame(qr_card_inner, bg="#1e1e3a")
        info_col.pack(side="left", fill="both", expand=True)

        tk.Label(
            info_col,
            text="手机扫码控制",
            font=("Segoe UI", 13, "bold"),
            fg=TEXT_PRIMARY,
            bg="#1e1e3a",
        ).pack(anchor="w")

        self.lan_url_label = tk.Label(
            info_col,
            text=self.lan_url,
            font=("Segoe UI", 10),
            fg=TEXT_HEX_SECONDARY,
            bg="#1e1e3a",
            cursor="hand2",
        )
        self.lan_url_label.pack(anchor="w", pady=(4, 8))
        self.lan_url_label.bind("<Button-1>", lambda e: self._copy_lan_url())

        btn_row = tk.Frame(info_col, bg="#1e1e3a")
        btn_row.pack(anchor="w")

        copy_btn = tk.Button(
            btn_row,
            text="复制链接",
            font=("Segoe UI", 9),
            fg=TEXT_PRIMARY,
            bg="#2a2a4a",
            activebackground="#3a3a5a",
            activeforeground=TEXT_PRIMARY,
            bd=0,
            padx=10,
            pady=4,
            cursor="hand2",
            command=self._copy_lan_url,
        )
        copy_btn.pack(side="left", padx=(0, 8))

        open_btn = tk.Button(
            btn_row,
            text="浏览器打开",
            font=("Segoe UI", 9),
            fg=TEXT_PRIMARY,
            bg="#667eea",
            activebackground="#5a6fd8",
            activeforeground=TEXT_PRIMARY,
            bd=0,
            padx=10,
            pady=4,
            cursor="hand2",
            command=lambda: webbrowser.open(self.server_url),
        )
        open_btn.pack(side="left")

        status_bar = tk.Frame(main_content, bg="#1e1e3a", highlightthickness=1, highlightbackground="#2a2a4a")
        status_bar.pack(fill="x", pady=(0, 12))

        status_inner = tk.Frame(status_bar, bg="#1e1e3a")
        status_inner.pack(padx=14, pady=10, fill="x")

        self.status_dot = tk.Canvas(status_inner, width=8, height=8, bg="#1e1e3a", highlightthickness=0)
        self.status_dot.pack(side="left", padx=(0, 6))
        self._status_dot_id = self.status_dot.create_oval(0, 0, 8, 8, fill=SUCCESS_GREEN, outline="")

        self.status_text = tk.Label(
            status_inner,
            text="服务器运行中",
            font=("Segoe UI", 10),
            fg=TEXT_HEX_SECONDARY,
            bg="#1e1e3a",
        )
        self.status_text.pack(side="left")

        self.port_label = tk.Label(
            status_inner,
            text=f"端口: {self.flask_port}",
            font=("Segoe UI", 10),
            fg=TEXT_HEX_MUTED,
            bg="#1e1e3a",
        )
        self.port_label.pack(side="right")

        nowplaying_card = tk.Frame(main_content, bg="#1e1e3a", highlightthickness=1, highlightbackground="#2a2a4a")
        nowplaying_card.pack(fill="x", pady=(0, 12))

        np_inner = tk.Frame(nowplaying_card, bg="#1e1e3a")
        np_inner.pack(padx=16, pady=14, fill="x")

        self.album_frame = tk.Frame(np_inner, bg="#2a2a5a", width=100, height=100)
        self.album_frame.pack(side="left", padx=(0, 14))
        self.album_frame.pack_propagate(False)

        self.album_art = tk.Label(
            self.album_frame,
            text="\u266b",
            font=("Segoe UI Symbol", 36),
            fg="#667eea",
            bg="#2a2a5a",
        )
        self.album_art.place(relx=0.5, rely=0.5, anchor="center")

        song_col = tk.Frame(np_inner, bg="#1e1e3a")
        song_col.pack(side="left", fill="both", expand=True)

        self.song_title_label = tk.Label(
            song_col,
            text="未检测到媒体播放",
            font=("Segoe UI", 14, "bold"),
            fg=TEXT_PRIMARY,
            bg="#1e1e3a",
            anchor="w",
        )
        self.song_title_label.pack(fill="x")

        self.song_artist_label = tk.Label(
            song_col,
            text="等待音频播放...",
            font=("Segoe UI", 11),
            fg=TEXT_HEX_SECONDARY,
            bg="#1e1e3a",
            anchor="w",
        )
        self.song_artist_label.pack(fill="x", pady=(4, 0))

        progress_frame = tk.Frame(main_content, bg=BG_PRIMARY)
        progress_frame.pack(fill="x", pady=(0, 12))

        self.progress_canvas = tk.Canvas(
            progress_frame, height=6, bg=BG_PRIMARY, highlightthickness=0,
        )
        self.progress_canvas.pack(fill="x")
        self.progress_canvas.bind("<Configure>", self._redraw_progress)

        time_frame = tk.Frame(main_content, bg=BG_PRIMARY)
        time_frame.pack(fill="x", pady=(0, 16))

        self.current_time_label = tk.Label(
            time_frame, text="0:00", font=("Segoe UI", 9),
            fg=TEXT_HEX_MUTED, bg=BG_PRIMARY,
        )
        self.current_time_label.pack(side="left")

        self.total_time_label = tk.Label(
            time_frame, text="0:00", font=("Segoe UI", 9),
            fg=TEXT_HEX_MUTED, bg=BG_PRIMARY,
        )
        self.total_time_label.pack(side="right")

        controls_frame = tk.Frame(main_content, bg=BG_PRIMARY)
        controls_frame.pack(fill="x", pady=(0, 14))

        controls_inner = tk.Frame(controls_frame, bg=BG_PRIMARY)
        controls_inner.pack()

        btn_style = {
            "font": ("Segoe UI Symbol", 16),
            "bd": 0,
            "cursor": "hand2",
            "width": 3,
            "height": 1,
        }

        self.prev_btn = tk.Button(
            controls_inner, text="\u23ee", fg=TEXT_PRIMARY, bg="#2a2a4a",
            activebackground="#3a3a5a", activeforeground=TEXT_PRIMARY,
            command=lambda: self._control("previous"), **btn_style,
        )
        self.prev_btn.pack(side="left", padx=12)

        self.play_btn_frame = tk.Frame(controls_inner, bg="#667eea", width=64, height=64)
        self.play_btn_frame.pack(side="left", padx=12)
        self.play_btn_frame.pack_propagate(False)

        self.play_btn = tk.Button(
            self.play_btn_frame, text="\u25b6", font=("Segoe UI Symbol", 20),
            fg=TEXT_PRIMARY, bg="#667eea", activebackground="#5a6fd8",
            activeforeground=TEXT_PRIMARY, bd=0, cursor="hand2",
            command=lambda: self._control("play_pause"),
        )
        self.play_btn.place(relx=0.5, rely=0.5, anchor="center", width=64, height=64)

        self.next_btn = tk.Button(
            controls_inner, text="\u23ed", fg=TEXT_PRIMARY, bg="#2a2a4a",
            activebackground="#3a3a5a", activeforeground=TEXT_PRIMARY,
            command=lambda: self._control("next"), **btn_style,
        )
        self.next_btn.pack(side="left", padx=12)

        volume_frame = tk.Frame(main_content, bg=BG_PRIMARY)
        volume_frame.pack(fill="x", pady=(0, 12))

        self.volume_icon_label = tk.Label(
            volume_frame, text="\U0001f50a", font=("Segoe UI Symbol", 14),
            fg=TEXT_PRIMARY, bg=BG_PRIMARY, cursor="hand2",
        )
        self.volume_icon_label.pack(side="left", padx=(0, 8))
        self.volume_icon_label.bind("<Button-1>", lambda e: self._toggle_mute())

        self.volume_slider = tk.Scale(
            volume_frame, from_=0, to=100, orient="horizontal",
            bg=BG_PRIMARY, fg=TEXT_PRIMARY, highlightbackground=BG_PRIMARY,
            troughcolor="#2a2a4a", activebackground="#667eea",
            bd=0, sliderlength=16, length=200,
            command=self._on_volume_change,
        )
        self.volume_slider.set(50)
        self.volume_slider.pack(side="left", fill="x", expand=True)

        self.volume_pct_label = tk.Label(
            volume_frame, text="50%", font=("Segoe UI", 10),
            fg=TEXT_HEX_SECONDARY, bg=BG_PRIMARY, width=4,
        )
        self.volume_pct_label.pack(side="right", padx=(8, 0))

        bottom_frame = tk.Frame(main_content, bg=BG_PRIMARY)
        bottom_frame.pack(fill="x", pady=(4, 0))

        tray_btn = tk.Button(
            bottom_frame, text="隐藏到托盘",
            font=("Segoe UI", 9),
            fg=TEXT_HEX_SECONDARY, bg="#2a2a4a",
            activebackground="#3a3a5a", activeforeground=TEXT_PRIMARY,
            bd=0, padx=12, pady=6, cursor="hand2",
            command=self._minimize_to_tray,
        )
        tray_btn.pack(side="left", padx=(0, 4))

        pin_btn = tk.Button(
            bottom_frame, text="更改 PIN",
            font=("Segoe UI", 9),
            fg=TEXT_HEX_SECONDARY, bg="#2a2a4a",
            activebackground="#3a3a5a", activeforeground=TEXT_PRIMARY,
            bd=0, padx=12, pady=6, cursor="hand2",
            command=self._show_change_pin_dialog,
        )
        pin_btn.pack(side="left", padx=4)

        debug_btn = tk.Button(
            bottom_frame, text="调试窗口",
            font=("Segoe UI", 9),
            fg=TEXT_HEX_SECONDARY, bg="#2a2a4a",
            activebackground="#3a3a5a", activeforeground=TEXT_PRIMARY,
            bd=0, padx=12, pady=6, cursor="hand2",
            command=self._show_debug_window,
        )
        debug_btn.pack(side="left", padx=4)

        quit_btn = tk.Button(
            bottom_frame, text="退出",
            font=("Segoe UI", 9),
            fg="#e74c3c", bg="#2a2a4a",
            activebackground="#3a3a5a", activeforeground="#e74c3c",
            bd=0, padx=12, pady=6, cursor="hand2",
            command=self._show_quit_dialog,
        )
        quit_btn.pack(side="right")

        self._prev_btn = self.prev_btn
        self._next_btn = self.next_btn

        self._progress_bg_id = None
        self._progress_fill_id = None

    def _redraw_progress(self, event=None):
        canvas = self.progress_canvas
        canvas.delete("progress")
        w = canvas.winfo_width()
        h = canvas.winfo_height()
        if w < 2 or h < 2:
            return
        canvas.create_rectangle(0, 0, w, h, fill="#2a2a4a", outline="", tags="progress_bg")
        if self.current_status.get("duration", 0) > 0:
            ratio = self.current_status["position"] / self.current_status["duration"]
        else:
            ratio = 0
        fill_w = int(w * ratio)
        steps = 30
        for i in range(steps):
            x1 = int(fill_w * i / steps)
            x2 = int(fill_w * (i + 1) / steps)
            color = blend_colors(ACCENT_START, ACCENT_END, i / steps)
            canvas.create_rectangle(x1, 0, x2, h, fill=color, outline="", tags="progress_fill")

    def _copy_lan_url(self):
        self.root.clipboard_clear()
        self.root.clipboard_append(self.lan_url)
        self.lan_url_label.configure(text="已复制到剪贴板!")
        self.root.after(2000, lambda: self.lan_url_label.configure(text=self.lan_url))

    def _on_volume_change(self, val):
        if self._suppress_volume_cb:
            return
        vol = int(float(val))
        self.volume_pct_label.configure(text=f"{vol}%")
        if vol == 0:
            self.volume_icon_label.configure(text="\U0001f507")
        elif vol < 50:
            self.volume_icon_label.configure(text="\U0001f509")
        else:
            self.volume_icon_label.configure(text="\U0001f50a")
        self._set_volume(vol)

    def _update_ui(self):
        if not self.root:
            return
        if not self._server_running:
            if self.running and self.root:
                self.root.after(500, self._update_ui)
            return
        status = self._fetch_status()
        if status:
            self.current_status = status
            self.song_title_label.configure(text=status.get("title", "未知"))
            self.song_artist_label.configure(text=status.get("artist", ""))
            self.play_btn.configure(
                text="\u23f8" if status.get("is_playing") else "\u25b6",
            )
            self._prev_btn.configure(
                state="normal" if status.get("has_previous") else "disabled",
            )
            self._next_btn.configure(
                state="normal" if status.get("has_next") else "disabled",
            )
            dur = status.get("duration", 0)
            pos = status.get("position", 0)
            self.current_time_label.configure(text=self._format_time(pos))
            self.total_time_label.configure(text=self._format_time(dur))
            self._redraw_progress()

            if not self._volume_initialized:
                self._volume_initialized = True
                vol = int(status.get("volume", 0))
                muted = status.get("muted", False)
                self._suppress_volume_cb = True
                self.volume_slider.set(vol)
                self._suppress_volume_cb = False
                self.volume_pct_label.configure(text=f"{vol}%")
                if muted or vol == 0:
                    self.volume_icon_label.configure(text="\U0001f507")
                elif vol < 50:
                    self.volume_icon_label.configure(text="\U0001f509")
                else:
                    self.volume_icon_label.configure(text="\U0001f50a")

            thumbnail = status.get("thumbnail", "")
            if thumbnail:
                try:
                    req = urllib.request.Request(thumbnail, headers={"User-Agent": "Mozilla/5.0"})
                    with urllib.request.urlopen(req, timeout=5) as resp:
                        data = resp.read()
                    img = Image.open(io.BytesIO(data))
                    img = img.resize((100, 100), Image.LANCZOS).convert("RGBA")

                    mask = Image.new("L", (100, 100), 0)
                    mask_draw = ImageDraw.Draw(mask)
                    mask_draw.ellipse([0, 0, 99, 99], fill=255)
                    img.putalpha(mask)

                    self._album_tk = ImageTk.PhotoImage(img)
                    self.album_art.configure(image=self._album_tk, text="")
                except Exception:
                    pass
        else:
            self.status_dot.itemconfig(self._status_dot_id, fill="#e74c3c")
            self.status_text.configure(text="连接服务器失败")
            self.root.after(2000, self._reset_status_dot)

        if self.running and self.root:
            self.root.after(1000, self._update_ui)

    def _reset_status_dot(self):
        self.status_dot.itemconfig(self._status_dot_id, fill=SUCCESS_GREEN)
        self.status_text.configure(text="服务器运行中")

    def _setup_tray(self):
        if not HAS_TRAY:
            return
        icon_img = self._create_tray_icon_image(64)

        def on_open(icon, item):
            self._show_window()

        def on_play_pause(icon, item):
            self._control("play_pause")

        def on_next(icon, item):
            self._control("next")

        def on_quit(icon, item):
            icon.stop()
            self._quit_app()

        menu = pystray.Menu(
            pystray.MenuItem("显示窗口", on_open, default=True),
            pystray.MenuItem("播放/暂停", on_play_pause),
            pystray.MenuItem("下一首", on_next),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("退出", on_quit),
        )

        self.tray_icon = pystray.Icon("smtc_player", icon_img, "SMTC Player", menu)

    def _run_tray(self):
        if self.tray_icon:
            self.tray_icon.run()

    def _minimize_to_tray(self):
        if not HAS_TRAY:
            self.root.iconify()
            return
        self.root.withdraw()
        self.minimized_to_tray = True
        if not self.tray_thread:
            self._setup_tray()
            self.tray_thread = threading.Thread(target=self._run_tray, daemon=True)
            self.tray_thread.start()
        elif self.tray_icon and not self.tray_icon.visible:
            self.tray_thread = threading.Thread(target=self._run_tray, daemon=True)
            self.tray_thread.start()

    def _show_window(self):
        self.root.deiconify()
        self.root.lift()
        self.root.focus_force()
        self.minimized_to_tray = False

    def _on_close(self):
        self._show_quit_dialog()

    def _show_quit_dialog(self):
        dialog = tk.Toplevel(self.root)
        dialog.title("SMTC Player")
        dialog.configure(bg=BG_PRIMARY)
        dialog.resizable(False, False)
        dialog.transient(self.root)
        dialog.grab_set()

        dw = 320
        dh = 200
        rx = self.root.winfo_rootx()
        ry = self.root.winfo_rooty()
        rw = self.root.winfo_width()
        rh = self.root.winfo_height()
        x = rx + (rw - dw) // 2
        y = ry + (rh - dh) // 2
        dialog.geometry(f"{dw}x{dh}+{x}+{y}")

        frm = tk.Frame(dialog, bg=BG_PRIMARY)
        frm.pack(fill="both", expand=True, padx=20, pady=20)

        tk.Label(
            frm, text="退出 SMTC Player？",
            font=("Segoe UI", 14, "bold"),
            fg=TEXT_PRIMARY, bg=BG_PRIMARY,
        ).pack(pady=(0, 16))

        btn_frame = tk.Frame(frm, bg=BG_PRIMARY)
        btn_frame.pack()

        btn_style = {
            "font": ("Segoe UI", 10),
            "bd": 0, "padx": 16, "pady": 8,
            "cursor": "hand2",
        }

        tk.Button(
            btn_frame, text="退出", fg=TEXT_PRIMARY, bg="#e74c3c",
            activebackground="#c0392b", activeforeground=TEXT_PRIMARY,
            command=lambda: [dialog.destroy(), self._quit_app()],
            **btn_style,
        ).pack(side="left", padx=4)

        tk.Button(
            btn_frame, text="最小化到托盘", fg=TEXT_PRIMARY, bg="#2a2a4a",
            activebackground="#3a3a5a", activeforeground=TEXT_PRIMARY,
            command=lambda: [dialog.destroy(), self._minimize_to_tray()],
            **btn_style,
        ).pack(side="left", padx=4)

        tk.Button(
            btn_frame, text="取消", fg=TEXT_HEX_SECONDARY, bg="#1e1e3a",
            activebackground="#2a2a4a", activeforeground=TEXT_PRIMARY,
            command=dialog.destroy,
            **btn_style,
        ).pack(side="left", padx=4)

    def _show_change_pin_dialog(self):
        dialog = tk.Toplevel(self.root)
        dialog.title("更改 PIN")
        dialog.configure(bg=BG_PRIMARY)
        dialog.resizable(False, False)
        dialog.transient(self.root)
        dialog.grab_set()

        dw = 360
        dh = 320
        rx = self.root.winfo_rootx()
        ry = self.root.winfo_rooty()
        rw = self.root.winfo_width()
        rh = self.root.winfo_height()
        x = rx + (rw - dw) // 2
        y = ry + (rh - dh) // 2
        dialog.geometry(f"{dw}x{dh}+{x}+{y}")

        frm = tk.Frame(dialog, bg=BG_PRIMARY)
        frm.pack(fill="both", expand=True, padx=24, pady=24)

        tk.Label(
            frm, text="更改访问 PIN",
            font=("Segoe UI", 14, "bold"),
            fg=TEXT_PRIMARY, bg=BG_PRIMARY,
        ).pack(pady=(0, 16))

        entry_style = {
            "font": ("Segoe UI", 11), "show": "*",
            "bg": "#2a2a4a", "fg": TEXT_PRIMARY, "bd": 0,
            "insertbackground": TEXT_PRIMARY, "width": 30,
        }

        tk.Label(frm, text="新 PIN（4-16 位）", font=("Segoe UI", 10),
                 fg=TEXT_HEX_SECONDARY, bg=BG_PRIMARY, anchor="w").pack(fill="x")
        new_entry = tk.Entry(frm, **entry_style)
        new_entry.pack(pady=(4, 10), ipady=4)

        tk.Label(frm, text="确认新 PIN", font=("Segoe UI", 10),
                 fg=TEXT_HEX_SECONDARY, bg=BG_PRIMARY, anchor="w").pack(fill="x")
        confirm_entry = tk.Entry(frm, **entry_style)
        confirm_entry.pack(pady=(4, 12), ipady=4)

        msg_label = tk.Label(frm, text="", font=("Segoe UI", 9),
                             fg="#e74c3c", bg=BG_PRIMARY)
        msg_label.pack(pady=(0, 10))

        def do_change():
            new = new_entry.get().strip()
            confirm = confirm_entry.get().strip()
            if not new or not confirm:
                msg_label.configure(text="请填写所有字段")
                return
            if new != confirm:
                msg_label.configure(text="两次输入的新 PIN 不一致")
                return
            try:
                data = json.dumps({"new_pin": new}).encode()
                req = urllib.request.Request(
                    f"{self.server_url}/api/auth/reset_pin",
                    data=data, method="POST",
                    headers={"Content-Type": "application/json"},
                )
                with urllib.request.urlopen(req, timeout=3) as resp:
                    result = json.loads(resp.read().decode())
                if result.get("success"):
                    msg_label.configure(fg=SUCCESS_GREEN, text="PIN 修改成功！")
                    dialog.after(1200, dialog.destroy)
                else:
                    msg_label.configure(fg="#e74c3c",
                                        text=result.get("error", "修改失败"))
            except Exception as e:
                msg_label.configure(text=f"请求失败: {e}")

        tk.Button(
            frm, text="确认修改", font=("Segoe UI", 12, "bold"),
            fg=TEXT_PRIMARY, bg="#667eea",
            activebackground="#5a6fd8", activeforeground=TEXT_PRIMARY,
            bd=0, padx=30, pady=10, cursor="hand2",
            command=do_change,
        ).pack(fill="x", ipady=4)

        new_entry.focus()

    def _show_debug_window(self):
        if self._debug_window and self._debug_window.winfo_exists():
            self._debug_window.deiconify()
            self._debug_window.lift()
            self._debug_window.focus_force()
            return

        self._debug_window = tk.Toplevel(self.root)
        self._debug_window.title("调试窗口 - SMTC Player")
        self._debug_window.configure(bg=BG_PRIMARY)

        dw = 680
        dh = 400
        rx = self.root.winfo_rootx()
        ry = self.root.winfo_rooty()
        rw = self.root.winfo_width()
        rh = self.root.winfo_height()
        x = rx + (rw - dw) // 2
        y = ry + (rh - dh) // 2
        self._debug_window.geometry(f"{dw}x{dh}+{x}+{y}")
        self._debug_window.minsize(400, 200)

        text_frame = tk.Frame(self._debug_window, bg=BG_PRIMARY)
        text_frame.pack(fill="both", expand=True, padx=10, pady=(10, 4))

        scrollbar = tk.Scrollbar(text_frame)
        scrollbar.pack(side="right", fill="y")

        self._debug_text = tk.Text(
            text_frame, bg="#0a0a1a", fg="#e0e0e0",
            font=("Consolas", 10), bd=0,
            insertbackground=TEXT_PRIMARY,
            yscrollcommand=scrollbar.set,
            wrap="word", state="disabled",
        )
        self._debug_text.pack(fill="both", expand=True)
        scrollbar.configure(command=self._debug_text.yview)

        btn_frame = tk.Frame(self._debug_window, bg=BG_PRIMARY)
        btn_frame.pack(fill="x", padx=10, pady=(4, 10))

        def clear_log():
            self._debug_text.configure(state="normal")
            self._debug_text.delete("1.0", "end")
            self._debug_text.configure(state="disabled")

        tk.Button(
            btn_frame, text="清空", font=("Segoe UI", 9),
            fg=TEXT_HEX_SECONDARY, bg="#2a2a4a",
            activebackground="#3a3a5a", activeforeground=TEXT_PRIMARY,
            bd=0, padx=12, pady=4, cursor="hand2",
            command=clear_log,
        ).pack(side="left")

        tk.Button(
            btn_frame, text="关闭", font=("Segoe UI", 9),
            fg=TEXT_PRIMARY, bg="#667eea",
            activebackground="#5a6fd8", activeforeground=TEXT_PRIMARY,
            bd=0, padx=12, pady=4, cursor="hand2",
            command=self._debug_window.withdraw,
        ).pack(side="right")

        self._debug_window.protocol("WM_DELETE_WINDOW", self._debug_window.withdraw)

        self._load_existing_log()

        self._start_log_watcher()

    def _load_existing_log(self):
        log_dir = os.path.join(os.environ.get("ProgramData", os.path.expanduser("~")),
                               "SMTCPlayer")
        log_path = os.path.join(log_dir, "smtc.log")
        if os.path.exists(log_path):
            try:
                with open(log_path, "r", encoding="utf-8") as f:
                    content = f.read()
                if content:
                    self._append_debug(content)
            except Exception:
                pass

    def _start_log_watcher(self):
        log_dir = os.path.join(os.environ.get("ProgramData", os.path.expanduser("~")),
                               "SMTCPlayer")
        self._debug_log_path = os.path.join(log_dir, "smtc.log")
        self._debug_log_offset = os.path.getsize(self._debug_log_path) if os.path.exists(
            self._debug_log_path) else 0

        def watch():
            if not self._debug_window or not self._debug_window.winfo_exists():
                return
            try:
                if os.path.exists(self._debug_log_path):
                    size = os.path.getsize(self._debug_log_path)
                    if size > self._debug_log_offset:
                        with open(self._debug_log_path, "r", encoding="utf-8") as f:
                            f.seek(self._debug_log_offset)
                            new_data = f.read()
                            self._debug_log_offset = size
                            self._append_debug(new_data)
            except Exception:
                pass
            self.root.after(2000, watch)

        watch()

    def _append_debug(self, text):
        self._debug_text.configure(state="normal")
        self._debug_text.insert("end", text)
        self._debug_text.see("end")
        self._debug_text.configure(state="disabled")

    def _quit_app(self):
        self.running = False
        if self.tray_icon and HAS_TRAY:
            try:
                self.tray_icon.stop()
            except Exception:
                pass
        if self.root:
            try:
                self.root.destroy()
            except Exception:
                pass
        os._exit(0)

    def run(self):
        self._build_ui()
        self.root.after(500, self._update_ui)
        self.root.mainloop()


if __name__ == "__main__":
    gui = SMTCGui()
    gui.run()
