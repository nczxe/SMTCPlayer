(function () {
    function byId(id) {
        return document.getElementById(id);
    }

    function setAuthMessage(message) {
        byId('authMessage').textContent = message || '';
    }

    function validPin(pin) {
        return /^[A-Za-z0-9!@#$%^&*()_\-+=[\]{}:;,.?/|~]{4,16}$/.test(pin);
    }

    (function trapFocusInAuthOverlay() {
        const overlay = byId('authOverlay');
        const pin = byId('authPin');
        const submit = byId('authSubmit');
        overlay.addEventListener('keydown', (e) => {
            if (e.key !== 'Tab') return;
            if (e.shiftKey && document.activeElement === pin) {
                e.preventDefault();
                submit.focus();
            } else if (!e.shiftKey && document.activeElement === submit) {
                e.preventDefault();
                pin.focus();
            }
        });
    })();

    async function authRequest(path, pin) {
        const resp = await smtcNativeFetch(path, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ pin })
        });
        return resp.json();
    }

    window.ensureAuth = async function () {
        const statusResp = await smtcNativeFetch('/api/auth/status');
        const status = await statusResp.json();
        if (!status.configured) {
            byId('authTitle').textContent = '设置访问 PIN';
            byId('authSubmit').textContent = '保存并进入';
        } else {
            byId('authTitle').textContent = '输入访问 PIN';
            byId('authSubmit').textContent = '进入控制器';
        }

        if (status.configured && smtcAuth.getToken()) {
            const health = await fetch('/api/health');
            if (health.ok) {
                byId('authOverlay').classList.add('hidden');
                return true;
            }
            smtcAuth.clearToken();
        }

        byId('authOverlay').classList.remove('hidden');
        // PIN input is the single primary action on this auth modal; focus it for desktop keyboard users.
        // On mobile, the soft keyboard will appear; this is acceptable because authentication is the only task.
        byId('authPin').focus();

        return new Promise((resolve) => {
            byId('authForm').onsubmit = async (event) => {
                event.preventDefault();
                const pin = byId('authPin').value.trim();
                if (!validPin(pin)) {
                    setAuthMessage('PIN 需 4-16 位，可包含字母、数字和常用安全字符');
                    return;
                }
                setAuthMessage('');
                const path = status.configured ? '/api/auth/login' : '/api/auth/setup';
                try {
                    const result = await authRequest(path, pin);
                    if (result.success && result.token) {
                        smtcAuth.setToken(result.token);
                        byId('authPin').value = '';
                        byId('authOverlay').classList.add('hidden');
                        resolve(true);
                    } else {
                        setAuthMessage('PIN 不正确或无法保存，请重新输入');
                    }
                } catch (e) {
                    setAuthMessage('连接失败，请确认服务正在运行');
                }
            };
        });
    };
})();
