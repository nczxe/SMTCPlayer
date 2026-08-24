# -*- mode: python ; coding: utf-8 -*-
# 内嵌无头服务端（供 .NET 宿主随安装包分发）：
# 与根目录 SMTCPlayer.spec（独立带 GUI 版）的区别——排除 Tkinter/pystray/GUI 模块，
# 由 .NET 端以 "--port N --no-gui" 启动，最终用户机器无需安装任何 Python。
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
        "app",
        "Crypto.Cipher.AES",
        "Crypto.Util.Padding",
        "Crypto.Cipher._mode_cbc",
        "PIL.Image",
        "PIL.ImageDraw",
        "PIL.ImageFilter",
        "qrcode",
        "qrcode.image.pil",
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        "gui",
        "tkinter",
        "pystray",
        "PIL.ImageTk",
        "PIL._tkinter_finder",
    ],
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
    name="SMTCPlayerServer",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    console=True,  # 由宿主 CreateNoWindow 启动；console 模式避免 windowed stdout 缓冲问题
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
    name="SMTCPlayerServer",
)
