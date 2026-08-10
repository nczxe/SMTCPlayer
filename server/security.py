import base64
import hashlib
import hmac
import json
import os
import re
import secrets
import sys
import time
from pathlib import Path


PIN_RE = re.compile(r"^[A-Za-z0-9!@#$%^&*()_\-+=\[\]{}:;,.?/|~]{4,16}$")


def get_app_dir():
    if getattr(sys, "frozen", False):
        return Path(sys.executable).parent
    return Path(__file__).resolve().parent


def get_config_path():
    return get_app_dir() / "config.json"


def load_config():
    path = get_config_path()
    if path.exists():
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            return {}
    return {}


def save_config(config):
    path = get_config_path()
    path.write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding="utf-8")


def validate_pin(pin):
    return isinstance(pin, str) and bool(PIN_RE.fullmatch(pin))


class PinAuth:
    def __init__(self):
        self._tokens = set()
        self._failed_attempts = {}
        self.max_attempts = 5
        self.lock_seconds = 300

    def _limiter_key(self, client_ip):
        return client_ip or "unknown"

    def check_locked(self, client_ip):
        key = self._limiter_key(client_ip)
        info = self._failed_attempts.get(key)
        if not info:
            return None
        remaining = info["locked_until"] - time.time()
        if remaining > 0:
            return int(remaining) + 1
        return None

    def register_failure(self, client_ip):
        key = self._limiter_key(client_ip)
        info = self._failed_attempts.get(key)
        if info is None or (info["locked_until"] > 0 and info["locked_until"] <= time.time()):
            info = {"count": 0, "locked_until": 0}
        info["count"] += 1
        if info["count"] >= self.max_attempts:
            info["locked_until"] = time.time() + self.lock_seconds
            info["count"] = 0
        self._failed_attempts[key] = info

    def reset_failures(self, client_ip):
        self._failed_attempts.pop(self._limiter_key(client_ip), None)

    def _migrate_legacy(self, cfg, pin):
        if cfg.get("pin_salt") and cfg.get("pin_hash"):
            cfg.pop("pin_salt", None)
            cfg.pop("pin_hash", None)
            cfg["pin_encrypted"] = protect_bytes(pin.encode("utf-8"))
            save_config(cfg)
            return True
        return False

    def is_configured(self):
        cfg = load_config()
        return bool(cfg.get("pin_encrypted") or (cfg.get("pin_salt") and cfg.get("pin_hash")))

    def set_pin(self, pin):
        if not validate_pin(pin):
            return False
        cfg = load_config()
        cfg.pop("pin_salt", None)
        cfg.pop("pin_hash", None)
        cfg["pin_encrypted"] = protect_bytes(pin.encode("utf-8"))
        save_config(cfg)
        self._tokens.clear()
        return True

    def login(self, pin):
        cfg = load_config()
        encrypted = cfg.get("pin_encrypted")
        if encrypted:
            try:
                stored = unprotect_bytes(encrypted).decode("utf-8")
            except Exception:
                return None
            if not hmac.compare_digest(stored, pin):
                return None
        elif cfg.get("pin_salt") and cfg.get("pin_hash"):
            digest = hashlib.pbkdf2_hmac("sha256", pin.encode("utf-8"), cfg["pin_salt"].encode("ascii"), 200_000)
            expected = base64.b64encode(digest).decode("ascii")
            if not hmac.compare_digest(expected, cfg.get("pin_hash", "")):
                return None
            self._migrate_legacy(cfg, pin)
        else:
            return None
        token = secrets.token_urlsafe(32)
        self._tokens.add(token)
        return token

    def change_pin(self, old_pin, new_pin):
        if not validate_pin(new_pin):
            return False, "新 PIN 格式无效（需 4-16 位字母/数字/安全字符）"
        cfg = load_config()
        encrypted = cfg.get("pin_encrypted")
        if encrypted:
            try:
                stored = unprotect_bytes(encrypted).decode("utf-8")
            except Exception:
                return False, "无法解密 PIN"
            if not hmac.compare_digest(stored, old_pin):
                return False, "旧 PIN 不正确"
        elif cfg.get("pin_salt") and cfg.get("pin_hash"):
            digest = hashlib.pbkdf2_hmac("sha256", old_pin.encode("utf-8"), cfg["pin_salt"].encode("ascii"), 200_000)
            expected = base64.b64encode(digest).decode("ascii")
            if not hmac.compare_digest(expected, cfg.get("pin_hash", "")):
                return False, "旧 PIN 不正确"
        else:
            return False, "PIN 未设置"
        cfg.pop("pin_salt", None)
        cfg.pop("pin_hash", None)
        cfg["pin_encrypted"] = protect_bytes(new_pin.encode("utf-8"))
        save_config(cfg)
        self._tokens.clear()
        return True, None

    def force_set_pin(self, new_pin):
        if not validate_pin(new_pin):
            return False, "PIN 格式无效（需 4-16 位字母/数字/安全字符）"
        cfg = load_config()
        cfg.pop("pin_salt", None)
        cfg.pop("pin_hash", None)
        cfg["pin_encrypted"] = protect_bytes(new_pin.encode("utf-8"))
        save_config(cfg)
        self._tokens.clear()
        return True, None

    def validate_token(self, token):
        return bool(token and token in self._tokens)


def protect_bytes(data):
    if sys.platform != "win32":
        return base64.b64encode(data).decode("ascii")
    import ctypes
    from ctypes import wintypes

    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32
    in_buf = ctypes.create_string_buffer(data)
    in_blob = DATA_BLOB(len(data), ctypes.cast(in_buf, ctypes.POINTER(ctypes.c_char)))
    out_blob = DATA_BLOB()
    if not crypt32.CryptProtectData(ctypes.byref(in_blob), None, None, None, None, 0, ctypes.byref(out_blob)):
        raise OSError("CryptProtectData failed")
    try:
        protected = ctypes.string_at(out_blob.pbData, out_blob.cbData)
        return "dpapi:" + base64.b64encode(protected).decode("ascii")
    finally:
        kernel32.LocalFree(out_blob.pbData)


def unprotect_bytes(text):
    if not text:
        return b""
    if not text.startswith("dpapi:"):
        return base64.b64decode(text.encode("ascii"))
    if sys.platform != "win32":
        raise OSError("DPAPI data can only be decrypted on Windows")
    import ctypes
    from ctypes import wintypes

    raw = base64.b64decode(text[6:].encode("ascii"))

    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32
    in_buf = ctypes.create_string_buffer(raw)
    in_blob = DATA_BLOB(len(raw), ctypes.cast(in_buf, ctypes.POINTER(ctypes.c_char)))
    out_blob = DATA_BLOB()
    if not crypt32.CryptUnprotectData(ctypes.byref(in_blob), None, None, None, None, 0, ctypes.byref(out_blob)):
        raise OSError("CryptUnprotectData failed")
    try:
        return ctypes.string_at(out_blob.pbData, out_blob.cbData)
    finally:
        kernel32.LocalFree(out_blob.pbData)
