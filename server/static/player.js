let currentStatus = null;
let pollInterval = null;
let currentThumbnail = '';
let volumeChanging = false;
let volumeChangeTimer = null;
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
    const slider = document.getElementById('volumeSlider');
    const valueText = document.getElementById('volumeValue');
    const icon = document.getElementById('volumeIcon');
    if (!volumeChanging) {
        slider.value = Math.round(volume);
    }
    valueText.textContent = Math.round(volume) + '%';
    if (muted || volume === 0) {
        icon.textContent = '\u{1F507}';
    } else if (volume < 50) {
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
}

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
        return (await response.json()).success;
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
        if (data.success) setTimeout(fetchStatus, 100);
    } catch (e) {}
}

function initVolumeControl() {
    const slider = document.getElementById('volumeSlider');
    const valueText = document.getElementById('volumeValue');
    slider.addEventListener('input', () => {
        volumeChanging = true;
        const vol = parseInt(slider.value);
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

function attachSongItemListeners(container) {
    container.querySelectorAll('.song-item').forEach(btn => {
        btn.addEventListener('click', () => {
            const id = parseInt(btn.dataset.songId, 10);
            const name = btn.dataset.songName;
            playNcmSong(id, name);
        });
        btn.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                const id = parseInt(btn.dataset.songId, 10);
                const name = btn.dataset.songName;
                playNcmSong(id, name);
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
    attachSongItemListeners(container);
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

async function searchSongs(keywords) {
    const container = document.getElementById('searchResults');
    container.innerHTML = '<div class="loading-dots" role="status">\u641C\u7D22\u4E2D…</div>';
    try {
        const resp = await fetch(`/api/ncm/search?q=${encodeURIComponent(keywords)}&limit=30`);
        const data = await resp.json();
        renderSearchResults(data.songs || [], data.songCount || 0);
    } catch (e) {
        container.innerHTML = '<div class="empty-state" role="alert"><div class="icon" aria-hidden="true">\u{1F61E}</div><div>\u641C\u7D22\u5931\u8D25\uFF0C\u8BF7\u7A0D\u540E\u91CD\u8BD5</div></div>';
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

document.addEventListener('DOMContentLoaded', async () => {
    await ensureAuth();
    initVolumeControl();
    initProgressBar();
    restoreTabFromUrl();
    restoreSearchFromUrl();
    startPolling();
    if ('serviceWorker' in navigator) {
        navigator.serviceWorker.register('/service-worker.js').catch(() => {});
    }
});


