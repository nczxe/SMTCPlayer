let currentStatus = null;
let pollInterval = null;
let currentThumbnail = '';
let volumeChanging = false;
let volumeChangeTimer = null;
let pendingVolume = null;   // 客户端最近主动设置的音量，等待服务器回显确认前锁定
let pendingVolumeAt = 0;
let searchTimer = null;
let currentPlaylistView = 'grid';
let ncmLoggedIn = false;
let ncmUserInfo = null;

function formatTime(seconds) {
    if (!seconds || seconds < 0) return '0:00';
    const mins = Math.floor(seconds / 60);
    const secs = Math.floor(seconds % 60);
    return `${mins}:${secs.toString().padStart(2, '0')}`;
}

function formatDuration(ms) {
    if (!ms) return '';
    const totalSec = Math.floor(ms / 1000);
    const m = Math.floor(totalSec / 60);
    const s = totalSec % 60;
    return `${m}:${s.toString().padStart(2, '0')}`;
}

function showLoading() {
    document.getElementById('loadingOverlay').classList.remove('hidden');
}

function hideLoading() {
    document.getElementById('loadingOverlay').classList.add('hidden');
}

function updateAlbumArt(thumbnail) {
    const albumArt = document.getElementById('albumArt');
    if (thumbnail && thumbnail !== currentThumbnail) {
        currentThumbnail = thumbnail;
        albumArt.style.backgroundImage = `url('${thumbnail}')`;
        albumArt.classList.add('has-cover');
        albumArt.textContent = '';
    } else if (!thumbnail && currentThumbnail) {
        currentThumbnail = '';
        albumArt.style.backgroundImage = '';
        albumArt.classList.remove('has-cover');
        albumArt.textContent = '\u{1F3B5}';
    }
}

function updateVolumeUI(volume, muted) {
    // 拖动中完全忽略服务端快照，防止音量条乱飘
    if (volumeChanging) return;

    const v = Math.round(volume);
    // 回显确认：客户端刚主动设置过音量时，3 秒内与设定值不一致的轮询快照
    // 视为过期数据丢弃，避免旧值覆盖新值导致音量条弹回；一致或超窗后恢复同步
    if (pendingVolume !== null) {
        if (Date.now() - pendingVolumeAt < 3000 && v !== pendingVolume) return;
        pendingVolume = null;
    }

    const slider = document.getElementById('volumeSlider');
    const valueText = document.getElementById('volumeValue');
    const icon = document.getElementById('volumeIcon');
    slider.value = v;
    valueText.textContent = v + '%';
    if (muted || v === 0) {
        icon.textContent = '\u{1F507}';
    } else if (v < 50) {
        icon.textContent = '\u{1F509}';
    } else {
        icon.textContent = '\u{1F50A}';
    }
}

async function fetchStatus() {
    try {
        const response = await fetch('/api/status');
        const data = await response.json();
        updateUI(data);
        hideLoading();
        document.getElementById('statusText').textContent = '\u5DF2\u8FDE\u63A5';
    } catch (e) {
        if (progressBase) progressBase.playing = false; // 断连时冻结本地进度推算
        document.getElementById('statusText').textContent = '\u8FDE\u63A5\u5931\u8D25\uFF0C\u6B63\u5728\u91CD\u8BD5...';
        showLoading();
    }
}

function updateUI(status) {
    currentStatus = status;
    document.getElementById('songTitle').textContent = status.title || '\u672A\u77E5\u6807\u9898';
    document.getElementById('songArtist').textContent = status.artist || '\u672A\u77E5\u827A\u672F\u5BB6';

    const albumArt = document.getElementById('albumArt');
    albumArt.setAttribute('aria-label', status.title ? `${status.title} 的专辑封面` : '专辑封面');
    updateAlbumArt(status.thumbnail);

    const progressPercent = status.duration > 0
        ? (status.position / status.duration) * 100
        : 0;
    document.getElementById('progressFill').style.width = `${progressPercent}%`;
    const progressBar = document.getElementById('progressBar');
    progressBar.setAttribute('aria-valuenow', Math.round(progressPercent));
    progressBar.setAttribute('aria-valuetext', `${formatTime(status.position)} / ${formatTime(status.duration)}`);
    document.getElementById('currentTime').textContent = formatTime(status.position);
    document.getElementById('totalTime').textContent = formatTime(status.duration);

    const playPauseBtn = document.getElementById('playPauseBtn');
    if (status.is_playing) {
        playPauseBtn.textContent = '\u23F8';
        document.getElementById('albumArt').classList.add('playing');
    } else {
        playPauseBtn.textContent = '\u25B6';
        document.getElementById('albumArt').classList.remove('playing');
    }

    document.getElementById('prevBtn').disabled = !status.has_previous;
    document.getElementById('nextBtn').disabled = !status.has_next;

    if (status.volume_available !== false) {
        updateVolumeUI(status.volume || 0, status.muted || false);
    }

    setProgressBase(status);
}

// ========== 进度平滑（本地插值） ==========
// 服务端每秒轮询一次；播放期间本地按经过时间推进进度，得到近似实时的进度条
let progressBase = null; // {position, duration, playing, at}

function setProgressBase(status) {
    progressBase = {
        position: status.position || 0,
        duration: status.duration || 0,
        playing: !!status.is_playing,
        at: performance.now(),
    };
}

setInterval(() => {
    if (!progressBase || !progressBase.duration) return;
    const elapsed = progressBase.playing ? (performance.now() - progressBase.at) / 1000 : 0;
    const pos = Math.min(progressBase.duration, progressBase.position + elapsed);
    const pct = progressBase.duration > 0 ? (pos / progressBase.duration) * 100 : 0;
    document.getElementById('progressFill').style.width = `${pct}%`;
    document.getElementById('currentTime').textContent = formatTime(pos);
    const bar = document.getElementById('progressBar');
    bar.setAttribute('aria-valuenow', Math.round(pct));
    bar.setAttribute('aria-valuetext', `${formatTime(pos)} / ${formatTime(progressBase.duration)}`);
}, 250);

async function control(action) {
    try {
        const response = await fetch(`/api/${action}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' }
        });
        const data = await response.json();
        if (data.success) {
            setTimeout(fetchStatus, 200);
        }
    } catch (e) {
        console.error('\u63A7\u5236\u5931\u8D25:', e);
    }
}

async function setVolume(volume) {
    try {
        const response = await fetch('/api/volume', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ volume: volume })
        });
        const ok = (await response.json()).success;
        if (ok) {
            pendingVolume = Math.round(volume);
            pendingVolumeAt = Date.now();
            loadSources(); // 音量调整允许触发来源下拉刷新
        }
        return ok;
    } catch (e) {
        return false;
    }
}

async function toggleMute() {
    try {
        const response = await fetch('/api/volume/toggle_mute', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' }
        });
        const data = await response.json();
        if (data.success) {
            setTimeout(fetchStatus, 100);
            loadSources(); // 静音切换同样允许刷新
        }
    } catch (e) {}
}

function initVolumeControl() {
    const slider = document.getElementById('volumeSlider');
    const valueText = document.getElementById('volumeValue');
    slider.addEventListener('input', () => {
        volumeChanging = true;
        const vol = parseInt(slider.value);
        pendingVolume = vol;
        pendingVolumeAt = Date.now();
        valueText.textContent = vol + '%';
        const icon = document.getElementById('volumeIcon');
        if (vol === 0) icon.textContent = '\u{1F507}';
        else if (vol < 50) icon.textContent = '\u{1F509}';
        else icon.textContent = '\u{1F50A}';
        if (volumeChangeTimer) clearTimeout(volumeChangeTimer);
        volumeChangeTimer = setTimeout(() => {
            setVolume(vol);
            volumeChanging = false;
        }, 150);
    });
    slider.addEventListener('change', () => {
        const vol = parseInt(slider.value);
        pendingVolume = vol;
        pendingVolumeAt = Date.now();
        setVolume(vol);
        volumeChanging = false;
    });
}





function updateSearchUrl(query) {
    const params = new URLSearchParams(window.location.search);
    if (query) {
        params.set('q', query);
    } else {
        params.delete('q');
    }
    window.history.replaceState({ q: query }, '', '?' + params.toString());
}

function restoreSearchFromUrl() {
    const params = new URLSearchParams(window.location.search);
    const query = params.get('q') || '';
    const input = document.getElementById('searchInput');
    if (query && input.value !== query) {
        input.value = query;
        searchSongs(query);
    }
}

function escapeHtml(str) {
    if (!str) return '';
    const div = document.createElement('div');
    div.textContent = str;
    return div.innerHTML;
}

function createSongItem(s, i, context) {
    const label = `播放 ${escapeHtml(s.name)}`;
    return `<button class="song-item" data-song-id="${s.id}" data-song-name="${escapeHtml(s.name)}" aria-label="${label}">
        <div class="song-item-index" aria-hidden="true">${i + 1}</div>
        <div class="song-item-cover" style="background-image:url('${s.cover || ''}')" aria-hidden="true">${s.cover ? '' : '\u{1F3B5}'}</div>
        <div class="song-item-info">
            <div class="song-item-name">${escapeHtml(s.name)}</div>
            <div class="song-item-artist">${escapeHtml(s.artists)}${s.album ? ' \u00B7 ' + escapeHtml(s.album) : ''}</div>
        </div>
        <div class="song-item-duration" aria-hidden="true">${formatDuration(s.duration)}</div>
    </button>`;
}

function attachSongItemListeners(container, handler) {
    const play = handler || playNcmSong; // 默认走网易云（歌单）；搜索结果传入插件播放
    container.querySelectorAll('.song-item').forEach(btn => {
        btn.addEventListener('click', () => {
            const id = parseInt(btn.dataset.songId, 10);
            const name = btn.dataset.songName;
            play(id, name, btn);
        });
        btn.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                const id = parseInt(btn.dataset.songId, 10);
                const name = btn.dataset.songName;
                play(id, name, btn);
            }
        });
    });
}

function renderSearchResults(songs, total) {
    const container = document.getElementById('searchResults');
    if (!songs.length) {
        container.innerHTML = '<div class="empty-state"><div class="icon" aria-hidden="true">\u{1F50D}</div><div>\u672A\u627E\u5230\u76F8\u5173\u6B4C\u66F2</div></div>';
        return;
    }
    container.innerHTML = songs.map((s, i) => createSongItem(s, i, 'search')).join('');
    attachSongItemListeners(container, playViaProvider);
}

// ========== Search ==========
document.getElementById('searchInput').addEventListener('input', function() {
    const query = this.value.trim();
    updateSearchUrl(query);
    if (searchTimer) clearTimeout(searchTimer);
    if (!query) {
        renderSearchResults([], 0);
        return;
    }
    searchTimer = setTimeout(() => searchSongs(query), 300);
});

// ========== Plugin search bridge ==========
// 搜索与播放均由宿主插件实现：请求入 Flask 任务队列 → 宿主转交对应插件 → 结果回传
const PROVIDER_STORAGE_KEY = 'smtc.search.provider';
let searchProviders = [];      // [{id, name}]，来自宿主上报
let lastSearchProviderId = null; // 当前搜索结果所属提供者（播放时路由回同一插件）

function currentSearchProviderId() {
    const select = document.getElementById('searchProvider');
    if (select && select.value) return select.value;
    return searchProviders.length ? searchProviders[0].id : null;
}

function refreshProviderSelect() {
    const select = document.getElementById('searchProvider');
    if (!select) return;
    const saved = localStorage.getItem(PROVIDER_STORAGE_KEY) || '';
    const valid = searchProviders.find(p => p.id === saved);
    const selected = valid ? saved : (searchProviders.length ? searchProviders[0].id : '');

    if (searchProviders.length === 0) {
        // 无插件时给出占位文案，避免渲染成空白下拉框
        select.innerHTML = '<option value="">暂无搜索源（由插件提供）</option>';
        select.disabled = true;
        return;
    }

    select.innerHTML = searchProviders
        .map(p => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name || p.id)}</option>`)
        .join('');
    select.disabled = false;
    if (selected) {
        select.value = selected;
        if (!valid) localStorage.setItem(PROVIDER_STORAGE_KEY, selected); // 上次提供者已失效 → 自动补选并保存
    }
}

document.addEventListener('DOMContentLoaded', () => {
    const select = document.getElementById('searchProvider');
    if (select) {
        select.addEventListener('change', () => {
            localStorage.setItem(PROVIDER_STORAGE_KEY, select.value);
            // 切换来源后清空旧结果，避免点击时路由到错误的插件
            const input = document.getElementById('searchInput');
            if (input && input.value.trim()) searchSongs(input.value.trim());
        });
    }
});

// ========== Media source switching（SMTC 会话切换） ==========
// 电脑上可能同时开着多个音乐软件（网易云 / Spotify / QQ…），SMTC 每个软件一个会话；
// 服务端默认取第一个会话，此处允许用户指定激活哪个会话（选择持久化到 localStorage）
const SOURCE_STORAGE_KEY = 'smtc.source.id';
let mediaSessions = [];

async function applySourceSelection(source) {
    try {
        await fetch('/api/sessions/active', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ source }),
        });
        fetchStatus(); // 立即刷新一次，不用等下一个轮询周期
    } catch (e) { /* 忽略网络错误，下轮轮询自愈 */ }
}

let _lastSourceSignature = null;

function renderSourceSelect(data) {
    const select = document.getElementById('sourceSelect');
    if (!select) return;

    const current = data.preferred || '';
    // 渲染策略：初始渲染一次；之后仅在以下事件时重建一次：
    // 来源集合变化 / 切歌（标题变化）/ 暂停标记（is_playing）/ 音量调整；
    // 其余轮询一律跳过 —— 避免高频重建原生 <select> 触发 Chromium 关闭态文字不重绘。
    const signature = JSON.stringify([
        mediaSessions.map(s => [s.source, s.name, s.title, s.is_playing]),
        current,
        currentStatus && typeof currentStatus.volume === 'number'
            ? Math.round(currentStatus.volume) : null,
    ]);
    if (signature === _lastSourceSignature) return;
    _lastSourceSignature = signature;

    const options = ['<option value="">自动（最近活跃）</option>'].concat(
        mediaSessions.map(s => {
            const label = s.title
                ? `${s.name} · ${s.title}${s.is_playing ? ' ▶' : ''}`
                : s.name;
            return `<option value="${escapeHtml(s.source)}">${escapeHtml(label)}</option>`;
        })
    );
    select.innerHTML = options.join('');
    select.disabled = mediaSessions.length === 0;
    if (Array.from(select.options).some(o => o.value === current)) {
        select.value = current;
    } else {
        select.value = '';
    }
}

async function loadSources(applySaved = false) {
    try {
        const resp = await fetch('/api/sessions');
        if (!resp.ok) return;
        const data = await resp.json();
        mediaSessions = data.sessions || [];
        renderSourceSelect(data);

        if (applySaved) {
            // 恢复上次选择；保存的来源已消失则回落"自动"并清除记录
            const saved = localStorage.getItem(SOURCE_STORAGE_KEY) || '';
            const exists = saved && mediaSessions.some(s => s.source === saved);
            const want = exists ? saved : '';
            if (!exists && saved) localStorage.removeItem(SOURCE_STORAGE_KEY);
            if (want !== (data.preferred || '')) {
                await applySourceSelection(want);
            }
        }
    } catch (e) {
        // 网络异常保持现状
    }
}

document.addEventListener('DOMContentLoaded', () => {
    const select = document.getElementById('sourceSelect');
    if (select) {
        select.addEventListener('change', () => {
            localStorage.setItem(SOURCE_STORAGE_KEY, select.value);
            applySourceSelection(select.value);
        });
    }
});

// 经任务桥调用插件（返回 {status, ...结果}；超时/失败统一为 error 文案）
async function invokePlugin(provider, action, payload) {
    const req = await fetch('/api/plugin/request', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ provider, action, ...payload }),
    });
    const { job_id } = await req.json();
    if (!job_id) throw new Error('任务创建失败');
    const resp = await fetch(`/api/plugin/result/${job_id}?wait=12`);
    const data = await resp.json();
    if (data.status === 'timeout') throw new Error('响应超时，请重试');
    if (data.status !== 'done') throw new Error('任务已失效');
    return data;
}

async function searchSongs(keywords) {
    const container = document.getElementById('searchResults');
    const provider = currentSearchProviderId();
    if (!provider) {
        container.innerHTML = '<div class="empty-state"><div class="icon" aria-hidden="true">\u{1F50D}</div><div>\u65E0\u53EF\u7528\u641C\u7D22\u63D2\u4EF6</div></div>';
        return;
    }
    container.innerHTML = '<div class="loading-dots" role="status">\u641C\u7D22\u4E2D…</div>';
    try {
        const data = await invokePlugin(provider, 'search', { query: keywords });
        if (!data.success) {
            container.innerHTML = `<div class="empty-state" role="alert"><div class="icon" aria-hidden="true">\u{1F61E}</div><div>${escapeHtml(data.error || '搜索失败')}</div></div>`;
            return;
        }
        lastSearchProviderId = provider;
        renderSearchResults(data.results || [], (data.results || []).length);
    } catch (e) {
        container.innerHTML = `<div class="empty-state" role="alert"><div class="icon" aria-hidden="true">\u{1F61E}</div><div>${escapeHtml(e.message || '搜索失败，请稍后重试')}</div></div>`;
    }
}

async function playViaProvider(_songId, _songName, btn) {
    if (!btn) return;
    const provider = lastSearchProviderId || currentSearchProviderId();
    if (!provider) return;
    const item = {
        id: btn.dataset.songId,
        name: btn.dataset.songName,
    };
    switchTab('nowplaying');
    try {
        const data = await invokePlugin(provider, 'play', { item });
        if (data.success === false) {
            console.warn('插件播放失败:', data.error);
        }
    } catch (e) {
        console.warn('插件播放异常:', e.message);
    }
}






// ========== Playlist ==========
async function checkNcmStatus() {
    try {
        const resp = await fetch('/api/ncm/status');
        const data = await resp.json();
        ncmLoggedIn = data.logged_in;
        ncmUserInfo = data;
        updatePlaylistUI();
    } catch (e) {
        updatePlaylistUI();
    }
}

function updatePlaylistUI() {
    if (ncmLoggedIn) {
        document.getElementById('playlistLoginSection').classList.add('hidden');
        document.getElementById('playlistLoggedIn').classList.remove('hidden');
        document.getElementById('playlistUserInfo').textContent =
            `${ncmUserInfo.nickname || '\u7528\u6237'} \u7684\u6B4C\u5355`;
        fetchPlaylists().then(() => restorePlaylistFromUrl());
    } else {
        document.getElementById('playlistLoginSection').classList.remove('hidden');
        document.getElementById('playlistLoggedIn').classList.add('hidden');
        document.getElementById('playlistDetail').classList.add('hidden');
    }
}

async function fetchPlaylists() {
    const grid = document.getElementById('playlistGrid');
    grid.innerHTML = '<div class="loading-dots" role="status">\u52A0\u8F7D\u6B4C\u5355\u4E2D…</div>';
    try {
        const resp = await fetch('/api/ncm/playlists');
        const data = await resp.json();
        if (data.code !== 200 && data.msg) {
            ncmLoggedIn = false;
            updatePlaylistUI();
            return;
        }
        renderPlaylists(data.playlists || []);
    } catch (e) {
        grid.innerHTML = '<div class="empty-state" role="alert"><div class="icon" aria-hidden="true">\u{1F61E}</div><div>\u52A0\u8F7D\u5931\u8D25</div></div>';
    }
}

function attachPlaylistCardListeners(grid) {
    grid.querySelectorAll('.playlist-card').forEach(btn => {
        btn.addEventListener('click', () => {
            const id = parseInt(btn.dataset.playlistId, 10);
            const name = btn.dataset.playlistName;
            fetchPlaylistDetail(id, name);
        });
        btn.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                const id = parseInt(btn.dataset.playlistId, 10);
                const name = btn.dataset.playlistName;
                fetchPlaylistDetail(id, name);
            }
        });
    });
}

function renderPlaylists(playlists) {
    const grid = document.getElementById('playlistGrid');
    if (!playlists.length) {
        grid.innerHTML = '<div class="empty-state"><div class="icon" aria-hidden="true">\u{1F4CB}</div><div>\u6682\u65E0\u6B4C\u5355</div></div>';
        return;
    }
    grid.innerHTML = playlists.map(pl =>
        `<button class="playlist-card" data-playlist-id="${pl.id}" data-playlist-name="${escapeHtml(pl.name)}" aria-label="打开歌单 ${escapeHtml(pl.name)}">
            <div class="playlist-card-cover" style="background-image:url('${pl.cover || ''}')" aria-hidden="true">${pl.cover ? '' : '\u{1F3B5}'}</div>
            <div class="playlist-card-info">
                <div class="playlist-card-name">${escapeHtml(pl.name)}</div>
                <div class="playlist-card-count">${pl.trackCount}\u9996</div>
            </div>
        </button>`
    ).join('');
    attachPlaylistCardListeners(grid);
}

function updatePlaylistUrl(playlistId, playlistName) {
    const params = new URLSearchParams(window.location.search);
    if (playlistId) {
        params.set('playlist', playlistId);
        if (playlistName) params.set('playlistName', playlistName);
    } else {
        params.delete('playlist');
        params.delete('playlistName');
    }
    window.history.replaceState({ playlist: playlistId }, '', '?' + params.toString());
}

async function fetchPlaylistDetail(playlistId, playlistName) {
    currentPlaylistView = 'detail';
    document.getElementById('playlistGrid').parentElement.classList.add('hidden');
    document.getElementById('playlistDetail').classList.remove('hidden');
    document.getElementById('playlistLoggedIn').classList.add('hidden');
    updatePlaylistUrl(playlistId, playlistName);

    const listEl = document.getElementById('playlistSongList');
    listEl.innerHTML = '<div class="loading-dots" role="status">\u52A0\u8F7D\u6B4C\u66F2\u4E2D…</div>';
    try {
        const resp = await fetch(`/api/ncm/playlist/${playlistId}`);
        const data = await resp.json();
        renderPlaylistSongs(data.tracks || []);
    } catch (e) {
        listEl.innerHTML = '<div class="empty-state" role="alert"><div class="icon" aria-hidden="true">\u{1F61E}</div><div>\u52A0\u8F7D\u5931\u8D25</div></div>';
    }
}

function restorePlaylistFromUrl() {
    const params = new URLSearchParams(window.location.search);
    const playlistId = params.get('playlist');
    const playlistName = params.get('playlistName') || '';
    if (playlistId && ncmLoggedIn) {
        fetchPlaylistDetail(playlistId, playlistName);
    }
}

function renderPlaylistSongs(tracks) {
    const listEl = document.getElementById('playlistSongList');
    if (!tracks.length) {
        listEl.innerHTML = '<div class="empty-state"><div class="icon" aria-hidden="true">\u{1F4CB}</div><div>\u6682\u65E0\u6B4C\u66F2</div></div>';
        return;
    }
    listEl.innerHTML = tracks.map((t, i) => createSongItem(t, i, 'playlist')).join('');
    attachSongItemListeners(listEl);
}

function backToPlaylists() {
    currentPlaylistView = 'grid';
    document.getElementById('playlistDetail').classList.add('hidden');
    document.getElementById('playlistLoggedIn').classList.remove('hidden');
    document.getElementById('playlistGrid').parentElement.classList.remove('hidden');
    updatePlaylistUrl(null);
    fetchPlaylists();
}

async function playNcmSong(songId, songName) {
    switchTab('nowplaying');

    fetch('/api/ncm/open_web', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ song_id: songId })
    }).catch(() => {});

    setTimeout(() => {
        control('play');
    }, 2000);
    setTimeout(fetchStatus, 3500);
}

function switchTab(tabName, { pushState = true } = {}) {
    // 目标 tab 因无插件能力被隐藏时不允许激活（如 URL 恢复到已禁用的功能区）
    const targetBtn = document.querySelector(`.tab-btn[data-tab="${tabName}"]`);
    if (targetBtn && targetBtn.classList.contains('hidden')) return;

    document.querySelectorAll('.tab-btn').forEach(b => {
        const active = b.dataset.tab === tabName;
        b.classList.toggle('active', active);
        b.setAttribute('aria-selected', active ? 'true' : 'false');
    });
    document.querySelectorAll('.tab-content').forEach(c => {
        c.classList.toggle('active', c.id === 'tab-' + tabName);
    });

    if (pushState) {
        const params = new URLSearchParams(window.location.search);
        params.set('tab', tabName);
        window.history.replaceState({ tab: tabName }, '', '?' + params.toString());
    }

    if (tabName === 'playlist') {
        checkNcmStatus();
    }
}

function restoreTabFromUrl() {
    const params = new URLSearchParams(window.location.search);
    const tab = params.get('tab') || 'nowplaying';
    if (['nowplaying', 'search', 'playlist'].includes(tab)) {
        switchTab(tab, { pushState: false });
    }
}

document.querySelectorAll('.tab-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        switchTab(btn.dataset.tab);
    });
});

function initProgressBar() {
    const bar = document.getElementById('progressBar');
    bar.addEventListener('keydown', (e) => {
        if (!currentStatus || !currentStatus.duration) return;
        const step = currentStatus.duration / 20;
        let newPos = currentStatus.position || 0;
        if (e.key === 'ArrowRight' || e.key === 'ArrowUp') {
            e.preventDefault();
            newPos = Math.min(currentStatus.duration, newPos + step);
        } else if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') {
            e.preventDefault();
            newPos = Math.max(0, newPos - step);
        } else if (e.key === 'Home') {
            e.preventDefault();
            newPos = 0;
        } else if (e.key === 'End') {
            e.preventDefault();
            newPos = currentStatus.duration;
        }
        if (newPos !== currentStatus.position) {
            // Note: server API does not expose seek; this only updates UI for now.
            currentStatus.position = newPos;
            updateUI(currentStatus);
        }
    });
}

// ========== Login ==========
async function doLogin(type) {
    hideLoginError();
    const cookie = document.getElementById('loginCookie').value.trim();
    if (!cookie) { showLoginError('\u8BF7\u7C98\u8D34 MUSIC_U cookie'); return; }

    try {
        const resp = await fetch('/api/ncm/login_cookie', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ cookie })
        });
        const data = await resp.json();
        if (data.logged_in) {
            ncmLoggedIn = true;
            ncmUserInfo = data;
            updatePlaylistUI();
        } else {
            showLoginError(data.msg || 'Cookie \u65E0\u6548\u6216\u5DF2\u8FC7\u671F');
        }
    } catch (e) {
        showLoginError('\u7F51\u7EDC\u9519\u8BEF\uFF0C\u8BF7\u91CD\u8BD5');
    }
}

async function doLogout() {
    try {
        await fetch('/api/ncm/logout', { method: 'POST' });
    } catch (e) {}
    ncmLoggedIn = false;
    ncmUserInfo = null;
    updatePlaylistUI();
}



function showLoginError(msg) {
    const el = document.getElementById('loginError');
    el.textContent = msg;
    el.style.display = 'block';
}

function hideLoginError() {
    document.getElementById('loginError').style.display = 'none';
}

function startPolling() {
    fetchStatus();
    pollInterval = setInterval(fetchStatus, 1000);
}

// ========== Plugin capabilities ==========
// 搜索/歌单等内容功能由宿主插件声明提供；无对应插件时隐藏相关入口
let activeCapabilities = null;

async function applyCapabilities() {
    try {
        const resp = await fetch('/api/capabilities');
        if (!resp.ok) return;
        const data = await resp.json();
        activeCapabilities = data.capabilities || [];
        const providers = data.providers || [];
        const changed = JSON.stringify(providers) !== JSON.stringify(searchProviders);
        if (changed) {
            searchProviders = providers;
            refreshProviderSelect();
        }
    } catch (e) {
        return; // 网络异常时保持现状
    }
    const hasSearch = activeCapabilities.includes('search');
    const hasPlaylists = activeCapabilities.includes('playlists');
    document.getElementById('tab-btn-search').classList.toggle('hidden', !hasSearch);
    document.getElementById('tab-search').classList.toggle('hidden', !hasSearch);
    document.getElementById('tab-btn-playlist').classList.toggle('hidden', !hasPlaylists);
    document.getElementById('tab-playlist').classList.toggle('hidden', !hasPlaylists);

    // 当前激活 tab 被隐藏时退回"正在播放"
    const activeBtn = document.querySelector('.tab-btn.active');
    if (activeBtn && activeBtn.classList.contains('hidden')) {
        switchTab('nowplaying');
    }
}

document.addEventListener('DOMContentLoaded', async () => {
    await ensureAuth();
    await applyCapabilities(); // 先应用能力布局，再恢复 URL 状态
    loadSources(true);         // 恢复上次的播放来源选择
    initVolumeControl();
    initProgressBar();
    restoreTabFromUrl();
    restoreSearchFromUrl();
    startPolling();
    setInterval(applyCapabilities, 30000); // 插件启停后至多 30s 反映到 UI
    setInterval(() => loadSources(false), 5000); // 会话增删后至多 5s 反映到下拉框
    if ('serviceWorker' in navigator) {
        navigator.serviceWorker.register('/service-worker.js').catch(() => {});
    }
});


