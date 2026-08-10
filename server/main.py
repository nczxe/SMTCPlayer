import argparse
import json
import os
import subprocess as _sp
import sys
import threading
import time
import atexit
import socket
from security import get_app_dir as security_app_dir, load_config, save_config, validate_pin, PinAuth


def get_app_dir():
    return str(security_app_dir())


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


def get_local_ip():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"


import shutil


def start_watcher(app_dir):
    watcher_dir = os.path.join(app_dir, "netease-watcher")
    watcher_exe = os.path.join(watcher_dir, "netease-watcher.exe")

    if getattr(sys, "frozen", False):
        meipass = getattr(sys, "_MEIPASS", "")
        if meipass:
            src_dir = os.path.join(meipass, "netease-watcher")
            src_exe = os.path.join(src_dir, "netease-watcher.exe")
            if os.path.exists(src_exe):
                persistent_dir = os.path.join(
                    os.environ.get("ProgramData", os.path.expanduser("~")),
                    "SMTCPlayer", "watcher",
                )
                os.makedirs(persistent_dir, exist_ok=True)
                dst_exe = os.path.join(persistent_dir, "netease-watcher.exe")
                dst_dll = os.path.join(persistent_dir, "wndhok.dll")
                src_dll = os.path.join(src_dir, "wndhok.dll")

                if not os.path.exists(dst_exe) or (
                    os.path.getmtime(src_exe) > os.path.getmtime(dst_exe)
                ):
                    shutil.copy2(src_exe, dst_exe)
                    if os.path.exists(src_dll):
                        shutil.copy2(src_dll, dst_dll)
                    print(f"[Watcher] 已复制到 {persistent_dir}")

                watcher_exe = dst_exe

    if not os.path.exists(watcher_exe):
        script_dir = os.path.dirname(os.path.abspath(__file__))
        watcher_exe = os.path.join(
            os.path.dirname(script_dir), "netease-watcher", "netease-watcher.exe"
        )
    if os.path.exists(watcher_exe):
        try:
            proc = _sp.Popen(
                [watcher_exe],
                stdout=_sp.DEVNULL,
                stderr=_sp.DEVNULL,
                creationflags=_sp.CREATE_NO_WINDOW if sys.platform == "win32" else 0,
            )
            print(f"[Watcher] netease-watcher 已启动 (PID: {proc.pid})")
            return proc
        except Exception as e:
            print(f"[Watcher] 启动失败: {e}")
    else:
        print("[Watcher] 未找到 netease-watcher.exe")
    return None


def main():
    if getattr(sys, "frozen", False) and sys.platform == "win32":
        log_dir = os.path.join(os.environ.get("ProgramData", os.path.expanduser("~")), "SMTCPlayer")
        os.makedirs(log_dir, exist_ok=True)
        log_path = os.path.join(log_dir, "smtc.log")
        try:
            sys.stdout = open(log_path, "a", encoding="utf-8", buffering=1)
            sys.stderr = sys.stdout
        except Exception:
            pass

    parser = argparse.ArgumentParser(description="SMTC Player")
    parser.add_argument("--port", type=int, default=None, help="HTTP 服务端口 (默认: 8888)")
    parser.add_argument("--save-port", action="store_true", help="将 --port 参数保存到 config.json")
    parser.add_argument("--no-gui", action="store_true", help="不启动 GUI，仅运行 Flask 服务")
    parser.add_argument("--set-pin", default=None, help="设置访问 PIN (4-16 位字母/数字/安全字符)")
    args = parser.parse_args()

    port = resolve_port(args.port)

    if args.set_pin is not None:
        if not validate_pin(args.set_pin):
            print("[Config] PIN 格式无效：需 4-16 位，可包含字母、数字和 !@#$%^&*()_-+=[]{}:;,.?/|~")
            return
        PinAuth().set_pin(args.set_pin)
        print("[Config] PIN 已保存")

    if args.save_port and args.port:
        cfg = load_config()
        cfg["port"] = int(args.port)
        config_path = os.path.join(get_app_dir(), "config.json")
        try:
            save_config(cfg)
            print(f"[Config] 端口已保存到 {config_path}")
        except Exception as e:
            print(f"[Config] 保存配置失败: {e}")

    app_dir = get_app_dir()
    watcher_proc = start_watcher(app_dir)

    def stop_watcher():
        if watcher_proc and watcher_proc.poll() is None:
            try:
                watcher_proc.terminate()
                watcher_proc.wait(timeout=3)
            except Exception:
                try:
                    watcher_proc.kill()
                except Exception:
                    pass

    atexit.register(stop_watcher)

    local_ip = get_local_ip()

    def start_flask(flask_port=None):
        actual_port = flask_port or port
        from app import app as _flask_app
        t = threading.Thread(
            target=lambda: _flask_app.run(
                host="0.0.0.0", port=actual_port, debug=False, threaded=True, use_reloader=False,
            ),
            daemon=True,
        )
        t.start()
        time.sleep(0.5)
        print("=" * 60)
        print("  SMTC Player v1.1.0")
        print("=" * 60)
        print(f"  本地访问: http://127.0.0.1:{actual_port}")
        print(f"  局域网访问: http://{local_ip}:{actual_port}")
        print("=" * 60)
        return t

    if args.no_gui:
        start_flask()
        print("  Flask 服务运行中 (无 GUI 模式)")
        print("  按 Ctrl+C 退出")
        try:
            while True:
                time.sleep(1)
        except KeyboardInterrupt:
            print("\n  正在退出...")
        return

    from gui import SMTCGui

    gui = SMTCGui(flask_port=port, local_ip=local_ip, start_flask=start_flask)
    gui.run()


if __name__ == "__main__":
    main()
