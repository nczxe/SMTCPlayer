import asyncio
import sys
import platform

# source_app_user_model_id 子串 → 友好名称（大小写不敏感）
FRIENDLY_SOURCE_NAMES = [
    ("spotify", "Spotify"),
    ("netease", "网易云音乐"),
    ("cloudmusic", "网易云音乐"),
    ("qqmusic", "QQ音乐"),
    ("qq.music", "QQ音乐"),
    ("kugou", "酷狗音乐"),
    ("kuwo", "酷我音乐"),
    ("migu", "咪咕音乐"),
    ("foobar2000", "foobar2000"),
    ("potplayer", "PotPlayer"),
    ("vlc", "VLC"),
    ("wmplayer", "Windows Media Player"),
    ("msedge", "Edge 浏览器"),
    ("chrome", "Chrome 浏览器"),
    ("firefox", "Firefox 浏览器"),
]


class SMTCController:
    def __init__(self):
        self._manager = None
        self._session = None
        self._available = False
        self._preferred_source = None   # 用户指定的会话来源（None=自动取第一个）
        self._cover_cache = {}          # source -> {"key", "mime", "data"}；按曲目缓存 SMTC 封面
        self._cover_version = 0         # 封面内容版本（变化时递增，用于 URL 缓存穿透）
        self._last_status = {
            "title": "未检测到媒体",
            "artist": "",
            "album_title": "",
            "status": "stopped",
            "position": 0,
            "duration": 0,
            "is_playing": False,
            "has_previous": False,
            "has_next": False,
            "source": "",
        }
        self._init_smtc()

    def _init_smtc(self):
        if platform.system() != "Windows":
            print("[WARN] 非Windows平台，SMTC不可用，将使用模拟模式")
            self._available = False
            return

        try:
            import winrt.windows.media.control as wmc
            import winrt.windows.foundation  # noqa: F401
            import winrt.windows.foundation.collections  # noqa: F401

            self._wmc = wmc
            self._available = True
            print("[INFO] SMTC 初始化成功")
        except ImportError as e:
            print(f"[WARN] 无法导入 winrt: {e}")
            print("[WARN] 请运行: pip install winrt-Windows.Media.Control winrt-Windows.Foundation winrt-Windows.Foundation.Collections")
            self._available = False

    def _get_session(self):
        if not self._available:
            return None
        try:
            sessions = self._run_async(self._get_sessions_async())
            if not sessions:
                return None
            # 优先返回用户指定的来源；指定来源已消失时回落第一个
            if self._preferred_source:
                for s in sessions:
                    if (s.source_app_user_model_id or "") == self._preferred_source:
                        return s
            return sessions[0]
        except Exception as e:
            print(f"[ERROR] 获取会话失败: {e}")
            return None

    def _run_async(self, coro):
        try:
            asyncio.get_running_loop()
        except RuntimeError:
            return asyncio.run(coro)

        loop = asyncio.new_event_loop()
        try:
            return loop.run_until_complete(coro)
        finally:
            loop.close()

    async def _get_sessions_async(self):
        manager = await self._wmc.GlobalSystemMediaTransportControlsSessionManager.request_async()
        sessions = manager.get_sessions()
        return list(sessions)

    def get_session_source(self):
        if not self._available:
            return ""
        try:
            session = self._get_session()
            if session:
                return session.source_app_user_model_id or ""
        except Exception:
            pass
        return ""

    # ============== 多会话支持 ==============

    @staticmethod
    def get_friendly_name(source):
        """把 source_app_user_model_id 映射为友好名称。"""
        s = (source or "").lower()
        if not s:
            return ""
        for key, name in FRIENDLY_SOURCE_NAMES:
            if key in s:
                return name
        # 回落：取包名最后一段（如 Foo.Bar_xxx → Bar）
        head = s.split("_")[0]
        return head.split(".")[-1].capitalize() if "." in head else (source or "")

    def get_sessions_info(self):
        """枚举所有 SMTC 会话（含曲目快照与播放状态），供 UI 切换来源。"""
        if not self._available:
            return []
        try:
            sessions = self._run_async(self._get_sessions_async())
        except Exception as e:
            print(f"[ERROR] 枚举会话失败: {e}")
            return []

        result = []
        for s in sessions:
            src = s.source_app_user_model_id or ""
            entry = {
                "source": src,
                "name": self.get_friendly_name(src),
                "title": "",
                "artist": "",
                "is_playing": False,
            }
            try:
                info = self._run_async(s.try_get_media_properties_async())
                entry["title"] = info.title or ""
                entry["artist"] = info.artist or ""
            except Exception:
                pass
            try:
                playback = s.get_playback_info()
                entry["is_playing"] = int(playback.playback_status) == 4
            except Exception:
                pass
            result.append(entry)
        return result

    def set_preferred_source(self, source):
        """指定激活会话（空串恢复自动）。"""
        self._preferred_source = (source or "").strip() or None

    def get_preferred_source(self):
        return self._preferred_source or ""

    def get_active_source(self):
        """当前实际使用的会话来源 id。"""
        try:
            session = self._get_session()
            return (session.source_app_user_model_id or "") if session else ""
        except Exception:
            return ""

    # ============== SMTC 封面 ==============

    async def _read_thumbnail(self, ref):
        """读取 RandomAccessStreamReference 封面为 (mime, bytes)；失败/无图返回 None。"""
        if not ref:
            return None
        try:
            import winrt.windows.storage.streams as wss
        except ImportError:
            print("[WARN] 缺少 winrt-Windows.Storage.Streams，无法读取 SMTC 专辑封面")
            return None
        try:
            stream = await ref.open_read_async()
            size = int(stream.size)
            if size <= 0 or size > 3 * 1024 * 1024:
                return None
            reader = wss.DataReader(stream.get_input_stream_at(0))
            await reader.load_async(size)
            raw = None
            try:
                # winrt v2：read_bytes(count) 直接返回 bytes/list
                raw = reader.read_bytes(size)
                if not isinstance(raw, (bytes, bytearray, list)):
                    raw = None
            except (TypeError, ValueError):
                raw = None
            if raw is None:
                # winrt v3：需要传入预分配缓冲区填充
                buf = bytearray(size)
                reader.read_bytes(buf)
                raw = buf
            data = bytes(bytearray(raw))
            mime = (getattr(stream, "content_type", "") or "image/png").split(";")[0].strip()
            if not mime.startswith("image/"):
                mime = "image/png"
            return mime, data
        except Exception as e:
            print(f"[WARN] 读取封面失败: {e}")
            return None

    def get_active_cover(self):
        """当前会话封面 (mime, bytes, version)；无则 None。"""
        src = self.get_active_source()
        cached = self._cover_cache.get(src)
        if cached and cached.get("data"):
            return cached["mime"], cached["data"], self._cover_version
        return None

    def get_status(self):
        if not self._available:
            return self._last_status

        try:
            session = self._get_session()
            if not session:
                self._last_status["status"] = "stopped"
                self._last_status["is_playing"] = False
                self._last_status["title"] = "未检测到媒体播放"
                self._last_status["artist"] = ""
                self._last_status["position"] = 0
                self._last_status["duration"] = 0
                self._last_status["source"] = ""
                return self._last_status

            info = self._run_async(self._get_media_info_async(session))
            timeline = session.get_timeline_properties()
            playback = session.get_playback_info()

            status_map = {
                0: "closed",
                1: "opened",
                2: "changing",
                3: "stopped",
                4: "playing",
                5: "paused",
            }

            position = 0
            duration = 0
            if timeline:
                try:
                    pos = timeline.position
                    position = pos.total_seconds() if callable(pos.total_seconds) else pos.total_seconds
                except Exception:
                    pass
                try:
                    end = timeline.end_time
                    duration = end.total_seconds() if callable(end.total_seconds) else end.total_seconds
                except Exception:
                    pass

            self._last_status = {
                "title": info.get("title", "未知标题"),
                "artist": info.get("artist", "未知艺术家"),
                "album_title": info.get("album_title", ""),
                "status": status_map.get(int(playback.playback_status), "unknown"),
                "position": position,
                "duration": duration,
                "is_playing": int(playback.playback_status) == 4,
                "has_previous": bool(playback.controls.is_previous_enabled),
                "has_next": bool(playback.controls.is_next_enabled),
                "source": info.get("source", ""),
            }
            return self._last_status
        except Exception as e:
            print(f"[ERROR] 获取状态失败: {e}")
            return self._last_status

    async def _get_media_info_async(self, session):
        info = await session.try_get_media_properties_async()

        source = ""
        try:
            source = session.source_app_user_model_id or ""
        except Exception:
            pass

        # 封面按曲目缓存：曲目变化时才重新读取 SMTC 缩略图
        key = (info.title or "", info.artist or "", info.album_title or "")
        cached = self._cover_cache.get(source)
        if cached is None or cached["key"] != key:
            thumb = await self._read_thumbnail(info.thumbnail)
            self._cover_cache[source] = {
                "key": key,
                "mime": thumb[0] if thumb else None,
                "data": thumb[1] if thumb else None,
            }
            self._cover_version += 1

        return {
            "title": info.title or "未知标题",
            "artist": info.artist or "未知艺术家",
            "album_title": info.album_title or "",
            "album_artist": info.album_artist or "",
            "track_number": info.track_number,
            "source": source,
        }

    def play_pause(self):
        if not self._available:
            return False
        try:
            session = self._get_session()
            if not session:
                return False
            if hasattr(session, "try_toggle_play_pause_async"):
                result = self._run_async(session.try_toggle_play_pause_async())
            else:
                result = self._run_async(session.try_play_pause_toggle_async())
            return bool(result)
        except Exception as e:
            print(f"[ERROR] 播放暂停失败: {e}")
            return False

    def play(self):
        if not self._available:
            return False
        try:
            session = self._get_session()
            if not session:
                return False
            return bool(self._run_async(session.try_play_async()))
        except Exception as e:
            print(f"[ERROR] 播放失败: {e}")
            return False

    def pause(self):
        if not self._available:
            return False
        try:
            session = self._get_session()
            if not session:
                return False
            return bool(self._run_async(session.try_pause_async()))
        except Exception as e:
            print(f"[ERROR] 暂停失败: {e}")
            return False

    def next_track(self):
        if not self._available:
            return False
        try:
            session = self._get_session()
            if not session:
                return False
            return bool(self._run_async(session.try_skip_next_async()))
        except Exception as e:
            print(f"[ERROR] 下一首失败: {e}")
            return False

    def previous_track(self):
        if not self._available:
            return False
        try:
            session = self._get_session()
            if not session:
                return False
            return bool(self._run_async(session.try_skip_previous_async()))
        except Exception as e:
            print(f"[ERROR] 上一首失败: {e}")
            return False

    @property
    def available(self):
        return self._available
