import urllib.request
import urllib.error
import json
import time


class NeteaseWatcherClient:
    def __init__(self, host="127.0.0.1", port=3574):
        self.base_url = f"http://{host}:{port}"
        self._available = False
        self._last_data = None
        self._last_fail_time = 0
        self._retry_interval = 5
        self._check_available()

    def _check_available(self, quiet=False):
        was_available = self._available
        try:
            data = self._fetch()
            if data and "music" in data:
                self._available = True
                self._last_data = data
                if not was_available:
                    print(f"[INFO] Netease Watcher 已连接: {self.base_url}")
                return
        except Exception:
            pass
        self._available = False
        if not quiet:
            print(f"[INFO] Netease Watcher 未检测到 (地址: {self.base_url})")

    def _should_try(self):
        if self._available:
            return True
        now = time.time()
        if now - self._last_fail_time >= self._retry_interval:
            return True
        return False

    def _fetch(self):
        if not self._should_try():
            return None
        try:
            req = urllib.request.Request(
                self.base_url,
                headers={"User-Agent": "SMTCPlayer/1.0"},
            )
            with urllib.request.urlopen(req, timeout=1) as resp:
                if resp.status == 200:
                    data = json.loads(resp.read().decode("utf-8"))
                    self._last_data = data
                    if not self._available:
                        self._available = True
                        print(f"[INFO] Netease Watcher 已连接: {self.base_url}")
                    return data
        except (urllib.error.URLError, urllib.error.HTTPError, OSError):
            self._last_fail_time = time.time()
            if self._available:
                self._available = False
                print(f"[WARN] Netease Watcher 连接断开")
        except Exception:
            self._last_fail_time = time.time()
        return None

    def get_status(self):
        data = self._fetch()
        if not data or "music" not in data:
            return None

        music = data["music"]
        # 监视器未锁定进度时 time 为 -1 哨兵：钳为 0，
        # 避免负值经合并逻辑污染 UI 播放进度
        time_val = data.get("time")
        position = float(time_val) if isinstance(time_val, (int, float)) and time_val >= 0 else 0.0
        return {
            "title": music.get("name", ""),
            "artist": ", ".join(music.get("artists", [])) if music.get("artists") else "",
            "album_title": music.get("album", ""),
            "duration": music.get("duration", 0) / 1000.0 if music.get("duration") else 0,
            "position": position,
            "song_id": music.get("id"),
            "thumbnail": music.get("thumbnail", ""),
            "aliases": music.get("aliases", []),
        }

    @property
    def available(self):
        return self._available

    @property
    def last_data(self):
        return self._last_data
