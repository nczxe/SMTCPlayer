# -*- mode: python ; coding: utf-8 -*-
import sys
from pathlib import Path

server_dir = Path("server")

a = Analysis(
    [str(server_dir / "main.py")],
    pathex=[str(server_dir)],
    binaries=[],
    datas=[
        (str(server_dir / "static"), "static"),
    ],
    hiddenimports=[
        "smtc_controller",
        "netease_watcher_client",
        "volume_controller",
        "ncm_music_api",
        "security",
        "gui",
        "app",
        "Crypto.Cipher.AES",
        "Crypto.Util.Padding",
        "Crypto.Cipher._mode_cbc",
        "PIL._tkinter_finder",
        "PIL.Image",
        "PIL.ImageDraw",
        "PIL.ImageFont",
        "PIL.ImageFilter",
        "PIL.ImageTk",
        "qrcode",
        "qrcode.image.pil",
        "pystray",
        "pystray._win32",
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)

a.datas = [
    t for t in a.datas
    if "tcl_data" not in t[0] or "encoding" not in t[0] or not t[0].endswith(".enc")
]

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="SMTCPlayer",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name="SMTCPlayer",
)
