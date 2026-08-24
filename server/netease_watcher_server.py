# -*- coding: utf-8 -*-
"""网易云音乐播放状态监视器（原创 Python 实现，由网易云增强插件托管）。

设计说明
========
对外契约与旧版保持一致：在 127.0.0.1:3574 提供任意路径的 HTTP GET，
返回 ``{"time": <秒>, "music": {...}}``。

数据来自两条独立通道：

1. 播放进度 —— 定位 cloudmusic.exe 进程内播放位置变量（float64，秒）。
   客户端为多进程架构（主窗口/渲染器/迷你播放器各一个 cloudmusic.exe）：
   主窗口关闭时播放时钟由 UI 进程（迷你播放器等）维护，主进程内的
   同名全局不再推进，因此必须对全部进程逐一尝试，而非只扫第一个。
   静态定位优先（在 cloudmusic.dll 指令段中搜索触碰 [rip+disp32] f64
   全局的 movsd/movq/x87 指令，解出候选全局地址后做短窗口斜率验证），
   失败回落"多轮差分收敛"扫描：
     * 扫描范围 = cloudmusic.dll 全部映射段 ∪ 进程私有可写区域（堆等）；
     * 每轮对全区域做一次 8 字节对齐快照，仅保留取值像"秒数"的地址；
     * 相邻两轮求差：前进量落在合理播放速率区间（或大幅回退=切歌）则保留，
       其余淘汰；若干轮后自然收敛到极少数候选；
     * 对最终候选按 |斜率 − 1| 打分并结合已知时长择优锁定。
   行为扫描不依赖任何指令特征码，天然兼容客户端版本更新。
2. 歌曲元数据 —— 监听本地媒体库 webdb.dat 的修改时间，读取 historyTracks
   表最新一条记录的 jsonStr 字段解析出曲名 / 歌手 / 专辑 / 封面等信息。

已知取舍
========
* 未实现旧版通过窗口钩子维持最小化刷新的逻辑；客户端长时间最小化时
  进度可能停止更新，恢复前台后自动继续。
* webdb.dat 采用 mtime 轮询而非目录变更通知，元数据延迟约半秒。
"""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import struct
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Optional

# ============== 常量配置 ==============

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 3574

PROCESS_NAME = "cloudmusic.exe"
MODULE_NAME = "cloudmusic.dll"

# 进度值合理性范围（秒）：覆盖超长播客，排除明显的指针碎片
VALUE_MIN = 0.05
VALUE_MAX = 86_400.0

SCAN_CHUNK = 1 << 20           # 内存分块读取大小（1 MiB）
ROUND_INTERVAL = 0.7           # 差分轮间隔（秒）
NARROW_ROUNDS = 8              # 收敛轮数上限
STEP_FORWARD_MAX = 2.6         # 单轮最大合理前跳（秒）
BIG_BACK_RESET = 15.0          # 视为"切歌重置"的最小回退量（秒），允许保留
LOCK_POLL_INTERVAL = 0.15      # 锁定地址后的读取间隔（秒）
BAD_READ_LIMIT = 3             # 连续异常读次数阈值，超过则重新扫描
WATCHDOG_WINDOW = 14           # 锁定后滑动窗口样本数（约 2 秒）
SLOPE_TOLERANCE = 0.45         # 斜率偏离 1 的容忍度
RESCAN_COOLDOWN = 2.5          # 扫描失败后的重试间隔（秒）

DB_POLL_INTERVAL = 0.35

# 冻结环境下 stdout 重定向到管道时默认块缓冲且使用本地 ANSI 编码：
# 宿主（NeteaseEnhance 插件）以 UTF-8 读取子进程输出，若不统一编码，
# 中文诊断信息会变成乱码写进日志；此处强制行缓冲 + UTF-8。
# 直连控制台调试如需本地编码显示，可设 PYTHONIOENCODING 覆盖。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    sys.stderr.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
except Exception:
    pass


def _local_appdata() -> str:
    return os.environ.get("LOCALAPPDATA",
                          os.path.join(os.path.expanduser("~"), "AppData", "Local"))


def music_db_path() -> str:
    """网易云媒体库数据库路径。"""
    return os.path.join(_local_appdata(), "NetEase", "CloudMusic", "Library", "webdb.dat")


# ============== 进程内存读取 ==============

class MemoryReader:
    """对目标进程只读访问的最小封装（OpenProcess + ReadProcessMemory + VirtualQueryEx）。"""

    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    PROCESS_VM_READ = 0x0010

    MEM_COMMIT = 0x1000
    MEM_PRIVATE = 0x40000
    MEM_MAPPED = 0x40000 << 1      # 0x80000
    MEM_IMAGE = 0x1000000
    PAGE_READWRITE = 0x04
    PAGE_WRITECOPY = 0x08
    PAGE_EXECUTE_READWRITE = 0x40

    def __init__(self, pid: int):
        import ctypes
        import ctypes.wintypes

        self._ctypes = ctypes
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.OpenProcess.restype = ctypes.wintypes.HANDLE
        k32.OpenProcess.argtypes = [ctypes.wintypes.DWORD, ctypes.wintypes.BOOL,
                                    ctypes.wintypes.DWORD]
        k32.ReadProcessMemory.restype = ctypes.wintypes.BOOL
        k32.ReadProcessMemory.argtypes = [ctypes.wintypes.HANDLE, ctypes.c_void_p,
                                          ctypes.c_void_p, ctypes.c_size_t,
                                          ctypes.POINTER(ctypes.c_size_t)]
        k32.VirtualQueryEx.restype = ctypes.c_size_t
        k32.VirtualQueryEx.argtypes = [ctypes.wintypes.HANDLE, ctypes.c_void_p,
                                       ctypes.c_void_p, ctypes.c_size_t]
        self._k32 = k32
        access = self.PROCESS_QUERY_LIMITED_INFORMATION | self.PROCESS_VM_READ
        self.handle = k32.OpenProcess(access, False, pid)
        if not self.handle:
            raise OSError(f"OpenProcess({pid}) 失败, winerror={ctypes.get_last_error()}")

    def read(self, address: int, size: int) -> Optional[bytes]:
        buf = self._ctypes.create_string_buffer(size)
        got = self._ctypes.c_size_t(0)
        ok = self._k32.ReadProcessMemory(self.handle, self._ctypes.c_void_p(address),
                                         buf, size, self._ctypes.byref(got))
        if not ok or got.value != size:
            return None
        return buf.raw

    def read_f64(self, address: int) -> Optional[float]:
        raw = self.read(address, 8)
        if raw is None:
            return None
        return struct.unpack("<d", raw)[0]

    def read_u32(self, address: int) -> Optional[int]:
        raw = self.read(address, 4)
        if raw is None:
            return None
        return struct.unpack("<I", raw)[0]

    def module_base_by_name(self, dll_name_lower: str) -> Optional[int]:
        """用 PSAPI 枚举目标进程模块，返回指定 dll 的基址。
        比 psutil.memory_maps 可靠：不依赖映射路径格式，权限要求相同。"""
        import ctypes
        import ctypes.wintypes

        k32 = self._k32
        enum_fn = getattr(k32, "K32EnumProcessModulesEx", None)
        name_fn = getattr(k32, "K32GetModuleFileNameExW", None)
        if enum_fn is None or name_fn is None:
            return None

        LIST_MODULES_ALL = 0x03
        arr = (ctypes.wintypes.HMODULE * 4096)()
        needed = ctypes.wintypes.DWORD()
        enum_fn.restype = ctypes.wintypes.BOOL
        enum_fn.argtypes = [ctypes.wintypes.HANDLE, ctypes.POINTER(ctypes.wintypes.HMODULE),
                            ctypes.wintypes.DWORD, ctypes.POINTER(ctypes.wintypes.DWORD),
                            ctypes.wintypes.DWORD]
        name_fn.restype = ctypes.wintypes.DWORD
        name_fn.argtypes = [ctypes.wintypes.HANDLE, ctypes.wintypes.HMODULE,
                            ctypes.c_wchar_p, ctypes.wintypes.DWORD]

        if not enum_fn(self.handle, arr, ctypes.sizeof(arr), ctypes.byref(needed),
                       LIST_MODULES_ALL):
            return None
        count = needed.value // ctypes.sizeof(ctypes.wintypes.HMODULE)
        buf = ctypes.create_unicode_buffer(1024)
        for i in range(min(count, 4096)):
            n = name_fn(self.handle, arr[i], buf, 1024)
            if n and buf.value.lower().endswith(dll_name_lower):
                return int(arr[i])
        return None

    def walk_regions(self, max_addr: int = 0x7FFFFFFEFFFF) -> list:
        """VirtualQueryEx 枚举已提交区域，返回 [(start, size, protect, type)]。"""
        import ctypes

        class MBI(ctypes.Structure):
            _fields_ = [("BaseAddress", ctypes.c_void_p),
                        ("AllocationBase", ctypes.c_void_p),
                        ("AllocationProtect", ctypes.c_uint32),
                        ("_pad", ctypes.c_uint32),
                        ("RegionSize", ctypes.c_size_t),
                        ("State", ctypes.c_uint32),
                        ("Protect", ctypes.c_uint32),
                        ("Type", ctypes.c_uint32)]

        out = []
        addr = 0x10000
        mbi = MBI()
        while addr < max_addr:
            got = self._k32.VirtualQueryEx(self.handle, ctypes.c_void_p(addr),
                                           ctypes.byref(mbi), ctypes.sizeof(mbi))
            if not got:
                break
            size = mbi.RegionSize
            if mbi.State == self.MEM_COMMIT and size > 0:
                out.append((mbi.BaseAddress or addr, size, mbi.Protect, mbi.Type))
            addr += size if size > 0 else 0x1000
        return out

    def close(self):
        try:
            self._k32.CloseHandle(self.handle)
        except Exception:
            pass


def pe_image_size(reader: "MemoryReader", image_base: int) -> int:
    """读取 PE 头中的 SizeOfImage。
    偏移：e_lfanew + 4(DOS 签名后即 PE 签名) + 20(文件头) + 56(可选头内偏移) = e_lfanew+0x50。"""
    e_lfanew = reader.read_u32(image_base + 0x3C)
    if not e_lfanew or e_lfanew > 0x1000:
        return 0
    return reader.read_u32(image_base + e_lfanew + 0x50) or 0


# ============== 静态定位：扫描 movsd 存储指令推导全局 f64 地址 ==============

# movsd [rip+disp32], xmm0-7 的机器码形态：F2 0F 11 /r 且 mod=00、rm=101
_MOVSD_STORE = __import__("re").compile(
    rb"\xf2\x0f\x11[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]....", __import__("re").S)

EXEC_PROTECTS = None  # 延迟初始化


# movsd/movq/x87 访问 [rip+disp32] f64 全局的机器码形态族：
#   F2 0F 11 /r        movsd m64, xmm（存储）
#   66 0F D6 /r        movq  m64, xmm（存储）
#   DD xx              x87 FLD/FSTP/FIST 等（加载或写回）
# 均要求 mod=00、rm=101（RIP 相对寻址），reg 字段任意
_STATIC_F64_PATTERNS = None


def _static_patterns():
    global _STATIC_F64_PATTERNS
    if _STATIC_F64_PATTERNS is None:
        import re as _re
        modrm_rip = rb"[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]"
        _STATIC_F64_PATTERNS = [
            _re.compile(rb"\xf2\x0f\x11" + modrm_rip + rb"(....)", _re.S),
            _re.compile(rb"\x66\x0f\xd6" + modrm_rip + rb"(....)", _re.S),
            _re.compile(rb"\xdd" + modrm_rip + rb"(....)", _re.S),
        ]
    return _STATIC_F64_PATTERNS


# 成对存储指纹：MOVSD [rip+d],XMM7 紧跟 MOVSD [rip+d],XMM6 ——
# 客户端回写 {进度, 时长} 双全局的唯一写入点（思路参考，正则与解析自研）
_PAIRED_MOVSD = None


def _paired_pattern():
    global _PAIRED_MOVSD
    if _PAIRED_MOVSD is None:
        import re as _re
        _PAIRED_MOVSD = _re.compile(
            rb"\xf2\x0f\x11\x3d(....)\xf2\x0f\x11\x35(....)", _re.S)
    return _PAIRED_MOVSD


def find_paired_position_globals(reader: "MemoryReader", image_base: int,
                                 image_size: int) -> list:
    """定位成对 movsd 写入的两个全局地址，返回 [(xmm7_addr, xmm6_addr), ...]。"""
    import re as _re
    pat = _paired_pattern()
    pairs = []
    img_end = image_base + image_size
    EXEC = {MemoryReader.PAGE_EXECUTE_READWRITE,
            getattr(MemoryReader, "PAGE_EXECUTE_READ", 0x20),
            getattr(MemoryReader, "PAGE_EXECUTE", 0x10)}

    for start, size, protect, mtype in reader.walk_regions():
        if not (image_base <= start < img_end) or protect not in EXEC:
            continue
        pos = start
        end = min(start + size, img_end)
        carry = b""
        carry_base = pos
        while pos < end:
            chunk = min(SCAN_CHUNK, end - pos)
            raw = reader.read(pos, chunk)
            if raw is None:
                pos += chunk
                carry = b""
                carry_base = pos
                continue
            buf = carry + raw
            buf_base = carry_base
            for m in pat.finditer(buf):
                d7 = struct.unpack("<i", m.group(1))[0]
                d6 = struct.unpack("<i", m.group(2))[0]
                t7 = buf_base + m.start() + 8 + d7
                t6 = buf_base + m.start() + 16 + d6
                if image_base <= t7 < img_end and image_base <= t6 < img_end:
                    pairs.append((t7, t6))
            keep = min(len(buf), 15)
            carry = buf[-keep:]
            carry_base = buf_base + len(buf) - keep
            pos += chunk
    return pairs


def slope_lock(cands: list, state: dict, stop: threading.Event,
               tag: str) -> Optional[tuple]:
    """对候选 [(reader, addr), ...] 做短窗口斜率验证：
    采样若干轮，斜率接近 1 且确在推进的地址即为播放时钟。
    返回命中的 (reader, addr)；播放暂停（无推进样本）时返回 None。"""
    prev = {}
    for r, a in cands:
        v = r.read_f64(a)
        # 允许 0 附近（歌曲起始）与 -1 哨兵（客户端未初始化）
        if v is not None and (-0.5 <= v <= VALUE_MAX or v == -1.0):
            prev[(r, a)] = 0.0 if v == -1.0 else v
    print(f"[Watcher] {tag}候选 {len(cands)} 个，其中 {len(prev)} 个取值合理")
    if not prev:
        return None

    fwd = {k: 0.0 for k in prev}
    span = {k: 0.0 for k in prev}
    t_prev = time.monotonic()
    for rnd in range(8):
        stop_event_or_delay(0.35)
        t_now = time.monotonic()
        dt = max(t_now - t_prev, 1e-3)
        t_prev = t_now
        for k in list(prev):
            v = k[0].read_f64(k[1])
            if v is None:
                prev.pop(k)
                continue
            d = v - prev[k]
            if 0.02 <= d <= fwd_max_for(dt):
                fwd[k] += d
                span[k] += dt
            prev[k] = v
        # 有候选累计推进满 1 秒且斜率达标即提前锁定
        for k in prev:
            if span[k] >= 1.0:
                slope = fwd[k] / span[k]
                if 0.85 <= slope <= 1.2:
                    print(f"[Watcher] 已锁定({tag}) 0x{k[1]:X} (斜率 {slope:.3f})")
                    return k
        if rnd in (0, 3, 7):
            movers = sum(1 for k in prev if fwd[k] > 0)
            print(f"[Watcher] {tag}验证第 {rnd + 1}/8 轮：存活 {len(prev)}，推进中 {movers}")
        if not prev:
            return None

    best, err = None, 1e9
    for k in prev:
        if span[k] <= 0:
            continue
        s = fwd[k] / span[k]
        e = abs(s - 1.0)
        if e < err:
            best, err = k, e
    if best is not None and err <= 0.2:
        print(f"[Watcher] 已锁定({tag}终选) 0x{best[1]:X} (斜率 {fwd[best] / span[best]:.3f})")
        return best
    print(f"[Watcher] {tag}验证未通过")
    return None


def find_static_f64_stores(reader: "MemoryReader", image_base: int,
                           image_size: int) -> list:
    """扫描镜像可执行段中触碰 [rip+disp32] f64 全局的指令，返回目标地址列表。

    思路来源：播放位置由客户端代码以双精度形式周期性读写；
    用指令的 RIP 相对寻址可直接解出这些全局变量的确定地址，
    从而把候选集从数十万内存值缩小到指令级的小集合。
    """
    import re as _re
    pats = _static_patterns()
    targets = set()
    img_end = image_base + image_size

    for start, size, protect, mtype in reader.walk_regions():
        if not (image_base <= start < img_end):
            continue
        EXEC = {MemoryReader.PAGE_EXECUTE_READWRITE,
                getattr(MemoryReader, "PAGE_EXECUTE_READ", 0x20),
                getattr(MemoryReader, "PAGE_EXECUTE", 0x10)}
        if protect not in EXEC:
            continue
        pos = start
        end = min(start + size, img_end)
        overlap = max(p.pattern.count(b"(....)") * 0 + 7 for p in pats)
        carry = b""
        carry_base = pos
        while pos < end:
            chunk = min(SCAN_CHUNK, end - pos)
            raw = reader.read(pos, chunk)
            if raw is None:
                pos += chunk
                carry = b""
                carry_base = pos
                continue
            buf = carry + raw
            buf_base = carry_base
            for pat in pats:
                for m in pat.finditer(buf):
                    off = m.start()
                    instr_len = m.end() - m.start()
                    disp = struct.unpack("<i", m.group(1))[0]
                    tgt = buf_base + off + instr_len + disp
                    if image_base <= tgt < img_end:
                        targets.add(tgt)
            # 保留尾部可能被截断的指令字节供下一块拼接
            keep = min(len(buf), overlap)
            carry = buf[-keep:]
            carry_base = buf_base + len(buf) - keep
            pos += chunk

    return sorted(targets)


def fwd_max_for(dt: float) -> float:
    return dt * 1.6 + 0.75


def find_cloudmusic_processes() -> list:
    """找到所有加载了 cloudmusic.dll 的进程，返回
    [(pid, MemoryReader, 待扫描区域列表, 镜像基址, 镜像大小), ...]。

    客户端为多进程架构：主窗口关闭后，播放进度时钟由 UI 进程
    （迷你播放器等）维护，主进程内的同名全局不再推进——曾导致
    只扫第一个进程时差分扫描永远找不到走动的时钟。
    模块基址通过 PSAPI EnumProcessModulesEx 直接枚举——
    psutil.memory_maps 在部分进程/会话上会失败或缺失 dll 条目，
    曾导致挂错辅助进程、静态路径整体失效。
      区域 = [镜像基址, 基址+SizeOfImage) ∪ 全部可写区域（堆等）。
    """
    try:
        import psutil
    except ImportError:
        print("[Watcher] 缺少 psutil，无法定位进程")
        return []

    out = []
    for p in psutil.process_iter(["pid", "name"]):
        if (p.info.get("name") or "").lower() != PROCESS_NAME:
            continue
        pid = p.info["pid"]
        try:
            reader = MemoryReader(pid)
        except Exception as ex:
            print(f"[Watcher] 打开进程 {pid} 失败: {ex}")
            continue

        # 1) PSAPI 枚举模块拿 cloudmusic.dll 基址；无该模块的辅助进程直接跳过
        image_base = reader.module_base_by_name(MODULE_NAME)
        if not image_base:
            reader.close()
            continue

        img_size = pe_image_size(reader, image_base)
        if not img_size:
            print(f"[Watcher] PE 头解析失败 (PID {pid})")
            reader.close()
            continue

        # 2) VirtualQueryEx 枚举区域
        WRITABLE = {MemoryReader.PAGE_READWRITE,
                    MemoryReader.PAGE_WRITECOPY,
                    MemoryReader.PAGE_EXECUTE_READWRITE}
        dll_span_end = None
        writable_regions = []  # [(start, end)]
        try:
            for start, size, protect, mtype in reader.walk_regions():
                # 镜像首段恰好起始于基址，须用闭区间判断
                if dll_span_end is None and start <= image_base < start + size:
                    dll_span_end = image_base + img_size
                if protect in WRITABLE and mtype != MemoryReader.MEM_IMAGE:
                    # 可写区域（私有堆 / 写时复制映射等）
                    if size <= (512 << 20):  # 跳过病态大区
                        writable_regions.append((start, start + size))
        except Exception as ex:
            print(f"[Watcher] 区域枚举失败 (PID {pid}): {ex}")
            reader.close()
            continue

        regions = []
        if dll_span_end:
            regions.append((image_base, dll_span_end))
        regions.extend(writable_regions)
        if not regions:
            reader.close()
            continue

        out.append((pid, reader, regions, image_base, img_size))
    return out


# ============== 快照（numpy 向量化过滤） ==============

def _filter_chunk(raw: bytes, base: int, restrict_sorted: Optional["object"],
                  out: dict, limit: float):
    """单个内存块的向量化解析：取值合法的地址写入 out。
    restrict_sorted 非 None 时（升序 numpy 数组）额外要求地址命中候选集。"""
    import numpy as np
    usable = len(raw) - (len(raw) % 8)
    if usable <= 0:
        return
    arr = np.frombuffer(raw[:usable], dtype="<f8")
    mask = (arr > VALUE_MIN) & (arr <= limit) & np.isfinite(arr)
    idx = np.nonzero(mask)[0]
    if idx.size == 0:
        return
    if restrict_sorted is not None:
        addrs = base + idx * 8
        hit = np.isin(addrs, restrict_sorted)
        idx = idx[hit]
        if idx.size == 0:
            return
    for i in idx:
        out[base + int(i) * 8] = float(arr[int(i)])


def _snapshot_regions(reader: MemoryReader, regions: list,
                      vmax: float = VALUE_MAX) -> dict:
    """初始全量快照：扫描全部区域，收集取值像秒数的 8 字节对齐地址。"""
    out = {}
    limit = min(VALUE_MAX, vmax)
    for start, end in regions:
        pos = start
        while pos + 8 <= end:
            chunk = min(SCAN_CHUNK, end - pos)
            raw = reader.read(pos, chunk)
            if raw is None:
                pos += chunk
                continue
            _filter_chunk(raw, pos, None, out, limit)
            pos += chunk
    return out


def _snapshot_restrict(reader: MemoryReader, regions: list, restrict,
                       vmax: float = VALUE_MAX) -> dict:
    """复检快照：全区域批量扫描 + 向量化命中候选集。"""
    import numpy as np
    out = {}
    limit = min(VALUE_MAX, vmax)
    rs = np.fromiter(restrict, dtype=np.uint64)
    rs.sort()
    for start, end in regions:
        pos = start
        while pos + 8 <= end:
            chunk = min(SCAN_CHUNK, end - pos)
            raw = reader.read(pos, chunk)
            if raw is None:
                pos += chunk
                continue
            _filter_chunk(raw, pos, rs, out, limit)
            pos += chunk
    return out


def narrow_by_diff_rounds(reader: MemoryReader, regions: list,
                          duration_hint: Optional[float]) -> Optional[int]:
    """多轮差分收敛：返回最像播放位置的地址；无法收敛返回 None。"""
    # 时长已知时同步收紧取值上限（下一首更长则由重扫机制自然覆盖）
    vmax = min(VALUE_MAX, duration_hint * 1.25 + 30.0) if duration_hint and duration_hint > 20 else VALUE_MAX

    t0 = time.monotonic()
    prev = _snapshot_regions(reader, regions, vmax=vmax)
    if not prev:
        print(f"[Watcher] 区域内未发现候选")
        return None
    print(f"[Watcher] 初始候选 {len(prev)} 个（区域 "
          f"{sum(e - s for s, e in regions) / (1 << 20):.0f}MB，"
          f"快照耗时 {time.monotonic() - t0:.1f}s），开始差分收敛...")

    ever_forward = {}   # addr -> 累计正增量
    moving_time = {}    # addr -> 处于"前进样本"的累计真实时长（斜率分母）
    static_span = {}    # addr -> 连续微抖/静止时长（超阈值淘汰）
    t_prev = time.monotonic()
    t_budget_start = t_prev
    for rnd in range(NARROW_ROUNDS):
        stop_wait = stop_event_or_delay(ROUND_INTERVAL)
        t_now = time.monotonic()
        dt = max(t_now - t_prev, 1e-3)
        t_prev = t_now

        # 阈值随实测轮间隔动态缩放：轮次无论快慢，真时钟 d≈dt 都能存活
        fwd_max = dt * 1.6 + 0.75
        jitter = max(0.01, dt * 0.02)

        cur = _snapshot_restrict(reader, regions, prev, vmax=vmax)

        survived = {}
        for addr, pv in prev.items():
            cv = cur.get(addr)
            if cv is None:
                continue
            d = cv - pv
            if d > fwd_max:
                continue
            if d <= -BIG_BACK_RESET:
                survived[addr] = cv
                ever_forward[addr] = 0.0
                moving_time[addr] = 0.0
                static_span[addr] = 0.0
                continue
            if d < jitter:
                # 微抖/静止：累计超过 ~2.5s 判定为非时钟变量淘汰
                s = static_span.get(addr, 0.0) + dt
                if s >= 2.5:
                    continue
                static_span[addr] = s
                survived[addr] = cv
                continue
            # 明确前进样本
            static_span[addr] = 0.0
            survived[addr] = cv
            ever_forward[addr] = ever_forward.get(addr, 0.0) + d
            moving_time[addr] = moving_time.get(addr, 0.0) + dt
        prev = survived

        movers = sum(1 for a in prev if moving_time.get(a, 0.0) > 0)
        if movers > 0 or rnd == 0 or rnd == NARROW_ROUNDS - 1:
            print(f"[Watcher] 第 {rnd + 1}/{NARROW_ROUNDS} 轮：存活 {len(prev)}，推进中 {movers}")

        if rnd >= 3 and movers > 0 and len(prev) <= 60:
            print(f"[Watcher] 第 {rnd + 1} 轮提前收敛")
            break

        if rnd >= 2 and movers == 0 and len(prev) > 4:
            print("[Watcher] 全程静止（播放未开始或已暂停），等待恢复播放后重试")
            return None

        if time.monotonic() - t_budget_start > 26:
            print("[Watcher] 达到单轮扫描时间预算，使用当前候选提前终选")
            break

        if not prev:
            break

    if not prev:
        return None

    # 终选：只接受斜率真正接近 1 的地址（宁可返回 None 重扫，也不锁慢速杂项）
    best_addr, best_score = None, 1e9
    for addr in prev:
        forward = ever_forward.get(addr, 0.0)
        span_t = moving_time.get(addr, 0.0)
        if forward <= 0 or span_t <= 0:
            continue
        slope = forward / span_t
        if not (0.85 <= slope <= 1.18):
            continue  # 硬性门槛：斜率必须像真实播放时钟
        score = abs(slope - 1.0)
        if duration_hint and duration_hint > 0:
            ratio = prev[addr] / duration_hint
            if ratio > 1.25:
                score += 10.0
        if score < best_score:
            best_addr, best_score = addr, score
    if best_addr is None:
        print("[Watcher] 无斜率达标的候选（窗口内可能发生 seek/暂停），本轮放弃")
        return None
    print(f"[Watcher] 已锁定进度地址 0x{best_addr:X} "
          f"(斜率 {ever_forward[best_addr] / moving_time[best_addr]:.3f})")
    return best_addr


def stop_event_or_delay(delay: float):
    """预留的协作式等待点（当前无取消源，简单休眠）。"""
    time.sleep(delay)


def scan_position_forever(state: dict, stop: threading.Event):
    """后台线程：静态定位优先（指令级，秒锁），失败回落行为差分扫描。
    依次尝试所有 cloudmusic.exe 进程——播放时钟可能在其中任意一个。"""
    diff_rr = 0  # 路径 B 的进程轮换游标

    while not stop.is_set():
        procs = find_cloudmusic_processes()
        if not procs:
            stop.wait(RESCAN_COOLDOWN)
            continue
        with state["lock"]:
            hint = state.get("duration_sec") or None
        print(f"[Watcher] 已定位 {PROCESS_NAME} (PID "
              f"{', '.join(str(p[0]) for p in procs)})，"
              f"候选进程 {len(procs)} 个...")
        try:
            locked = None

            # ---- 路径 A1：成对 movsd 指纹（最快最准；跨进程合并候选一次验证）----
            pair_cands = []
            for pid, r, _regions, base, img_size in procs:
                try:
                    t0 = time.monotonic()
                    pairs = find_paired_position_globals(r, base, img_size)
                    if pairs:
                        print(f"[Watcher] PID {pid} 成对 movsd 指纹命中 {len(pairs)} 处 "
                              f"(镜像 {img_size >> 20}MB, 扫描 {time.monotonic() - t0:.1f}s)")
                        pair_cands.extend((r, t) for t7, t6 in pairs for t in (t7, t6))
                except Exception as ex:
                    print(f"[Watcher] PID {pid} 成对指纹异常: {ex}")
            if pair_cands:
                locked = slope_lock(pair_cands, state, stop, tag="成对")

            # ---- 路径 A2：静态指令定位（次选，同样跨进程合并）----
            if locked is None:
                static_cands = []
                for pid, r, _regions, base, img_size in procs:
                    try:
                        t0 = time.monotonic()
                        statics = find_static_f64_stores(r, base, img_size)
                        if statics:
                            print(f"[Watcher] PID {pid} movsd 静态候选 {len(statics)} 个 "
                                  f"(扫描 {time.monotonic() - t0:.1f}s)")
                            static_cands.extend((r, t) for t in statics)
                    except Exception as ex:
                        print(f"[Watcher] PID {pid} 静态定位异常: {ex}")
                if static_cands:
                    locked = slope_lock(static_cands, state, stop, tag="静态")

            # ---- 路径 B：行为差分扫描（回退；每次轮换一个进程省时）----
            if locked is None:
                pid, r, regions, _base, _size = procs[diff_rr % len(procs)]
                diff_rr += 1
                print(f"[Watcher] PID {pid} 转行为差分扫描")
                addr = narrow_by_diff_rounds(r, regions, hint)
                if addr is not None:
                    locked = (r, addr)

            if locked is None:
                print("[Watcher] 本轮未收敛（可能全程暂停或曲目静止），稍后重试")
                stop.wait(RESCAN_COOLDOWN)
                continue

            reader, addr = locked
            poll_locked_address(reader, addr, state, stop)
            print("[Watcher] 进度地址失效，准备重新扫描")
        finally:
            for _, r, *_ in procs:
                r.close()


def poll_locked_address(reader: MemoryReader, addr: int, state: dict,
                        stop: threading.Event):
    """锁定后的小间隔读取循环；连续异常或行为不符则放弃该地址触发重扫。"""
    bad = 0
    last = None
    window = []  # (t, v) 滑动窗口用于斜率看门狗
    suspicious_windows = 0

    while not stop.is_set():
        time.sleep(LOCK_POLL_INTERVAL)
        v = reader.read_f64(addr)
        now = time.monotonic()

        if v is None or v < 0 or v > VALUE_MAX:
            bad += 1
            if bad >= BAD_READ_LIMIT:
                return
            continue

        with state["lock"]:
            dur = state.get("duration_sec") or 0.0
        cap = dur * 2.0 + 120.0 if dur > 0 else VALUE_MAX
        if v > cap:
            bad += 1
            continue
        bad = 0

        if v != last:
            with state["lock"]:
                state["time"] = round(v, 3)
            last = v

        # ---- 看门狗：确认锁定的确是播放时钟而非其他计时器 ----
        window.append((now, v))
        if len(window) > WATCHDOG_WINDOW:
            window.pop(0)
        if len(window) == WATCHDOG_WINDOW:
            dt_total = window[-1][0] - window[0][0]
            dv_total = window[-1][1] - window[0][1]
            forwards = [window[i][1] - window[i - 1][1]
                        for i in range(1, len(window))]
            has_forward = any(d > 0.05 for d in forwards)
            slope = dv_total / dt_total if dt_total > 0 else 0.0
            if has_forward and abs(slope - 1.0) > SLOPE_TOLERANCE:
                suspicious_windows += 1
                if suspicious_windows >= 2:
                    print(f"[Watcher] 斜率 {slope:.2f} 持续偏离 1.0，判定误锁，重扫")
                    return
            else:
                suspicious_windows = 0


# ============== 歌曲元数据通道 ==============

class MusicMonitor(threading.Thread):
    """监听 webdb.dat 变化并解析最新一条播放记录。"""

    def __init__(self, state: dict, stop: threading.Event):
        super().__init__(daemon=True, name="watcher-music")
        self.state = state
        self.stop_evt = stop
        self._conn = None
        self._mtime = None

    def _connect(self) -> Optional[sqlite3.Connection]:
        path = music_db_path()
        if not os.path.exists(path):
            return None
        try:
            conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True, timeout=0.3)
            conn.execute("PRAGMA query_only = ON")
            return conn
        except sqlite3.Error as ex:
            print(f"[Watcher] 打开媒体库失败: {ex}")
            return None

    @staticmethod
    def _query_latest(conn: sqlite3.Connection) -> Optional[dict]:
        row = conn.execute(
            "SELECT jsonStr FROM historyTracks ORDER BY playtime DESC LIMIT 1"
        ).fetchone()
        if not row or not row[0]:
            return None
        try:
            j = json.loads(row[0])
        except (ValueError, TypeError):
            return None
        album = j.get("album") or {}
        artists = [a.get("name", "") for a in (j.get("artists") or []) if isinstance(a, dict)]
        aliases = [a for a in (j.get("alias") or []) if isinstance(a, str)]
        duration_ms = j.get("duration") or 0
        try:
            song_id = int(str(j.get("id") or 0))
        except ValueError:
            song_id = 0
        return {
            "id": song_id,
            "name": j.get("name") or "",
            "artists": artists,
            "album": album.get("name") or "",
            "thumbnail": album.get("picUrl") or "",
            "duration": int(duration_ms),
            "aliases": aliases,
        }

    def _publish(self, music: Optional[dict]):
        with self.state["lock"]:
            changed = music != self.state.get("music")
            self.state["music"] = music
            if music:
                self.state["duration_sec"] = music["duration"] / 1000.0
        if changed and music:
            print(f"[Watcher] 曲目变更: {music['name']} - {', '.join(music['artists'])}")

    def run(self):
        while not self.stop_evt.is_set():
            path = music_db_path()
            try:
                mtime = os.stat(path).st_mtime if os.path.exists(path) else None
            except OSError:
                mtime = None

            if mtime is None:
                self._publish(None)
                if self._conn:
                    self._conn.close()
                    self._conn = None
                self.stop_evt.wait(2.0)
                continue

            if self._conn is None:
                self._conn = self._connect()

            if mtime != self._mtime and self._conn is not None:
                self._mtime = mtime
                time.sleep(0.15)  # 等待写入方提交完成
                try:
                    self._publish(self._query_latest(self._conn))
                except sqlite3.Error:
                    # 数据库写入中偶发锁定，下一轮自动恢复
                    self._conn.close()
                    self._conn = None
            self.stop_evt.wait(DB_POLL_INTERVAL)


# ============== HTTP 服务 ==============

def make_handler(state: dict):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802 —— 任意路径均返回当前状态
            with state["lock"]:
                payload = {"time": state.get("time", -1.0)}
                music = state.get("music")
                if music is not None:
                    payload["music"] = music
            body = json.dumps(payload).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, fmt, *args):  # 静默访问日志
            pass

    return Handler


# ============== 入口 ==============

def start(host: str = DEFAULT_HOST, port: int = DEFAULT_PORT,
          stop: Optional[threading.Event] = None) -> threading.Event:
    """启动监视器（非阻塞）。返回停止信号 Event。

    端口被占用时先探测既有服务：若已是有效的监视器实例则静默待机，
    避免与其他副本或历史残留进程冲突。
    """
    stop = stop or threading.Event()

    if not _port_bindable(host, port):
        if _looks_like_watcher(host, port):
            print(f"[Watcher] 检测到 {host}:{port} 已有监视器实例在运行，本进程进入待机")
            return stop
        raise OSError(f"端口 {host}:{port} 被其他程序占用")

    state = {"lock": threading.Lock(), "time": -1.0, "music": None, "duration_sec": 0.0}

    threading.Thread(target=scan_position_forever, args=(state, stop),
                     daemon=True, name="watcher-position").start()
    MusicMonitor(state, stop).start()

    server = ThreadingHTTPServer((host, port), make_handler(state))
    server.daemon_threads = True
    threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.5},
                     daemon=True, name="watcher-http").start()
    state["server"] = server
    print(f"[Watcher] HTTP 服务已启动 http://{host}:{port}")
    return stop


def _port_bindable(host: str, port: int) -> bool:
    import socket
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            s.bind((host, port))
            return True
        except OSError:
            return False


def _looks_like_watcher(host: str, port: int) -> bool:
    try:
        from urllib.request import urlopen
        with urlopen(f"http://{host}:{port}/", timeout=1.5) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            return isinstance(data, dict) and "time" in data
    except Exception:
        return False


def main():
    parser = argparse.ArgumentParser(description="SMTC Player 内置网易云状态监视器")
    parser.add_argument("--host", default=os.environ.get("HOST", DEFAULT_HOST),
                        help="监听地址（默认 127.0.0.1，可用环境变量 HOST 覆盖）")
    parser.add_argument("--port", type=int,
                        default=int(os.environ.get("PORT") or DEFAULT_PORT),
                        help="监听端口（默认 3574，可用环境变量 PORT 覆盖）")
    args = parser.parse_args()

    stop = start(args.host, args.port)
    try:
        while not stop.is_set():
            stop.wait(1.0)
    except KeyboardInterrupt:
        print("\n[Watcher] 正在退出...")


if __name__ == "__main__":
    if sys.platform != "win32":
        print("[Watcher] 仅支持 Windows")
        sys.exit(1)
    main()
