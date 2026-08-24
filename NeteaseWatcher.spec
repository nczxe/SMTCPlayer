# -*- mode: python ; coding: utf-8 -*-
# 鐙珛缃戞槗浜戠姸鎬佺洃瑙嗗櫒锛圢eteaseEnhance 鎻掍欢鑷甫璧勬簮锛岄殢鎻掍欢鍒嗗彂鐩綍閮ㄧ讲锛夛細
# 鐢辨彃浠跺湪鏈嶅姟杩愯鏈熼棿鎷夎捣/鍥炴敹锛屾渶缁堢敤鎴锋満鍣ㄦ棤闇€瀹夎 Python銆?# 涓庝富鏈嶅姟绔紙SMTCPlayerServer.spec锛夊畬鍏ㄨВ鑰︼紝鐢熷懡鍛ㄦ湡褰掓彃浠舵墍鏈夈€?import sys
from pathlib import Path

server_dir = Path("server")

a = Analysis(
    [str(server_dir / "netease_watcher_server.py")],
    pathex=[str(server_dir)],
    binaries=[],
    datas=[],
    hiddenimports=["psutil", "numpy"],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="NeteaseWatcher",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    console=True,  # 瀹夸富浠?CreateNoWindow 鍚姩锛沜onsole 妯″紡閬垮厤 windowed stdout 缂撳啿闂
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
    name="NeteaseWatcher",
)
