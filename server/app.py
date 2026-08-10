import os
import sys
import socket
import json
import argparse
import struct
import zlib
import logging
from flask import Flask, jsonify, request, send_from_directory
from smtc_controller import SMTCController
from netease_watcher import NeteaseWatcherClient
from volume_controller import VolumeController
from security import PinAuth, validate_pin, load_config

logging.getLogger('werkzeug').setLevel(logging.ERROR)

def _get_static_folder():
    if getattr(sys, "frozen", False):
        return os.path.join(sys._MEIPASS, "static")
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), "static")


app = Flask(__name__, static_folder=_get_static_folder(), static_url_path="")
smtc = SMTCController()

_netease_watcher_host = os.environ.get("NETEASE_WATCHER_HOST", "127.0.0.1")
_netease_watcher_port = int(os.environ.get("NETEASE_WATCHER_PORT", "3574"))
netease_watcher = NeteaseWatcherClient(host=_netease_watcher_host, port=_netease_watcher_port)

volume_ctrl = VolumeController()
pin_auth = PinAuth()

IS_NETEASE_CLOUD_MUSIC = "cloudmusic"

_ncm_api = None


PUBLIC_API_PATHS = {
    "/api/auth/status",
    "/api/auth/setup",
    "/api/auth/login",
    "/api/auth/change_pin",
    "/api/auth/reset_pin",
}


@app.before_request
def require_pin_auth():
    if request.path in PUBLIC_API_PATHS:
        return None
    if not request.path.startswith("/api/"):
        return None
    if request.remote_addr in ("127.0.0.1", "::1"):
        return None
    if not pin_auth.is_configured():
        return jsonify({"success": False, "error": "PIN_NOT_CONFIGURED"}), 401
    token = request.headers.get("X-SMTC-Token") or request.args.get("token")
    if not pin_auth.validate_token(token):
        return jsonify({"success": False, "error": "UNAUTHORIZED"}), 401
    return None


def get_ncm_api():
    global _ncm_api
    if _ncm_api is None:
        try:
            from ncm_music_api import get_ncm_api as _get
            _ncm_api = _get()
        except ImportError as e:
            print(f"[NCM] 导入失败: {e}")
            _ncm_api = None
    return _ncm_api


def is_netease_cloud_music():
    source = smtc.get_session_source().lower()
    return IS_NETEASE_CLOUD_MUSIC in source


def get_merged_status():
    status = smtc.get_status().copy()
    status["source"] = smtc.get_session_source()
    status["netease_watcher_active"] = False

    if is_netease_cloud_music():
        ncm_status = netease_watcher.get_status()
        if ncm_status:
            if ncm_status["duration"] > 0:
                status["duration"] = ncm_status["duration"]
            if ncm_status["position"] > 0 or status["position"] == 0:
                status["position"] = ncm_status["position"]
            if not status["title"] or status["title"] == "未检测到媒体播放":
                status["title"] = ncm_status["title"]
            if not status["artist"]:
                status["artist"] = ncm_status["artist"]
            if not status["album_title"]:
                status["album_title"] = ncm_status["album_title"]
            if ncm_status.get("thumbnail"):
                status["thumbnail"] = ncm_status["thumbnail"]
            if ncm_status.get("song_id"):
                status["song_id"] = ncm_status["song_id"]
            status["netease_watcher_active"] = True

    master_vol = volume_ctrl.get_master_volume()
    status["volume"] = master_vol.get("volume", 0)
    status["muted"] = master_vol.get("muted", False)
    status["volume_available"] = master_vol.get("available", False)

    source = status.get("source", "")
    if source:
        app_vol = volume_ctrl.get_app_volume(source)
        if app_vol:
            status["app_volume"] = app_vol.get("volume", 100)
            status["app_muted"] = app_vol.get("muted", False)

    return status


def make_png_icon(size=192):
    r1, g1, b1 = 102, 126, 234
    r2, g2, b2 = 118, 75, 162
    cx, cy = size // 2, size // 2
    outer_r = int(size * 0.45)
    inner_r = int(size * 0.16)
    line_len = int(size * 0.35)
    line_width = max(4, int(size * 0.04))
    ring_width = max(4, int(size * 0.03))
    corner_r = size * 0.1875

    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            dx, dy = x - cx, y - cy
            dist = (dx * dx + dy * dy) ** 0.5

            bg_t = (x + y) / (2 * size)
            br = int(r1 + (r2 - r1) * bg_t)
            bg = int(g1 + (g2 - g1) * bg_t)
            bb = int(b1 + (b2 - b1) * bg_t)

            in_corner = True
            for cx_c, cy_c in [(corner_r, corner_r), (size - corner_r, corner_r),
                               (corner_r, size - corner_r), (size - corner_r, size - corner_r)]:
                dxc, dyc = x - cx_c, y - cy_c
                if (x < corner_r and y < corner_r and dxc * dxc + dyc * dyc > corner_r * corner_r):
                    in_corner = False
                if (x > size - corner_r and y < corner_r and dxc * dxc + dyc * dyc > corner_r * corner_r):
                    in_corner = False
                if (x < corner_r and y > size - corner_r and dxc * dxc + dyc * dyc > corner_r * corner_r):
                    in_corner = False
                if (x > size - corner_r and y > size - corner_r and dxc * dxc + dyc * dyc > corner_r * corner_r):
                    in_corner = False

            if not in_corner:
                raw.extend([0, 0, 0, 0])
                continue

            if outer_r - ring_width <= dist <= outer_r:
                raw.extend([255, 255, 255, 230])
            elif dist <= inner_r:
                raw.extend([255, 255, 255, 230])
            elif abs(dx) <= line_width // 2 and y < cy and y > cy - line_len:
                raw.extend([255, 255, 255, 230])
            else:
                raw.extend([br, bg, bb, 255])

    def chunk(ctype, data):
        c = ctype + data
        return struct.pack('>I', len(data)) + c + struct.pack('>I', zlib.crc32(c) & 0xffffffff)

    sig = b'\x89PNG\r\n\x1a\n'
    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    idat = zlib.compress(bytes(raw))
    return sig + chunk(b'IHDR', ihdr) + chunk(b'IDAT', idat) + chunk(b'IEND', b'')


PNG_192 = make_png_icon(192)
PNG_512 = make_png_icon(512)


@app.route("/")
def index():
    return send_from_directory(app.static_folder, "index.html")


@app.route("/service-worker.js")
def service_worker():
    resp = send_from_directory(app.static_folder, "service-worker.js")
    resp.headers["Cache-Control"] = "no-cache"
    return resp


@app.route("/icon-192.png")
def icon_192():
    return PNG_192, 200, {'Content-Type': 'image/png'}


@app.route("/icon-512.png")
def icon_512():
    return PNG_512, 200, {'Content-Type': 'image/png'}


@app.route("/api/auth/status")
def api_auth_status():
    return jsonify({
        "configured": pin_auth.is_configured(),
        "pin_policy": {
            "min": 4,
            "max": 16,
            "allowed": "letters, numbers, and !@#$%^&*()_-+=[]{}:;,.?/|~",
        },
    })


@app.route("/api/auth/setup", methods=["POST"])
def api_auth_setup():
    if pin_auth.is_configured():
        return jsonify({"success": False, "error": "PIN_ALREADY_CONFIGURED"}), 409
    data = request.get_json(silent=True) or {}
    pin = str(data.get("pin", ""))
    if not validate_pin(pin):
        return jsonify({"success": False, "error": "PIN_INVALID"}), 400
    pin_auth.set_pin(pin)
    token = pin_auth.login(pin)
    return jsonify({"success": True, "token": token})


@app.route("/api/auth/change_pin", methods=["POST"])
def api_auth_change_pin():
    data = request.get_json(silent=True) or {}
    old_pin = str(data.get("old_pin", ""))
    new_pin = str(data.get("new_pin", ""))
    retry_after = pin_auth.check_locked(request.remote_addr)
    if retry_after is not None:
        return jsonify({
            "success": False,
            "error": "TOO_MANY_ATTEMPTS",
            "retry_after": retry_after,
        }), 429
    success, error = pin_auth.change_pin(old_pin, new_pin)
    if not success:
        pin_auth.register_failure(request.remote_addr)
        return jsonify({"success": False, "error": error}), 400
    pin_auth.reset_failures(request.remote_addr)
    return jsonify({"success": True})


@app.route("/api/auth/reset_pin", methods=["POST"])
def api_auth_reset_pin():
    if request.remote_addr not in ("127.0.0.1", "::1"):
        return jsonify({"success": False, "error": "仅限本机操作"}), 403
    data = request.get_json(silent=True) or {}
    new_pin = str(data.get("new_pin", ""))
    success, error = pin_auth.force_set_pin(new_pin)
    if not success:
        return jsonify({"success": False, "error": error}), 400
    return jsonify({"success": True})


@app.route("/api/auth/login", methods=["POST"])
def api_auth_login():
    data = request.get_json(silent=True) or {}
    retry_after = pin_auth.check_locked(request.remote_addr)
    if retry_after is not None:
        return jsonify({
            "success": False,
            "error": "TOO_MANY_ATTEMPTS",
            "retry_after": retry_after,
        }), 429
    token = pin_auth.login(str(data.get("pin", "")))
    if not token:
        pin_auth.register_failure(request.remote_addr)
        return jsonify({"success": False, "error": "PIN_INCORRECT"}), 401
    pin_auth.reset_failures(request.remote_addr)
    return jsonify({"success": True, "token": token})


@app.route("/api/health")
def api_health():
    ncm = get_ncm_api()
    cfg = load_config()
    return jsonify({
        "ok": True,
        "version": "1.1.0",
        "auth_configured": pin_auth.is_configured(),
        "port": resolve_port(),
        "smtc": {
            "available": smtc.available,
            "source": smtc.get_session_source(),
        },
        "netease_watcher": {
            "available": netease_watcher.available,
            "base_url": netease_watcher.base_url,
        },
        "volume": {
            "available": volume_ctrl.available,
        },
        "ncm_api": {
            "available": ncm is not None,
            "logged_in": bool(ncm and ncm.logged_in),
            "nickname": ncm.nickname if ncm else None,
        },
        "config": {
            "has_pin": bool(cfg.get("pin_encrypted") or cfg.get("pin_hash")),
        },
    })


@app.route("/api/status", methods=["GET"])
def get_status():
    status = get_merged_status()
    return jsonify(status)


@app.route("/api/play_pause", methods=["POST"])
def api_play_pause():
    success = smtc.play_pause()
    return jsonify({"success": success})


@app.route("/api/play", methods=["POST"])
def api_play():
    success = smtc.play()
    return jsonify({"success": success})


@app.route("/api/pause", methods=["POST"])
def api_pause():
    success = smtc.pause()
    return jsonify({"success": success})


@app.route("/api/next", methods=["POST"])
def api_next():
    success = smtc.next_track()
    return jsonify({"success": success})


@app.route("/api/previous", methods=["POST"])
def api_previous():
    success = smtc.previous_track()
    return jsonify({"success": success})


@app.route("/api/volume", methods=["GET"])
def get_volume():
    vol = volume_ctrl.get_master_volume()
    return jsonify(vol)


@app.route("/api/volume", methods=["POST"])
def set_volume():
    data = request.get_json(silent=True) or {}
    volume = data.get("volume")
    if volume is None:
        return jsonify({"success": False, "error": "缺少 volume 参数"}), 400
    try:
        volume = float(volume)
    except (ValueError, TypeError):
        return jsonify({"success": False, "error": "volume 必须是数字"}), 400
    success = volume_ctrl.set_master_volume(volume)
    return jsonify({"success": success})


@app.route("/api/volume/toggle_mute", methods=["POST"])
def toggle_mute():
    success = volume_ctrl.toggle_mute()
    return jsonify({"success": success})


@app.route("/api/app_volume", methods=["GET"])
def get_app_volume():
    source = smtc.get_session_source()
    if not source:
        return jsonify({"available": False, "volume": 100, "muted": False})
    vol = volume_ctrl.get_app_volume(source)
    if vol:
        return jsonify(vol)
    return jsonify({"available": False, "volume": 100, "muted": False})


@app.route("/api/app_volume", methods=["POST"])
def set_app_volume():
    data = request.get_json(silent=True) or {}
    volume = data.get("volume")
    if volume is None:
        return jsonify({"success": False, "error": "缺少 volume 参数"}), 400
    source = smtc.get_session_source()
    if not source:
        return jsonify({"success": False, "error": "没有活动的媒体会话"}), 400
    try:
        volume = float(volume)
    except (ValueError, TypeError):
        return jsonify({"success": False, "error": "volume 必须是数字"}), 400
    success = volume_ctrl.set_app_volume(source, volume)
    return jsonify({"success": success})


# ============ NCM API Routes ============

@app.route("/api/ncm/login", methods=["POST"])
def api_ncm_login():
    api = get_ncm_api()
    if api is None:
        return jsonify({"code": -1, "msg": "NCM API 不可用，请安装 pycryptodome: pip install pycryptodome"})
    data = request.get_json(silent=True) or {}
    phone = str(data.get("phone", "")).strip()
    password = data.get("password", "")
    if not phone or not password:
        return jsonify({"code": -1, "msg": "请输入手机号和密码"})
    result = api.login_cellphone(phone, password=password)
    if result.get("code") == 200:
        return jsonify({
            "code": 200,
            "logged_in": True,
            "uid": api.uid,
            "nickname": api.nickname,
        })
    return jsonify({
        "code": result.get("code", -1),
        "msg": result.get("message") or result.get("msg", "登录失败"),
        "logged_in": False,
    })


@app.route("/api/ncm/login_email", methods=["POST"])
def api_ncm_login_email():
    api = get_ncm_api()
    if api is None:
        return jsonify({"code": -1, "msg": "NCM API 不可用"})
    data = request.get_json(silent=True) or {}
    email = str(data.get("email", "")).strip()
    password = data.get("password", "")
    if not email or not password:
        return jsonify({"code": -1, "msg": "请输入邮箱/用户名和密码"})
    result = api.login_email(email, password=password)
    if result.get("code") == 200:
        return jsonify({
            "code": 200,
            "logged_in": True,
            "uid": api.uid,
            "nickname": api.nickname,
        })
    return jsonify({
        "code": result.get("code", -1),
        "msg": result.get("message") or result.get("msg", "登录失败"),
        "logged_in": False,
    })


@app.route("/api/ncm/qrcode/create", methods=["POST"])
def api_ncm_qrcode_create():
    api = get_ncm_api()
    if api is None:
        return jsonify({"code": -1, "msg": "NCM API 不可用"})
    result = api.create_qrcode()
    return jsonify(result)


@app.route("/api/ncm/qrcode/check", methods=["POST"])
def api_ncm_qrcode_check():
    api = get_ncm_api()
    if api is None:
        return jsonify({"code": -1, "msg": "NCM API 不可用"})
    data = request.get_json(silent=True) or {}
    unikey = data.get("unikey", "")
    if not unikey:
        return jsonify({"code": -1, "msg": "缺少 unikey"})
    result = api.check_qrcode(unikey)
    if result.get("code") == 803:
        return jsonify({
            "code": 803,
            "logged_in": True,
            "uid": api.uid,
            "nickname": api.nickname,
        })
    return jsonify({"code": result.get("code"), "msg": result.get("msg", "")})


@app.route("/api/ncm/login_cookie", methods=["POST"])
def api_ncm_login_cookie():
    api = get_ncm_api()
    if api is None:
        return jsonify({"code": -1, "msg": "NCM API 不可用"})
    data = request.get_json(silent=True) or {}
    cookie = str(data.get("cookie", "")).strip()
    if not cookie:
        return jsonify({"code": -1, "msg": "请输入 MUSIC_U cookie"})
    result = api.login_cookie(cookie)
    if result.get("logged_in"):
        return jsonify({
            "code": 200,
            "logged_in": True,
            "uid": api.uid,
            "nickname": api.nickname,
        })
    return jsonify({"code": -1, "msg": "Cookie 无效或已过期", "logged_in": False})


@app.route("/api/ncm/logout", methods=["POST"])
def api_ncm_logout():
    api = get_ncm_api()
    if api:
        api.logout()
    return jsonify({"success": True})


@app.route("/api/ncm/clear_cookies", methods=["POST"])
def api_ncm_clear_cookies():
    api = get_ncm_api()
    if api:
        api.clear_cookies()
    return jsonify({"success": True, "msg": "Cookie 已清除"})


@app.route("/api/ncm/status")
def api_ncm_status():
    api = get_ncm_api()
    if api is None:
        return jsonify({"logged_in": False, "error": "NCM API 不可用"})
    result = api.check_login()
    return jsonify(result)


@app.route("/api/ncm/search")
def api_ncm_search():
    api = get_ncm_api()
    if api is None:
        return jsonify({"songs": [], "songCount": 0, "error": "NCM API 不可用"})
    keywords = request.args.get("q", "").strip()
    if not keywords:
        return jsonify({"songs": [], "songCount": 0})
    limit = request.args.get("limit", 20, type=int)
    result = api.search(keywords, limit=limit)
    return jsonify(result)


@app.route("/api/ncm/playlists")
def api_ncm_playlists():
    api = get_ncm_api()
    if api is None:
        return jsonify({"playlists": [], "msg": "NCM API 不可用"})
    result = api.get_user_playlists()
    return jsonify(result)


@app.route("/api/ncm/playlist/<int:playlist_id>")
def api_ncm_playlist_detail(playlist_id):
    api = get_ncm_api()
    if api is None:
        return jsonify({"tracks": [], "error": "NCM API 不可用"})
    result = api.get_playlist_detail(playlist_id)
    return jsonify(result)


@app.route("/api/ncm/play", methods=["POST"])
def api_ncm_play():
    api = get_ncm_api()
    if api is None:
        return jsonify({"success": False, "error": "NCM API 不可用"})
    data = request.get_json(silent=True) or {}
    song_id = data.get("song_id")
    if not song_id:
        return jsonify({"success": False, "error": "缺少 song_id"})
    success = api.play_song(song_id)
    return jsonify({"success": success, "error": None if success else "无法启动播放，请确保网易云音乐已打开"})


@app.route("/api/ncm/open_web", methods=["POST"])
def api_ncm_open_web():
    api = get_ncm_api()
    if api is None:
        return jsonify({"success": False, "error": "NCM API 不可用"})
    data = request.get_json(silent=True) or {}
    song_id = data.get("song_id")
    if not song_id:
        return jsonify({"success": False, "error": "缺少 song_id"})
    success = api.open_webpage(song_id)
    return jsonify({"success": success, "error": None if success else "无法打开网页"})


@app.route("/api/ncm/play_playlist", methods=["POST"])
def api_ncm_play_playlist():
    api = get_ncm_api()
    if api is None:
        return jsonify({"success": False, "error": "NCM API 不可用"})
    data = request.get_json(silent=True) or {}
    playlist_id = data.get("playlist_id")
    if not playlist_id:
        return jsonify({"success": False, "error": "缺少 playlist_id"})
    success = api.play_playlist(playlist_id)
    return jsonify({"success": success})


def get_local_ip():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"


def get_app_dir():
    if getattr(sys, "frozen", False):
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.abspath(__file__))


def load_config():
    from security import load_config as _load_config
    return _load_config()


def resolve_port(cli_port=None):
    if cli_port is not None:
        return int(cli_port)
    env_port = os.environ.get("SMTC_PORT")
    if env_port:
        return int(env_port)
    cfg = load_config()
    cfg_port = cfg.get("port")
    if cfg_port is not None:
        return int(cfg_port)
    return 8888


if __name__ == "__main__":
    import atexit
    import subprocess as _sp
    import signal as _sig

    parser = argparse.ArgumentParser(description="SMTC Player v1.1.0")
    parser.add_argument("--port", type=int, default=None, help="HTTP 服务端口 (默认: 8888)")
    parser.add_argument("--save-port", action="store_true", help="将 --port 参数保存到 config.json")
    args = parser.parse_args()

    port = resolve_port(args.port)

    if args.save_port and args.port:
        cfg = load_config()
        cfg["port"] = int(args.port)
        config_path = os.path.join(get_app_dir(), "config.json")
        try:
            with open(config_path, "w", encoding="utf-8") as f:
                json.dump(cfg, f, ensure_ascii=False, indent=2)
            print(f"[Config] 端口已保存到 {config_path}")
        except Exception as e:
            print(f"[Config] 保存配置失败: {e}")

    _watcher_proc = None

    def _start_watcher():
        global _watcher_proc
        app_dir = get_app_dir()
        watcher_exe = os.path.join(app_dir, "netease-watcher", "netease-watcher.exe")
        if not os.path.exists(watcher_exe):
            script_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            watcher_exe = os.path.join(script_dir, "netease-watcher", "netease-watcher.exe")
        if os.path.exists(watcher_exe):
            try:
                _watcher_proc = _sp.Popen(
                    [watcher_exe],
                    stdout=_sp.DEVNULL,
                    stderr=_sp.DEVNULL,
                    creationflags=_sp.CREATE_NO_WINDOW if sys.platform == "win32" else 0,
                )
                print(f"[Watcher] netease-watcher 已启动 (PID: {_watcher_proc.pid})")
            except Exception as e:
                print(f"[Watcher] 启动失败: {e}")
        else:
            print(f"[Watcher] 未找到 netease-watcher.exe，跳过")

    def _stop_watcher():
        global _watcher_proc
        if _watcher_proc and _watcher_proc.poll() is None:
            try:
                _watcher_proc.terminate()
                _watcher_proc.wait(timeout=3)
                print("[Watcher] netease-watcher 已停止")
            except Exception:
                try:
                    _watcher_proc.kill()
                except Exception:
                    pass

    atexit.register(_stop_watcher)
    _start_watcher()

    # 等待 watcher 就绪后刷新可用性（最多重试 5 次，每次 1 秒）
    import time as _time
    for _ in range(5):
        _time.sleep(1)
        netease_watcher._last_fail_time = 0
        netease_watcher._check_available(quiet=True)
        if netease_watcher.available:
            break

    local_ip = get_local_ip()

    print("=" * 60)
    print("  SMTC Player v1.1.0 - 媒体控制器服务端")
    print("=" * 60)
    print(f"  本地访问: http://127.0.0.1:{port}")
    print(f"  局域网访问: http://{local_ip}:{port}")
    print(f"  SMTC可用: {'是' if smtc.available else '否 (模拟模式)'}")
    if netease_watcher.available:
        print(f"  网易云增强: 已启用 (netease-watcher)")
    else:
        print(f"  网易云增强: 未检测到 (可选)")
    print(f"  音量控制: {'是' if volume_ctrl.available else '否'}")

    ncm = get_ncm_api()
    if ncm is not None:
        print(f"  网易云API: 已加载")
        if ncm.logged_in:
            print(f"  网易云登录: {ncm.nickname or '已登录'}")
        else:
            print(f"  网易云登录: 未登录 (支持手机/邮箱/扫码三种方式)")
    else:
        print(f"  网易云API: 不可用 (需安装 pycryptodome)")

    print("=" * 60)
    print("  提示: 确保手机/设备与电脑在同一局域网")
    if netease_watcher.available:
        print("  netease-watcher 已就绪，提供精确进度和封面")
    if ncm is None:
        print("  搜索和歌单: 需安装 pycryptodome (pip install pycryptodome)")
    print("=" * 60)

    app.run(host="0.0.0.0", port=port, debug=False, threaded=True)
