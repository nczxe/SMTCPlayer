import base64
import binascii
import hashlib
import json
import os
import random
import subprocess
import sys
import time
from urllib.parse import urlencode

import requests
from security import protect_bytes, unprotect_bytes

MODULUS = (
    "00e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b7"
    "25152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0"
    "312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce1"
    "0b424d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db"
    "0a22b8e7"
)
EXPONENT = "010001"
NONCE = "0CoJUm6Qyw8W8jud"
IV = "0102030405060708"

CACHE_DIR = os.path.join(os.environ.get("ProgramData", os.path.expanduser("~")), "SMTCPlayer", ".ncm_cache")
COOKIE_FILE = os.path.join(CACHE_DIR, "cookies.json")


def ensure_cache_dir():
    os.makedirs(CACHE_DIR, exist_ok=True)


class NCMApi:
    def __init__(self):
        self.session = requests.Session()
        self.session.headers.update({
            "User-Agent": (
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/131.0.0.0 Safari/537.36"
            ),
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
            "Cache-Control": "no-cache",
            "Pragma": "no-cache",
            "Sec-Ch-Ua": '"Google Chrome";v="131", "Chromium";v="131", "Not_A Brand";v="24"',
            "Sec-Ch-Ua-Mobile": "?0",
            "Sec-Ch-Ua-Platform": '"Windows"',
            "Sec-Fetch-Dest": "document",
            "Sec-Fetch-Mode": "navigate",
            "Sec-Fetch-Site": "none",
            "Sec-Fetch-User": "?1",
            "Upgrade-Insecure-Requests": "1",
        })
        self.uid = None
        self.nickname = None
        self._load_cookies()

    def _load_cookies(self):
        ensure_cache_dir()
        if os.path.exists(COOKIE_FILE):
            try:
                with open(COOKIE_FILE, "r", encoding="utf-8") as f:
                    raw = json.load(f)
                if raw.get("encrypted"):
                    payload = unprotect_bytes(raw.get("payload", "")).decode("utf-8")
                    data = json.loads(payload)
                else:
                    data = raw
                for key, value in data.get("cookies", {}).items():
                    self.session.cookies.set(key, value, domain="music.163.com")
                self.uid = data.get("uid")
                self.nickname = data.get("nickname")
                print(f"[NCM] 已加载登录状态: {self.nickname or self.uid or '未知'}")
                if not raw.get("encrypted"):
                    self._save_cookies()
            except Exception as e:
                print(f"[NCM] 加载Cookie失败: {e}")

    def _save_cookies(self):
        ensure_cache_dir()
        data = {
            "uid": self.uid,
            "nickname": self.nickname,
            "cookies": {c.name: c.value for c in self.session.cookies
                        if c.domain in (".music.163.com", "music.163.com", ".163.com")},
        }
        try:
            protected = protect_bytes(json.dumps(data, ensure_ascii=False).encode("utf-8"))
            with open(COOKIE_FILE, "w", encoding="utf-8") as f:
                json.dump({"encrypted": True, "payload": protected}, f, ensure_ascii=False, indent=2)
        except Exception as e:
            print(f"[NCM] 保存Cookie失败: {e}")

    @property
    def logged_in(self):
        return self.uid is not None

    def _aes_encrypt(self, text, key):
        try:
            from Crypto.Cipher import AES
            data = text.encode("utf-8")
            pad = 16 - len(data) % 16
            data += bytes([pad] * pad)
            cipher = AES.new(key.encode("utf-8"), AES.MODE_CBC, IV.encode("utf-8"))
            return base64.b64encode(cipher.encrypt(data)).decode()
        except ImportError:
            raise ImportError(
                "需要 pycryptodome 库，请运行: pip install pycryptodome"
            )

    def _rsa_encrypt(self, text):
        text = text[::-1]
        rs = int(binascii.hexlify(text.encode("utf-8")).decode(), 16)
        result = pow(rs, int(EXPONENT, 16), int(MODULUS, 16))
        return format(result, 'x').zfill(256)

    def _encrypt_payload(self, data):
        json_str = json.dumps(data, ensure_ascii=False)
        sec_key = "".join(random.choices("0123456789abcdef", k=16))
        enc_text = self._aes_encrypt(json_str, NONCE)
        enc_text = self._aes_encrypt(enc_text, sec_key)
        enc_sec_key = self._rsa_encrypt(sec_key)
        return {"params": enc_text, "encSecKey": enc_sec_key}

    def _weapi(self, url, data):
        self._ensure_csrf()
        headers = {
            "Content-Type": "application/x-www-form-urlencoded",
            "Referer": "https://music.163.com/",
            "Origin": "https://music.163.com",
        }
        payload = self._encrypt_payload(data)
        resp = self.session.post(url, data=payload, headers=headers, timeout=10)
        try:
            return resp.json()
        except Exception as e:
            print(f"[NCM] API response error url={url} status={resp.status_code}")
            print(f"[NCM] Response: {resp.text[:300]}")
            raise

    def _eapi(self, url, data):
        pass

    def _get_csrf(self):
        for cookie in self.session.cookies:
            if cookie.name == "__csrf":
                return cookie.value
        return ""

    def _ensure_csrf(self):
        csrf_val = self._get_csrf()
        if csrf_val:
            return
        try:
            print("[NCM] Getting cookies...")
            resp = self.session.get("https://music.163.com/", timeout=10, headers={
                "Referer": "https://music.163.com/",
            })
            csrf_val = self._get_csrf()
            if csrf_val:
                print(f"[NCM] Got cookies, __csrf={csrf_val[:8]}...")
            else:
                print(f"[NCM] No __csrf cookie found")
        except Exception as e:
            print(f"[NCM] Failed to get cookies: {e}")

    def login_cellphone(self, phone, password=None, md5_password=None):
        if md5_password:
            pwd = md5_password
        elif password:
            pwd = hashlib.md5(password.encode("utf-8")).hexdigest()
        else:
            return {"code": -1, "msg": "需要密码"}

        data = {
            "phone": phone,
            "countrycode": "86",
            "password": pwd,
            "rememberLogin": "true",
            "csrf_token": self._get_csrf(),
        }

        try:
            result = self._weapi(
                "https://music.163.com/weapi/login/cellphone", data
            )
        except Exception as e:
            return {"code": -1, "msg": f"网络错误: {e}"}

        if result.get("code") == 200:
            profile = result.get("profile", {})
            self.uid = profile.get("userId") or result.get("account", {}).get("id")
            self.nickname = profile.get("nickname", "用户")
            self._save_cookies()
            print(f"[NCM] 登录成功: {self.nickname} (uid={self.uid})")
        else:
            code = result.get("code", -1)
            msg_map = {
                501: "手机号不存在",
                502: "密码错误",
                503: "验证码错误/验证太频繁",
                509: "登录状态已过期，请重新登录",
            }
            msg = msg_map.get(code, result.get("message", f"登录失败 (code={code})"))
            print(f"[NCM] 登录失败: {msg}")

        return result

    def login_email(self, email, password):
        pwd = hashlib.md5(password.encode("utf-8")).hexdigest()
        data = {
            "username": email,
            "password": pwd,
            "rememberLogin": "true",
            "csrf_token": self._get_csrf(),
        }
        try:
            result = self._weapi(
                "https://music.163.com/weapi/login", data
            )
        except Exception as e:
            return {"code": -1, "msg": f"网络错误: {e}"}

        if result.get("code") == 200:
            profile = result.get("profile", {})
            self.uid = profile.get("userId") or result.get("account", {}).get("id")
            self.nickname = profile.get("nickname", "用户")
            self._save_cookies()
            print(f"[NCM] 邮箱登录成功: {self.nickname} (uid={self.uid})")
        else:
            code = result.get("code", -1)
            msg = result.get("message", f"登录失败 (code={code})")
            print(f"[NCM] 邮箱登录失败: {msg}")
        return result

    def login_cookie(self, cookie_value):
        self.session.cookies.set("MUSIC_U", cookie_value, domain="music.163.com")
        self._ensure_csrf()
        try:
            data = {"csrf_token": self._get_csrf()}
            result = self._weapi(
                "https://music.163.com/weapi/w/nuser/account/get", data
            )
            if result.get("code") == 200:
                profile = result.get("profile", {})
                self.uid = profile.get("userId")
                self.nickname = profile.get("nickname", "用户")
                self._save_cookies()
                print(f"[NCM] Cookie登录成功: {self.nickname} (uid={self.uid})")
                return {"logged_in": True, "uid": self.uid, "nickname": self.nickname}
            else:
                print(f"[NCM] Cookie验证失败: code={result.get('code')}")
        except Exception as e:
            print(f"[NCM] Cookie登录异常: {e}")
        return {"logged_in": False}

    def create_qrcode(self):
        return {"code": -1, "msg": "扫码登录暂不可用，请使用 Cookie 登录"}

    def check_qrcode(self, unikey):
        return {"code": -1, "msg": "扫码登录暂不可用，请使用 Cookie 登录"}

    def check_login(self):
        if not self.uid:
            return {"logged_in": False}
        try:
            data = {"csrf_token": self._get_csrf()}
            result = self._weapi(
                "https://music.163.com/weapi/w/nuser/account/get", data
            )
            if result.get("code") == 200:
                profile = result.get("profile", {})
                self.nickname = profile.get("nickname", self.nickname)
                return {
                    "logged_in": True,
                    "uid": self.uid,
                    "nickname": self.nickname,
                }
            else:
                self.uid = None
                self.nickname = None
                return {"logged_in": False, "reason": result.get("message", "登录过期")}
        except Exception as e:
            return {"logged_in": self.uid is not None, "error": str(e)}

    def search(self, keywords, search_type=1, limit=30, offset=0):
        self._ensure_csrf()
        params = {
            "s": keywords,
            "type": search_type,
            "limit": limit,
            "offset": offset,
        }
        url = "https://music.163.com/api/cloudsearch/pc"
        headers = {
            "Referer": "https://music.163.com/",
        }
        try:
            resp = self.session.get(url, params=params, headers=headers, timeout=10)
            result = resp.json()
        except Exception as e:
            print(f"[NCM] search error: {e}")
            return {"songs": [], "songCount": 0, "code": -1}

        code = result.get("code", -1)
        print(f"[NCM] search code={code} keyword={keywords}")

        songs = []
        song_count = 0
        r = result.get("result", {})
        if isinstance(r, dict):
            song_count = r.get("songCount", 0)
            raw_songs = r.get("songs", [])
            for s in raw_songs:
                songs.append({
                    "id": s.get("id"),
                    "name": s.get("name", ""),
                    "artists": " / ".join(
                        a.get("name", "") for a in s.get("ar", [])
                    ),
                    "album": s.get("al", {}).get("name", ""),
                    "cover": s.get("al", {}).get("picUrl", ""),
                    "duration": s.get("dt", 0),
                })

        return {"songs": songs, "songCount": song_count, "code": code}

    def get_user_playlists(self):
        if not self.uid:
            return {"playlists": [], "msg": "未登录"}

        data = {
            "uid": self.uid,
            "limit": 100,
            "offset": 0,
            "csrf_token": self._get_csrf(),
        }
        result = self._weapi(
            "https://music.163.com/weapi/user/playlist", data
        )
        playlists = []
        for pl in result.get("playlist", []):
            playlists.append({
                "id": pl.get("id"),
                "name": pl.get("name", ""),
                "cover": pl.get("coverImgUrl", ""),
                "trackCount": pl.get("trackCount", 0),
                "playCount": pl.get("playCount", 0),
                "creator": pl.get("creator", {}).get("nickname", ""),
            })

        return {"playlists": playlists, "code": result.get("code")}

    def get_playlist_detail(self, playlist_id):
        data = {
            "id": str(playlist_id),
            "n": 100000,
            "s": 8,
            "csrf_token": self._get_csrf(),
        }
        result = self._weapi(
            "https://music.163.com/weapi/v6/playlist/detail", data
        )

        pl = result.get("playlist", {})
        track_ids = pl.get("trackIds", [])
        tracks_raw = pl.get("tracks", [])

        if tracks_raw:
            tracks = [{
                "id": t.get("id"),
                "name": t.get("name", ""),
                "artists": " / ".join(
                    a.get("name", "") for a in t.get("ar", [])
                ),
                "album": t.get("al", {}).get("name", ""),
                "cover": t.get("al", {}).get("picUrl", ""),
                "duration": t.get("dt", 0),
                "trackNo": t.get("no", 0),
            } for t in tracks_raw]

            return {
                "id": pl.get("id"),
                "name": pl.get("name", ""),
                "cover": pl.get("coverImgUrl", ""),
                "trackCount": pl.get("trackCount", 0),
                "creator": pl.get("creator", {}).get("nickname", ""),
                "tracks": tracks,
                "trackIds": [t.get("id") for t in track_ids],
                "code": result.get("code"),
            }
        else:
            all_ids = []
            for item in track_ids:
                tid = item.get("id") if isinstance(item, dict) else item
                all_ids.append(tid)

            tracks = []
            for batch_start in range(0, len(all_ids), 1000):
                batch = all_ids[batch_start:batch_start + 1000]
                ids_str = ",".join(str(tid) for tid in batch)
                detail_data = {"c": ids_str, "csrf_token": self._get_csrf()}
                detail_result = self._weapi(
                    "https://music.163.com/weapi/v3/song/detail", detail_data
                )
                for s in detail_result.get("songs", []):
                    tracks.append({
                        "id": s.get("id"),
                        "name": s.get("name", ""),
                        "artists": " / ".join(
                            a.get("name", "") for a in s.get("ar", [])
                        ),
                        "album": s.get("al", {}).get("name", ""),
                        "cover": s.get("al", {}).get("picUrl", ""),
                        "duration": s.get("dt", 0),
                        "trackNo": s.get("no", 0),
                    })

            return {
                "id": pl.get("id"),
                "name": pl.get("name", ""),
                "cover": pl.get("coverImgUrl", ""),
                "trackCount": pl.get("trackCount", 0),
                "creator": pl.get("creator", {}).get("nickname", ""),
                "tracks": tracks,
                "trackIds": all_ids,
                "code": result.get("code"),
            }

    def get_song_detail(self, song_id):
        data = {
            "c": str(song_id),
            "ids": f"[{song_id}]",
            "csrf_token": self._get_csrf(),
        }
        result = self._weapi(
            "https://music.163.com/weapi/v3/song/detail", data
        )
        songs = result.get("songs", [])
        if songs:
            s = songs[0]
            return {
                "id": s.get("id"),
                "name": s.get("name", ""),
                "artists": " / ".join(
                    a.get("name", "") for a in s.get("ar", [])
                ),
                "album": s.get("al", {}).get("name", ""),
                "cover": s.get("al", {}).get("picUrl", ""),
                "duration": s.get("dt", 0),
            }
        return None

    def play_song(self, song_id):
        urls = [
            f"orpheus://song/{song_id}",
        ]
        if sys.platform == "win32":
            for url in urls:
                try:
                    os.startfile(url)
                    print(f"[NCM] Opening: {url}")
                    return True
                except Exception as e:
                    print(f"[NCM] Failed: {url} -> {e}")
                    continue
        return False

    def open_webpage(self, song_id):
        url = f"https://music.163.com/song?id={song_id}"
        try:
            import webbrowser
            webbrowser.open(url)
            print(f"[NCM] Opening webpage: {url}")
            return True
        except Exception as e:
            print(f"[NCM] Failed to open webpage: {e}")
            return False

    def play_playlist(self, playlist_id):
        url_scheme = f"orpheus://playlist/{playlist_id}"
        if sys.platform == "win32":
            try:
                os.startfile(url_scheme)
                return True
            except Exception:
                pass
        try:
            import webbrowser
            return webbrowser.open(url_scheme)
        except Exception:
            return False

    def logout(self):
        try:
            data = {"csrf_token": self._get_csrf()}
            self._weapi("https://music.163.com/weapi/logout", data)
        except Exception:
            pass
        self.uid = None
        self.nickname = None
        self.session.cookies.clear()
        if os.path.exists(COOKIE_FILE):
            try:
                os.remove(COOKIE_FILE)
            except Exception:
                pass
        print("[NCM] 已退出登录")

    def clear_cookies(self):
        self.uid = None
        self.nickname = None
        self.session.cookies.clear()
        if os.path.exists(COOKIE_FILE):
            try:
                os.remove(COOKIE_FILE)
            except Exception:
                pass
        print("[NCM] Cookie 已清除")


_ncm_instance = None


def get_ncm_api():
    global _ncm_instance
    if _ncm_instance is None:
        _ncm_instance = NCMApi()
    return _ncm_instance
