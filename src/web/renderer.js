const bridge = window.chrome && window.chrome.webview;
const sessions = {};
const tabs = {};
let activeTab = null;
let focusedPane = null;
let seq = 0, tabSeq = 0;
function post(msg) { if (bridge) bridge.postMessage(msg); }

// Coalesce expensive fit.fit() calls per-session to avoid forced reflow storms.
// scheduleFit(s) will requestAnimationFrame once and emit a single resize message.
function scheduleFit(s) {
    if (!s) return;
    if (s._fitPending) return;
    s._fitPending = true;
    requestAnimationFrame(() => {
        s._fitPending = false;
        try { s.fit.fit(); post({ type: 'resize', id: s.sid, cols: s.term.cols, rows: s.term.rows }); } catch (_) { }
    });
}
function scheduleFitBySid(sid) {
    const s = sessions[sid];
    if (s) return scheduleFit(s);
    // If session not created yet, try once on next tick.
    setTimeout(() => { const s2 = sessions[sid]; if (s2) scheduleFit(s2); }, 0);
}
function copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).catch(() => execCopy(text));
    } else {
        execCopy(text);
    }
}
function execCopy(text) {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand('copy'); } catch (_) { }
    document.body.removeChild(ta);
}
function pasteInto(sid) {
    const send = (txt) => { if (txt) sendInputChunked(sid, txt); };
    if (navigator.clipboard && navigator.clipboard.readText) {
        navigator.clipboard.readText().then(send).catch(() => { });
    }
}

// Send large input in small chunks to avoid long on-frame JS work and reduce forced reflows
function sendInputChunked(id, text) {
    if (!text || text.length === 0) return;
    const CHUNK = 1024; // chars per frame
    if (text.length <= CHUNK) {
        try { console.log('[ui] sending input single chunk id=', id, 'len=', text.length); } catch(_){}
        post({ type: 'input', id: id, data: text });
        return;
    }
    try { console.log('[ui] sending input chunked id=', id, 'totalLen=', text.length); } catch(_){}
    let pos = 0;
    function sendNext() {
        const end = Math.min(pos + CHUNK, text.length);
        const piece = text.substring(pos, end);
        try { post({ type: 'input', id: id, data: piece }); } catch(_){}
        pos = end;
        if (pos < text.length) requestAnimationFrame(sendNext);
        else try { console.log('[ui] finished sending input chunks id=', id); } catch(_){}
    }
    requestAnimationFrame(sendNext);
}

let folders = [];
let openFolderSid = null;

function cdTo(sid, path) {
    if (!sessions[sid]) return;
    post({ type: 'input', id: sid, data: 'cd ' + path + ' && ls -l\n' });
    closeFolderMenu();
    const s = sessions[sid];
    if (s) s.term.focus();
}
function renderFolderMenu(sid) {
    const s = sessions[sid];
    if (!s) return;
    const menu = s.paneEl.querySelector('.folder-menu');
    if (!menu) return;
    let html = '';
    if (folders.length === 0) {
        html += '<div class="fm-empty">Nenhuma pasta favorita</div>';
    } else {
        folders.forEach(p => {
            html += '<div class="fm-item" data-path="' + encodeURIComponent(p) + '">' +
                '<span class="fm-path"></span>' +
                '<span class="fm-del" title="Remove">&#10005;</span>' +
                '</div>';
        });
    }
    html += '<div class="fm-add"><button class="fm-addbtn">+ Add Folder...</button></div>';
    menu.innerHTML = html;

    menu.querySelectorAll('.fm-item').forEach(it => {
        const p = decodeURIComponent(it.dataset.path);
        it.querySelector('.fm-path').textContent = p;
        it.querySelector('.fm-path').onclick = () => cdTo(sid, p);
        it.querySelector('.fm-del').onclick = (e) => { e.stopPropagation(); post({ type: 'removeFolder', path: p }); };
    });
    menu.querySelector('.fm-addbtn').onclick = () => {
        const p = prompt('Folder Path. Ex: ~/wspace', '');
        if (p && p.trim()) post({ type: 'addFolder', path: p.trim() });
    };
}

function toggleFolderMenu(sid) {
    const s = sessions[sid];
    if (!s) return;
    const menu = s.paneEl.querySelector('.folder-menu');
    const isOpen = menu.classList.contains('open');
    closeFolderMenu();
    if (!isOpen) {
        renderFolderMenu(sid);
        menu.classList.add('open');
        openFolderSid = sid;
    }
}
function closeFolderMenu() {
    document.querySelectorAll('.folder-menu.open').forEach(m => m.classList.remove('open'));
    openFolderSid = null;
}

document.addEventListener('mousedown', (e) => {
    if (openFolderSid && !e.target.closest('.folder-dd')) closeFolderMenu();
});

let snippets = [];
let openSnipSid = null;

function runSnippet(sid, snip) {
    if (!sessions[sid] || !snip) return;
    let data = (snip.Commands || '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    if (!data.endsWith('\n')) data += '\n';
    post({ type: 'input', id: sid, data });
    closeSnipMenu();
    const s = sessions[sid];
    if (s) s.term.focus();
}

function renderSnipMenu(sid) {
    const s = sessions[sid];
    if (!s) return;
    const menu = s.paneEl.querySelector('.snip-menu');
    if (!menu) return;
    let html = '';
    if (snippets.length == 0) {
        html += '<div class="fm-empty">Nenhum snippet salvo</div>';
    } else {
        snippets.forEach(sn => {
            html += '<div class="fm-item snip-item" data-id="' + sn.Id + '">' +
                '<span class="fm-path"></span>' +
                '<span class="snip-edit" title="Editar">&#9998;</span>' +
                '<span class="fm-del" title="Remover">&#10005;</span>' +
                '</div>';
        });
    }
    html += '<div class="fm-add"><button class="fm-addbtn snip-addbtn">+ Novo Snippet...</button></div>';
    menu.innerHTML = html;

    menu.querySelectorAll('.snip-item').forEach(it => {
        const sn = snippets.find(x => x.Id === it.dataset.id);
        if (!sn) return;
        it.querySelector('.fm-path').textContent = sn.Name || '(sem nome)';
        it.querySelector('.fm-path').title = sn.Commands || '';
        it.querySelector('.fm-path').onclick = () => runSnippet(sid, sn);
        it.querySelector('.snip-edit').onclick = (e) => { e.stopPropagation(); editSnippet(sn); };
        it.querySelector('.fm-del').onclick = (e) => { e.stopPropagation(); post({ type: 'deleteSnippet', snippetId: sn.Id }); };
    });
    menu.querySelector('.snip-addbtn').onclick = () => editSnippet(null);
}

function editSnippet(sn) {
    closeSnipMenu();
    document.getElementById('snip-modal-title').textContent = sn ? 'Edit snippet' : 'New snippet';
    document.getElementById('sf-id').value = sn ? sn.Id : '';
    document.getElementById('sf-name').value = sn ? (sn.Name || '') : '';
    document.getElementById('sf-cmds').value = sn ? (sn.Commands || '') : '';
    document.getElementById('sf-delete').style.display = sn ? '' : 'none';
    document.getElementById('snip-overlay').classList.add('show');
    document.getElementById('sf-name').focus();
}
function closeSnipForm() { document.getElementById('snip-overlay').classList.remove('show'); }

function saveSnippet() {
    const name = document.getElementById('sf-name').value.trim();
    const commands = document.getElementById('sf-cmds').value;
    if (!name) { alert('Snippet Name is required'); return; }
    if (!commands.trim()) { alert('Please enter at least one command'); return; }
    post({ type: 'saveSnippet', snippet: {
            id: document.getElementById('sf-id').value || undefined, name, commands
        }
    });
    closeSnipForm();
}
function deleteSnippetFromForm() {
    const id = document.getElementById('sf-id').value;
    if (!id) return;
    if (!confirm('Delete this snippet?')) return;
    post({ type: 'deleteSnippet', snippetId: id });
    closeSnipForm();
}

function toggleSnipMenu(sid) {
    const s = sessions[sid];
    if (!s) return;
    const menu = s.paneEl.querySelector('.snip-menu');
    const isOpen = menu.classList.contains('open');
    closeSnipMenu();
    if (!isOpen) {
        renderSnipMenu(sid);
        menu.classList.add('open');
        openSnipSid = sid;
    }
}

function closeSnipMenu() {
    document.querySelectorAll('.snip-menu.open').forEach(m => m.classList.remove('open'));
    openSnipSid = null;
}

document.addEventListener('mousedown', (e) => {
    if (openSnipSid && !e.target.closest('.snip-dd')) closeSnipMenu();
});

const THEMES = {
    // Paletas inspiradas no Termius (cores aproximadas)
    'default': {
        label: 'Termius Dark',
        bg: '#0b1220', fg: '#c8d3df', cursor: '#c8d3df', sel: '#1f2a44',
        ansi: ['#0b1220', '#ff6c6b', '#98be65', '#ecbe7b', '#51afef', '#c678dd', '#46d9ff', '#cdd6e6', '#545862', '#ff7b7b', '#9ec46a', '#ffd580', '#7ec0ff', '#d4bfff', '#7aefff', '#ffffff']
    },
    'termius-dark': {
        label: 'Termius Dark',
        bg: '#0b1220', fg: '#c8d3df', cursor: '#c8d3df', sel: '#1f2a44',
        ansi: ['#0b1220','#ff6c6b','#98be65','#ecbe7b','#51afef','#c678dd','#46d9ff','#cdd6e6','#545862','#ff7b7b','#9ec46a','#ffd580','#7ec0ff','#d4bfff','#7aefff','#ffffff']
    },
    'termius-light': {
        label: 'Termius Light',
        bg: '#f6f8fa', fg: '#2b2b2b', cursor: '#2b2b2b', sel: '#dfe7f3',
        ansi: ['#1b1f23','#b31f34','#237c3a','#b08900','#1161d3','#6b21a8','#0c7c86','#5a636a','#6c7680','#d75f5f','#5fd787','#ffc66d','#5fb3ff','#d8a9ff','#55e6ff','#ffffff']
    },
    dracula: {
        label: 'Dracula',
        bg: '#282a36', fg: '#f8f8f2', cursor: '#f8f8f2', sel: '#44475a',
        ansi: ['#000000','#ff5555','#50fa7b','#f1fa8c','#bd93f9','#ff79c6','#8be9fd','#bbbbbb','#44475a','#ff6e6e','#69ff94','#ffffa5','#d6acff','#ff92df','#a4ffff','#ffffff']
    },
    nord: {
        label: 'Nord',
        bg: '#2e3440', fg: '#d8dee9', cursor: '#d8dee9', sel: '#3b4252',
        ansi: ['#2e3440','#bf616a','#a3be8c','#ebcb8b','#81a1c1','#b48ead','#88c0d0','#e5e9f0','#4c566a','#d08770','#a3be8c','#ebcb8b','#81a1c1','#b48ead','#8fbcbb','#eceff4']
    },
    'solarized-dark': {
        label: 'Solarized Dark',
        bg: '#002b36', fg: '#839496', cursor: '#93a1a1', sel: '#073642',
        ansi: ['#073642','#dc322f','#859900','#b58900','#268bd2','#d33682','#2aa198','#eee8d5','#002b36','#cb4b16','#586e75','#657b83','#839496','#6c71c4','#93a1a1','#fdf6e3']
    },
    'solarized-light': {
        label: 'Solarized Light',
        bg: '#fdf6e3', fg: '#657b83', cursor: '#657b83', sel: '#eee8d5',
        ansi: ['#073642','#dc322f','#859900','#b58900','#268bd2','#d33682','#2aa198','#073642','#002b36','#cb4b16','#586e75','#657b83','#839496','#6c71c4','#93a1a1','#fdf6e3']
    },
    monokai: {
        label: 'Monokai',
        bg: '#272822', fg: '#f8f8f2', cursor: '#f8f8f0', sel: '#3e3d32',
        ansi: ['#000000','#f92672','#a6e22e','#f4bf75','#66d9ef','#ae81ff','#a1efe4','#f8f8f2','#49483e','#ff669d','#bde29f','#ffd89a','#9ae6f2','#caa9ff','#9be8dd','#ffffff']
    },
    gruvbox: {
        label: 'Gruvbox',
        bg: '#282828', fg: '#ebdbb2', cursor: '#ebdbb2', sel: '#3c3836',
        ansi: ['#282828','#cc241d','#98971a','#d79921','#458588','#b16286','#689d6a','#a89984','#928374','#fb4934','#b8bb26','#fabd2f','#83a598','#d3869b','#8ec07c','#fbf1c7']
    },
    everforest: {
        label: 'Everforest Dark',
        bg: '#2b3339', fg: '#d3c6aa', cursor: '#d3c6aa', sel: '#31424a',
        ansi: ['#2b3339','#e67e80','#a7c080','#dbbc7f','#7fbbb3','#d699b6','#83c092','#d3c6aa','#657070','#e78a84','#b7d3a8','#e2c58a','#9fd5ce','#e5b6cf','#9fd7b6','#ffffff']
    },
    amber: {
        label: 'Amber',
        bg: '#2b1700', fg: '#ffd8a6', cursor: '#ffb86b', sel: '#3a2200',
        ansi: ['#2b1700','#ff7043','#ff8a50','#ffb86b','#ff954f','#ff7a5f','#ffad66','#ffd8a6','#5a2b00','#ff8b59','#ffb07a','#ffd59a','#ffd2a6','#ffc1a8','#ffe0b3','#ffffff']
    },
    matrix: {
        label: 'Matrix',
        bg: '#000000', fg: '#00ff44', cursor: '#00ff44', sel: '#002200',
        ansi: ['#000000','#00ff44','#00aa00','#55ff55','#00ff99','#00ff66','#00cccc','#aaffaa','#444444','#66ff88','#99ff99','#bbffbb','#99ffcc','#88ffbb','#66ffff','#ffffff']
    },
    blue: {
        label: 'Blue',
        bg: '#001f3f', fg: '#cfeeff', cursor: '#cfeeff', sel: '#02263a',
        ansi: ['#001f3f','#ff6b6b','#74d67a','#ffd86b','#4aa3ff','#c27aff','#4bd6ff','#d6ecff','#455b6b','#ff8a8a','#a8e6b0','#ffe6a6','#7fbfff','#e0b7ff','#9ff3ff','#ffffff']
    },
    green: {
        label: 'Green',
        bg: '#07260a', fg: '#d8f6dc', cursor: '#d8f6dc', sel: '#0b2f0d',
        ansi: ['#07260a','#ff6b6b','#7bd389','#ffd86b','#6fbfff','#c27aff','#4bd6ff','#dff4e0','#3b5b40','#ff8a8a','#9fe6b8','#ffe6a6','#9fcfff','#e0b7ff','#9ff3ff','#ffffff']
    },
    red: {
        label: 'Red',
        bg: '#2b0a0a', fg: '#ffd6d6', cursor: '#ffd6d6', sel: '#3a0f0f',
        ansi: ['#2b0a0a','#ff6b6b','#98be65','#ecbe7b','#51afef','#c678dd','#46d9ff','#ffd6d6','#544040','#ff7b7b','#b7d3a8','#e2c58a','#7ec0ff','#d4bfff','#7aefff','#ffffff']
    },
    flexoki: {
        label: 'Flexoki',
        bg: '#0f1226', fg: '#dfe7ff', cursor: '#ffd479', sel: '#1b2238',
        ansi: ['#0f1226','#ff6b6b','#8be58b','#ffd86b','#6aa8ff','#d6a9ff','#4bd6ff','#dfe7ff','#46506a','#ff8a8a','#b7e6b7','#ffe6a6','#9fcfff','#e7caff','#bff3ff','#ffffff']
    },
    nightowl: {
        label: 'Night Owl',
        bg: '#011627', fg: '#d6deeb', cursor: '#d6deeb', sel: '#01243b',
        ansi: ['#011627','#ef5350','#21c7a8','#ffd866','#82aaff','#c792ea','#7fdbca','#a7b9cc','#2b3a42','#ff6b6b','#3be0b5','#fff29b','#a1c2ff','#d1a3ff','#bff0de','#ffffff']
    }
};

function themeEntries() {
    return Object.entries(THEMES).filter(([k]) => k !== 'default');
}

function themeOptions(name) {
    const t = THEMES[name] || THEMES.default;
    const o = { background: t.bg, foreground: t.fg, cursor: t.cursor, selectionBackground: t.sel };
    if (t.ansi) {
        const k = ['black', 'red', 'green', 'yellow', 'blue', 'magenta', 'cyan', 'white',
            'brightBlack', 'brightRed', 'brightGreen', 'brightYellow', 'brightBlue',
            'brightMagenta', 'brightCyan', 'brightWhite'];
        t.ansi.forEach((c, i) => o[k[i]] = c);
    }
    return o;
}

function xtermReady() {
    return typeof window.Terminal === 'function'
        && window.FitAddon && typeof window.FitAddon.FitAddon === 'function';
}

function showVendorError() {
    document.getElementById('empty').style.display = 'none';
    document.getElementById('terminals').insertAdjacentHTML('beforeend',
        '<div class="term active" style="padding:24px;color:#ff9d9d;font-family:var(--mono);' +
        'font-size:13px;line-height:1.6;white-space:pre-wrap">' +
        'xterm.js not found in web/vendor/\n\nMissing: vendor/xterm.js, vendor/xterm.css, ' +
        'vendor/addon-fit.js\n\nSee web/vendor/README.md</div>');
}

function makePane(spec) {
    const sid = 'S' + (++seq);

    const paneEl = document.createElement('div');
    paneEl.className = 'pane';
    paneEl.dataset.sid = sid;

    const bar = document.createElement('div');
    bar.className = 'pane-bar';
    bar.innerHTML =
        '<span class="pane-title"></span>' +
        '<span class="pane-actions">' +
        '<div class="folder-dd">' +
        '<button class="pb" data-act="folders" title="Favorite Folders">&#128193;</button>' +
        '<div class="folder-menu"></div>' +
        '</div>' +
        '<div class="snip-dd">' +
        '<button class="pb" data-act="snippets" title="Snippets (Commands)">&#9889;</button>' +
        '<div class="snip-menu"></div>' +
        '</div>' +
        '<select class="pane-theme" title="Theme"></select>' +
        '<button class="pb" data-act="sh" title="Dividir lado a lado (Ctrl+Shift+D">&#9707;</button>' +
        '<button class="pb" data-act="sv" title="Dividir Empilhado (Ctrl+Shift+E">&#9707;</button>' +
        '<button class="pb close" data-act="x" title="Fechar Painel">&#10005;</button>' +
        '</span>';
    bar.querySelector('.pane-title').textContent = spec.label;
    bar.querySelector('[data-act="sv"]').style.transform = 'rotate(90deg)';

    // dropdown pastas favoritas
    bar.querySelector('[data-act="folders"]').onclick = (e) => {
        e.stopPropagation();
        toggleFolderMenu(sid);
    };

    // dropdown snippets
    bar.querySelector('[data-act="snippets"]').onclick = (e) => {
        e.stopPropagation();
        toggleSnipMenu(sid);
    };

    // seletor de tema
    const sel = bar.querySelector('.pane-theme');
    themeEntries().forEach(([k, v]) => {
        const opt = document.createElement('option');
        opt.value = k; opt.textContent = v.label;
        if (k === (spec.theme || 'default')) opt.selected = true;
        sel.appendChild(opt);
    });

    sel.onchange = () => setPaneTheme(sid, sel.value);

    bar.querySelector('[data-act="sh"]').onclick = () => splitPane(sid, 'h');
    bar.querySelector('[data-act="sv"]').onclick = () => splitPane(sid, 'v');
    bar.querySelector('[data-act="x"]').onclick = () => closeSession(sid);

    const termEl = document.createElement('div');
    termEl.className = 'pane-term';

    paneEl.appendChild(bar);
    paneEl.appendChild(termEl);
    paneEl.addEventListener('mousedown', () => focusPane(sid), true);

    // xterm
    const term = new Terminal({
        fontFamily: '"Cascadia Code", "Consolas", monospace',
        fontSize: spec.fontSize || DEFAULT_FONT, cursorBlink: true, theme: themeOptions(spec.theme),
        scrollback: 10000,  // Linhas de historico
    });

    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(termEl);
    term.onData(data => post({ type: 'input', id: sid, data }));
    term.textarea && term.textarea.addEventListener('focus', () => focusPane(sid));

    // Comportamento estilo Putty
    termEl.addEventListener('mouseup', () => {
        const sel = term.getSelection();
        if (sel && sel.length) copyText(sel);
    });
    // Botao direito
    termEl.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        pasteInto(sid);
    });

    // refir automatico (janela, split, troca de aba)
    const ro = new ResizeObserver(() => {
        if (!paneEl.isConnected || paneEl.offsetParent === null) return;
        scheduleFitBySid(sid);
    });
    ro.observe(termEl);

    sessions[sid] = { sid, term, fit, ro, paneEl, termEl, tabId: null, spec, alive:true };

    // arranca o ConPTY
    // Envia start imediatamente, mas agenda o fit para um momento menos prioritário
    // para evitar trabalho pesado no primeiro requestAnimationFrame.
    post(Object.assign({}, spec.startMsg, { id: sid, cols: term.cols || 80, rows: term.rows || 24 }));
    if (typeof window !== 'undefined' && window.requestIdleCallback) {
        try { window.requestIdleCallback(() => scheduleFitBySid(sid), { timeout: 200 }); } catch (_) { setTimeout(() => scheduleFitBySid(sid), 120); }
    } else {
        setTimeout(() => scheduleFitBySid(sid), 120);
    }

    // Copy on selection (like Putty): use xterm selection change for reliable detection
    try {
        term.onSelectionChange(() => {
            try {
                const sel = term.getSelection();
                if (sel && sel.length > 0) copyText(sel);
            } catch (_) { }
        });
    } catch (_) {
        // fallback to DOM selection
        termEl.addEventListener('mouseup', () => {
            setTimeout(() => {
                let sel = '';
                try { sel = term.getSelection && term.getSelection() || (document.getSelection ? document.getSelection().toString() : ''); } catch (_) { sel = ''; }
                if (sel && sel.length > 0) copyText(sel);
            }, 50);
        });
    }

    // Double-click: copy selected word
    termEl.addEventListener('dblclick', () => {
        setTimeout(() => {
            try { const sel = term.getSelection(); if (sel && sel.length > 0) copyText(sel); } catch (_) { }
        }, 20);
    });

    return { sid, paneEl };
}

function setPaneTheme(sid, name) {
    const s = sessions[sid];
    if (!s) return;
    const opts = themeOptions(name);

    try { s.term.options.theme = opts; } catch (_) { }
    try { if (s.term.setOption) s.term.setOption('theme', opts); } catch (_) { }
    try { s.term.refresh(0, s.term.rows - 1); } catch (_) { }
    s.spec.theme = name;

    const sel = s.paneEl.querySelector('.pane-theme');
    if( sel && sel.value !== name ) sel.value = name;
    if (focusedPane === sid) syncAppearance();
    prefs.theme = name;
    persistPrefs();
}

const DEFAULT_FONT = 13.5;
const FONT_MIN = 8, FONT_MAX = 28;

let prefs = { theme: 'default', fontSize: DEFAULT_FONT }; 
function persistPrefs() { post({ type: 'savePrefs', theme: prefs.theme, fontSize: prefs.fontSize }); }

function setPaneFont(sid, px) {
    const s = sessions[sid];
    if (!s) return;
    px = Math.max(FONT_MIN, Math.min(FONT_MAX, px));
    s.spec.fontSize = px;
    try { s.term.options.fontSize = px; } catch (_) { }
    try { if (s.term.setOption) s.term.setOption('fontSize', px); } catch (_) { }
    // refir: mudar a fonte muda quantas colunas/linhas cabem 
    scheduleFit(s);
    if (focusedPane === sid) syncAppearance();
    // vira a preferncia da janela (reabre novas sessoes com esse tamanho)
    prefs.fontSize = px;
    persistPrefs();
}

function changeFont(delta) {
    if (!focusedPane) return;
    const s = sessions[focusedPane];
    if (!s) return;
    setPaneFont(focusedPane, (s.spec.fontSize || DEFAULT_FONT) + delta * 0.5);
}

function resetFont() {
    if (focusedPane) setPaneFont(focusedPane, DEFAULT_FONT);
}

function toggleAppearance() {
    const ap = document.getElementById('appearance');
    const willOpen = !ap.classList.contains('open');
    ap.classList.toggle('open', willOpen);
    if (willOpen) { buildThemeGrid(); syncAppearance(); }
}

function buildThemeGrid() {
    const grid = document.getElementById('ap-themes');
    if (grid.childElementCount) return;
    themeEntries().forEach(([k, v]) => {
        const card = document.createElement('div');
        card.className = 'ap-theme';
        card.dataset.theme = k;
        card.title = v.label;
        card.onclick = () => { if (focusedPane) setPaneTheme(focusedPane, k); }
        const sw = v.ansi || [v.bg, v.fg, v.cursor, v.sel];
        card.innerHTML =
            '<div class="ap-sw" style="background:' + v.bg + '">' +
                '<span style="background:' + (sw[1] || v.fg) + '"></span>' +
                '<span style="background:' + (sw[2] || v.cursor) + '"></span>' +
                '<span style="background:' + (sw[4] || v.cursor) + '"></span>' +
                '<span style="background:' + (sw[6] || v.fg) + '"></span>' +
            '</div>' +
            '<div class="ap-tname">' + v.label + '</div>';
         grid.appendChild(card);
     });
}

function syncAppearance() {
    const ap = document.getElementById('appearance');
    if (!ap.classList.contains('open')) return;
    const s = focusedPane && sessions[focusedPane];
    const note = document.getElementById('ap-note');
    if (!s) {
        note.textContent = 'Nenhuma sessao em foco';
        document.querySelectorAll('.ap-theme.sel').forEach(c => c.classList.remove('sel'));
        return;
    }
    note.textContent = 'Aplica a: ' + s.spec.label;
    document.getElementById('ap-fsize').textContent = (s.spec.fontSize || DEFAULT_FONT);
    const cur = s.spec.theme || 'default';
    document.querySelectorAll('.ap-theme').forEach(c => c.classList.toggle('sel', c.dataset.theme === cur));
}

function newTab(spec) {
    if (!xtermReady()) { showVendorError(); return; }
    document.getElementById('empty').style.display = 'none';

    const tabId = 'T' + (++tabSeq);
    const tabEl = document.createElement('div');
    tabEl.className = 'tab on';
    tabEl.id = 'tab-' + tabId;
    tabEl.draggable = true;
    tabEl.onclick = (e) => { if (!e.target.classList.contains('x')) activateTab(tabId); };
    tabEl.innerHTML =
        '<span class="st"></span><span class="dot" title="Nova saida"></span>' +
        '<span class="rec" title="Recording on File"></span>' +
        '<span class="label"></span><span class="x" title="Fechar aba">&#10005;</span>';
    tabEl.querySelector('.label').textContent = spec.label;
    const labelEl = tabEl.querySelector('.label');
    labelEl.textContent = spec.label;
    labelEl.title = 'Double-Click to Rename';
    labelEl.ondblclick = (e) => { e.stopPropagation(); beginRenameTab(tabId); };
    tabEl.querySelector('.x').onclick = (e) => { e.stopPropagation(); closeTab(tabId); };
    tabEl.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        e.stopPropagation();
        showTabMenu(tabId, e.clientX, e.clientY);
    });
    setupTabDrag(tabEl, tabId);
    document.getElementById('tabs').insertBefore(tabEl, document.getElementById('newtab'));

    const container = document.createElement('div');
    container.className = 'tab-panes';
    container.id = 'panes-' + tabId;
    document.getElementById('terminals').appendChild(container);

    tabs[tabId] = { id: tabId, tabEl, container };

    const { sid, paneEl } = makePane(spec);
    sessions[sid].tabId = tabId;
    container.appendChild(paneEl);

    activateTab(tabId);
    focusPane(sid);
}

let dragTabId = null;
function setupTabDrag(tabEl, tabId) {
    tabEl.addEventListener('dragstart', (e) => {
        if (tabEl.querySelector('.label-edit')) { e.preventDefault(); return; }
        dragTabId = tabId;
        tabEl.classList.add('dragging');
        try {
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData('text/plain', tabId);
            } catch (_) { }
    });

    tabEl.addEventListener('dragend', () => {
        dragTabId = null;
        tabEl.classList.remove('dragging');
        document.querySelectorAll('.tab.drop-before, .tab.drop-after')
            .forEach(t => t.classList.remove('drop-before', 'drop-after'));
    });

    tabEl.addEventListener('dragover', (e) => {
        if (dragTabId == null || dragTabId === tabId) return;
        e.preventDefault();
        try { e.dataTransfer.dropEffect = 'move'; } catch (_) { }
        const r = tabEl.getBoundingClientRect();
        const after = e.clientX > r.left + r.width / 2;
        tabEl.classList.toggle('drop-after', after);
        tabEl.classList.toggle('drop-before', !after);
    });

    tabEl.addEventListener('dragleave', () => {
        tabEl.classList.remove('drop-before', 'drop-after');
    });

    tabEl.addEventListener('drop', (e) => {
        e.preventDefault();
        if (dragTabId == null || dragTabId === tabId) return;
        const src = tabs[dragTabId] && tabs[dragTabId].tabEl;
        if (!src) return;
        const r = tabEl.getBoundingClientRect();
        const after = e.clientX > r.left + r.width / 2;
        const bar = document.getElementById('tabs');
        let ref = after ? tabEl.nextSibling : tabEl;
        const newtab = document.getElementById('newtab');
        if (ref && (ref === newtab || !ref.classList || !ref.classList.contains('tab'))) ref = newtab;
        bar.insertBefore(src, ref);
        tabEl.classList.remove('drop-before', 'drop-after');
    });
}

function beginRenameTab(tabId) {
    const t = tabs[tabId];
    if (!t) return;
    const labelEl = t.tabEl.querySelector('.label');
    if (!labelEl || t.tabEl.querySelector('.label-edit')) return;

    const current = labelEl.textContent;
    const input = document.createElement('input');
    input.className = 'label-edit';
    input.value = current;
    input.spellcheck = false;
    input.onclick = (e) => e.stopPropagation();
    input.ondblclick = (e) => e.stopPropagation();

    labelEl.style.display = 'none';
    labelEl.after(input);
    input.focus();
    input.select();

    let done = false;
    const finish = (save) => {
        if (done) return;
        const val = input.value.trim();
        if (save && val) {
            labelEl.textContent = val;
            const first = t.container.querySelector('.pane');
            const s = first && sessions[first.dataset.sid];
            if (s) s.spec.label = val;
        }
        input.remove();
        labelEl.style.display = '';
    };

    input.onkeydown = (e) => {
        if (e.key === 'Enter') { e.preventDefault(); finish(true); }
        else if (e.key === 'Escape') { e.preventDefault(); finish(false); }
        e.stopPropagation();
    };
    input.onblur = () => finish(true);
}
function activateTab(tabId) {
    activeTab = tabId;
    Object.values(tabs).forEach(t => {
        const on = t.id === tabId;
        t.tabEl.classList.toggle('active', on);
        t.container.classList.toggle('active', on);
        if (on) t.tabEl.classList.remove('activity');
    });
    const first = tabs[tabId] && tabs[tabId].container.querySelector('.pane');
    if (first) focusPane(first.dataset.sid);
}

function refreshTabStatus(tabId) {
    const t = tabs[tabId];
    if (!t) return;
    const paneSids = [...t.container.querySelectorAll('.pane')].map(p => p.dataset.sid);
    const anyAlive = paneSids.some(sid => sessions[sid] && sessions[sid].alive);
    t.tabEl.classList.toggle('dead', !anyAlive);
    t.tabEl.classList.toggle('on', anyAlive);
}

function focusPane(sid) {
    const s = sessions[sid];
    if (!s) return;
    focusedPane = sid;
    document.querySelectorAll('.pane.focused').forEach(p => p.classList.remove('focused'));
    s.paneEl.classList.add('focused');
    updateStatus(s);
    syncAppearance();
    requestAnimationFrame(() => {
        scheduleFit(s);
        if (document.querySelector('.label-edit')) return;
        try { s.term.focus(); } catch (_) { }
    });
}

function updateStatus(s) {
    dsocument.getElementById('sb-conn').innerHTML = '&#9679; <span class="k"></span>';
    document.querySelector('#sb-conn .k').textContent = s.spec.label;
    document.getElementById('sb-enc').textContent = s.spec.status || '';
}

function splitPane(sid, dir) {
    const s = sessions[sid];
    if (!s) return;
    const paneEl = s.paneEl;
    const parent = paneEl.parentNode;

    // novo painel duplica o alvo
    const { sid: nsid, paneEl: newPaneEl } = makePane(Object.assign({}, s.spec));
    sessions[nsid].tabId = s.tabId;

    const split = document.createElement('div');
    split.className = 'split ' + (dir === 'h' ? 'split-h' : 'split-v');

    // O novo split herda o espaco que o painel ocupava (para nao espremer)
    // os irmaos ao dividir um painel que ja estava dentro de outro split
    split.style.flex = paneEl.style.flex || '1 1 0';

    parent.replaceChild(split, paneEl);
    paneEl.style.flex = '1 1 0';
    newPaneEl.style.flex = '1 1 0';

    const splitter = document.createElement('div');
    splitter.className = 'splitter ' + (dir === 'h' ? 'sp-h' : 'sp-v');
    attachSplitterDrag(splitter, dir);

    split.appendChild(paneEl);
    split.appendChild(splitter);
    split.appendChild(newPaneEl);

    focusPane(nsid);
    refreshTabStatus(s.tabId);
}

function attachSplitterDrag(splitter, dir) {
    splitter.addEventListener('mousedown', (e) => {
        e.preventDefault();
        const prev = splitter.previousElementSibling;
        const next = splitter.nextElementSibling;
        if (!prev || !next) return;
        const horiz = dir === 'h';
        const startPos = horiz ? e.clientX : e.clientY;
        const prevSize = horiz ? prev.offsetWidth : prev.offsetHeight;
        const nextSize = horiz ? next.offsetWidth : next.offsetHeight;
        const total = prevSize + nextSize;

        let _movePending = false;
        const onMove = (ev) => {
            // throttle splitter moves to animation frames to avoid layout thrashing
            if (_movePending) return;
            _movePending = true;
            requestAnimationFrame(() => {
                _movePending = false;
                const pos = horiz ? ev.clientX : ev.clientY;
                let d = pos - startPos;
                let np = prevSize + d, nn = nextSize - d;
                const min = 80;
                if (np < min) { np = min; nn = total - min; }
                if (nn < min) { nn = min; np = total - min; }
                prev.style.flex = np + ' 1 0';
                next.style.flex = nn + ' 1 0';
            });
        };
        const onUp = () => {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.body.style.cursor = '';
        };
        document.body.style.cursor = horiz ? 'col-resize' : 'row-resize';
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    });
}

function closeSession(sid) {
    const s = sessions[sid];
    if (!s) return;
    const tabId = s.tabId;
    post({ type: 'close', id: sid });
    try { s.ro.disconnect(); } catch (_) { }
    try { s.term.dispose(); } catch (_) { }

    const paneEl = s.paneEl;
    const parent = paneEl.parentNode;
    delete sessions[sid];
    if(logging[sid]) { delete logging[sid]; updateTabRecIndicator(tabId); }

    if (parent && parent.classList.contains('split')) {
        const splitter = paneEl.previousElementSibling && paneEl.previousElementSibling.classList.contains('splitter')
            ? paneEl.previousElementSibling : paneEl.nextElementSibling;
        const sibling = [...parent.children].find(c => c !== paneEl && !c.classList.contains('splitter'));
        paneEl.remove();
        if (splitter) splitter.remove();
        if (sibling) {
            sibling.style.flex = parent.style.flex || '1 1 0';
            parent.parentNode.replaceChild(sibling, parent);
        }
        // foca algum painel da aba e reavalia a cor da aba (pode voltar a verde)
        const anyPane = tabs[tabId] && tabs[tabId].container.querySelector('.pane');
        if (anyPane) focusPane(anyPane.dataset.sid);
        refreshTabStatus(tabId);
    } else {
        // era o unico painel -> fecha a aba
        closeTab(tabId, true);
    }
}

function closeTab(tabId,skipSessions) {
    const t = tabs[tabId];
    if (!t) return;
    if (!skipSessions) {
        t.container.querySelectorAll('.pane').forEach(p => {
            const sid = p.dataset.sid, s = sessions[sid];
            if (s) { post({ type: 'close', id: sid }); try { s.ro.disconnect(); s.term.dispose(); } catch (_) { } delete sessions[sid]; }
            if (logging[sid]) { delete logging[sid]; }
        });
    }
    t.tabEl.remove();
    t.container.remove();
    delete tabs[tabId];

    const ids = Object.keys(tabs);
    if (ids.length) activateTab(ids[ids.length - 1]);
    else {
        activeTab = null; focusedPane = null;
        document.getElementById('empty').style.display = 'flex';
        document.getElementById('sb-conn').textContent = 'Sem sessao ativa';
        document.getElementById('sb-enc').textContent = '';
        const host = document.querySelector('.host.local');
        if (host) host.classList.remove('connected');
    }
}

const logging = {};

function tabLoggingSids(tabId) {
    const t = tabs[tabId];
    if (!t) return [];
    return [...t.container.querySelectorAll('.pane')].map(p => p.dataset.sid);
}
function isTabLogging(tabId) {
    return tabLoggingSids(tabId).some(sid => logging[sid]);
}
function updateTabRecIndicator(tabId) {
    const t = tabs[tabId];
    if (!t) return;
    const on = isTabLogging(tabId);
    t.tabEl.classList.toggle('logging', on);
    const rec = t.tabEl.querySelector('.rec');
    if (rec) {
        const paths = tabLoggingSids(tabId).map(sid => logging[sid]).filter(Boolean);
        rec.title = paths.length ? ('Recording to:\n' + paths.join('\n')) : '';
    }
}
function startTabLogging(tabId) {
    const t = tabs[tabId];
    if (!t) return;
    const label = t.tabEl.querySelector('.label').textContent || tabId;
    tabLoggingSids(tabId).forEach(sid => {
        if (logging[sid]) return;
        post({ type: 'startLog', id: sid, label });
    });
}
function stopTabLogging(tabId) {
    const t = tabs[tabId];
    if (!t) return;
    tabLoggingSids(tabId).forEach(sid => {
        // ask backend to stop logging for each session in the tab
        post({ type: 'stopLog', id: sid });
    });
}

let tabMenuEl = null;
function closeTabMenu() {
    if (tabMenuEl) { tabMenuEl.remove(); tabMenuEl = null; }
    document.removeEventListener('mousedown', onDocMouseDownForTabMenu, true);
    document.removeEventListener('keydown', onDocKeyDownForTabMenu, true);
}
function onDocMouseDownForTabMenu(e) {
    if (tabMenuEl && !tabMenuEl.contains(e.target)) {
        closeTabMenu();
    }
}
function onDocKeyDownForTabMenu(e) {
    if(e.key === 'Escape') closeTabMenu();
}
function showTabMenu(tabId, x, y) {
    closeTabMenu();
    const t = tabs[tabId];
    if (!t) return;
    const menu = document.createElement('div');
    menu.className = 'tab-menu';
    const isLog = isTabLogging(tabId);
    const items = [
        { label: 'Rename Tab', action: () => beginRenameTab(tabId) },
        isLog
            ? { label: 'Stop Recording', action: () => stopTabLogging(tabId) }
            : { label: 'Start Recording', action: () => startTabLogging(tabId) },
        { sep: true },
        { label: 'Open Log Directory', action: () => post({ type: 'openLogDir' }) },
        { label: 'Setup Log Directory', action: () => post({ type: 'pickLogDir' }) },
        { sep: true },
        { label: 'Close Tab', action: () => closeTab(tabId) },
    ];
    items.forEach(it => {
        if (it.sep) {
            const sep = document.createElement('div');
            sep.className = 'tab-menu-sep';
            menu.appendChild(sep);
            return;
        }
        const el = document.createElement('div');
        el.className = 'tab-menu-item';
        el.textContent = it.label;
        el.onclick = () => { closeTabMenu(); it.action(); };
        menu.appendChild(el);
    });
    document.body.appendChild(menu);
    const w = menu.offsetWidth, h = menu.offsetHeight;
    const vx = Math.min(x, window.innerWidth - w - 4);
    const vy = Math.min(y, window.innerHeight - h - 4);
    menu.style.left = vx + 'px';
    menu.style.top = vy + 'px';
    tabMenuEl = menu;
    setTimeout(() => {
        document.addEventListener('mousedown', onDocMouseDownForTabMenu, true);
        document.addEventListener('keydown', onDocKeyDownForTabMenu, true);
    }, 0);
}
function openGitBash() {
    const host = document.querySelector('.host.local');
    if (host) host.classList.add('connected');
    newTab({ startMsg: { type: 'start' }, label: 'Git Bash ' + (tabSeq + 1),
             status: 'bash - ConPTY', theme: prefs.theme, fontSize: prefs.fontSize });
}
function openSsh(connId, name, theme) {
    // Tema salvo
    newTab({
        startMsg: { type: 'startSsh', connId }, label: name || 'SSH',
        status: 'ssh - ConPTY', theme: theme || prefs.theme, fontSize: prefs.fontSize, connId });
}
function filterHosts(q) {
    q = q.toLowerCase();
    document.querySelectorAll('.host').forEach(h => {
        h.style.display = (h.dataset.name || '').toLowerCase().includes(q) ? '' : 'none';
    });
}

// ========== Mensagens do C# ================
if (bridge) {
    bridge.addEventListener('message', ev => {
        const m = ev.data;
        if (!m || !m.type) return;
        if (m.type === 'data') {
            const s = sessions[m.id];
            if (!s) return;
            s.term.write(m.data);
            if (s.tabId !== activeTab && tabs[s.tabId]) tabs[s.tabId].tabEl.classList.add('activity');
        } else if (m.type === 'exit') {
            const s = sessions[m.id];
            if (s) {
                const cor = m.code === 0 ? '33' : '31';     // 0 = amarelo (saiu normal); != 0 = vermelho
                s.term.write('\r\n\x1b[' + cor + 'm[sessao encerrada - codigo ' + m.code + ']\x1b[0m\r\n');
                s.alive = false;
                if (logging[m.id]) { delete logging[m.id]; updateTabRecIndicator(s.tabId); }
                refreshTabStatus(s.tabId);
            }
        } else if (m.type === 'conns') {
            renderConns(m.items || []);
        } else if (m.type === 'folders') {
            folders = m.items || [];
            // reabre o menu que estava aberto (se houver) para refletir mudancas
            if (openFolderSid && sessions[openFolderSid]) renderFolderMenu(openFolderSid);
        } else if (m.type === 'snippets') {
            snippets = m.items || [];
            if (openSnipSid && sessions[openSnipSid]) renderSnipMenu(openSnipSid);
        } else if (m.type === 'prefs') {
            if (m.theme && THEMES[m.theme]) prefs.theme = m.theme;
            if (m.fontSize) prefs.fontSize = m.fontSize;
        } else if (m.type === 'paste') {
            try { console.log('[ui] paste received id=', m.id, 'len=', (m.data || '').length); } catch (_) { }
            const s = sessions[m.id];
            if (!s) return;
            if (m.data) {
                try { console.log('[ui] forwarding paste to backend as input'); } catch (_) { }
                post({ type: 'input', id: m.id, data: m.data });
            }
        } else if (m.type === 'keyPicked') {
            document.getElementById('f-key').value = m.path;
        } else if (m.type === 'connSaved') {
            closeConnForm();
        } else if (m.type === 'error') {
            console.error('backend:', m.message);
            const s = focusedPane && sessions[focusedPane];
            if (s) s.term.write('\r\n\x1b[31m[error] ' + m.message + '\x1b[0m\r\n');
        } else if (m.type === 'logStatus') {
            const sid = m.id;
            const s = sessions[sid];
            if (m.active) logging[sid] = m.path || true;
            else delete logging[sid];
            if (s) updateTabRecIndicator(s.tabId);
        } else if (m.type === 'logDirPicked') {
            try { console.log('[ui] logDirPicked:', m.path); } catch (_) { }
        }
    });
}

let conns = [];
function renderConns(items) {
    conns = items;
    const list = document.getElementById('ssh-list');
    document.getElementById('ssh-group-hdr').style.display = items.length ? '' : 'none';
    list.innerHTML = '';
    items.forEach(c => {
            const row = document.createElement('div');
            row.className = 'host ssh';
            row.dataset.name = c.Name || c.Host;
            row.title = c.User + '@' + c.Host + ':' + c.Port;
            row.onclick = (e) => { if (!e.target.classList.contains('edit')) openSsh(c.Id, c.Name || c.Host, c.Theme); };
            row.innerHTML =
                '<div class="ico">&gt;</div>' +
                '<div class="meta"><div class="name"></div><div class="sub"></div></div>' +
                '<div class="edit" title="Editar">&#9998;</div>';
            row.querySelector('.name').textContent = c.Name || c.Host;
            row.querySelector('.sub').textContent = c.User + '@' + c.Host + ':' + c.Port;
            row.querySelector('.edit').onclick = (e) => { e.stopPropagation(); openConnForm(c.Id); };
            list.appendChild(row);
        });
}

function openConnForm(connId) {
    const c = connId ? conns.find(x => x.Id === connId) : null;
    document.getElementById('modal-title').textContent = c ? 'Edit SSH Connection' : 'New SSH Connection';
    document.getElementById('f-id').value = c ? c.Id : '';
    document.getElementById('f-name').value = c ? (c.Name || '') : '';
    document.getElementById('f-host').value = c ? (c.Host || '') : '';
    document.getElementById('f-port').value = c ? (c.Port || 22) : 22;
    document.getElementById('f-user').value = c ? (c.User || '') : '';
    document.getElementById('f-key').value = c ? (c.KeyPath || '') : '';
    document.getElementById('f-theme').value = c ? (c.Theme || 'default') : 'default';
    document.getElementById('f-password').value = '';
    document.getElementById('f-password').placeholder =
        c && c.hasPassword ? '****** (keep saved;)' : 'keep blank to not save';
    setAuth(c ? (c.AuthMethod || 'password') : 'password');
    document.getElementById('btn-delete').style.display = c ? '' : 'none';
    document.getElementById('overlay').classList.add('show');
    document.getElementById('f-name').focus();
}

function closeConnForm() { document.getElementById('overlay').classList.remove('show'); }

function setAuth(method) {
    document.querySelectorAll('.auth-tab').forEach(b => b.classList.toggle('active', b.dataset.auth === method));
    document.getElementById('auth-password').style.display = method === 'password' ? '' : 'none';
    document.getElementById('auth-key').style.display = method === 'key' ? '' : 'none';
}

function currentAuth() {
    const a = document.querySelector('.auth-tab.active');
    return a ? a.dataset.auth : 'password';
}

function togglePw() {
    const el = document.getElementById('f-password');
    el.type = el.type === 'password' ? 'text' : 'password';
}

function pickKey() { post({ type: 'pickKey' }); }

function saveConn() {
    const name = document.getElementById('f-name').value.trim();
    const host = document.getElementById('f-host').value.trim();
    const user = document.getElementById('f-user').value.trim();
    const port = parseInt(document.getElementById('f-port').value, 10) || 22;
    const authMethod = currentAuth();
    const keyPath = document.getElementById('f-key').value.trim();
    const theme = document.getElementById('f-theme').value;
    const pw = document.getElementById('f-password').value;

    if (!host) { alert('Informe o Host / IP.'); return; }
    if (!user) { alert('Informe o Usuario.'); return; }
    if (authMethod === 'key' && !keyPath) { alert('Private Key Path is required.'); return; }

    const conn = {
        id: document.getElementById('f-id').value || undefined,
        name: name || host,
        host,
        port,
        user,
        authMethod,
        keyPath,
        theme,
        group: 'SSH',
    };
    if (pw) conn.password = pw;
    post({ type: 'saveConn', conn });
}

function deleteConn() {
    const id = document.getElementById('f-id').value;
    if (!id) return;
    if (!confirm('Remove Connection ?')) return;
    post({ type: 'deleteConn', connId: id });
    closeConnForm();
}

// --- Barra lateral: recolher e redimensionar (estilo VSCode)
const SB_MIN = 150;      // largura minima antes de colapsar ao arrastar
const SB_DEFAULT = 208;  // largura ao reexpandir
const SB_MAX = 480;

function refitVisible() {
    Object.values(sessions).forEach(s => {
        if (s.paneEl.offsetParent !== null) {
            scheduleFit(s);
        }
    });
}

function setSidebarWidth(px) {
    document.querySelector('.app').style.setProperty('--sb-w', px + 'px');
}

function toggleSidebar() {
    const app = document.querySelector('.app');
    app.classList.toggle('sb-collapsed');
    if (!app.classList.contains('sb-collapsed')) setSidebarWidth(SB_DEFAULT);
    setTimeout(refitVisible, 60);
}

function initSidebarResizer() {
    const app = document.querySelector('.app');
    const resizer = document.getElementById('sb-resizer');
    if (!resizer) return;

    resizer.addEventListener('mousedown', (e) => {
        e.preventDefault();
        resizer.classList.add('dragging');
        document.body.style.cursor = 'col-resize';
        document.body.style.userSelect = 'none';

        const onMove = (ev) => {
            let w = ev.clientX;  // sidebar comeca com x=0
            if (w < SB_MIN) {
                // arrastou abaixo do minimo -> vira rail de icones (nao some)
                app.classList.add('sb-collapsed');
            } else {
                app.classList.remove('sb-collapsed');
                setSidebarWidth(Math.min(w, SB_MAX));
            }
            refitVisible();
        };
        const onUp = () => {
            resizer.classList.remove('dragging');
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            setTimeout(refitVisible, 30);
        };
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    });

    // Duplo clique na alca: alterna recolher/expandir
    resizer.addEventListener('dblclick', toggleSidebar);
}

window.addEventListener('keydown', e => {
    if (e.key === 'Escape' && document.getElementById('overlay').classList.contains('show')) closeConnForm();
    if (e.key === 'Escape' && document.getElementById('snip-overlay').classList.contains('show')) closeSnipForm();
    // Ctlr+Shift+D: Dividir vertical (lado a lado); Ctrl+Shift+E: horizontal (empilhado)
    if (e.ctrlKey && e.shiftKey && focusedPane) {
        if (e.key === 'D' || e.key === 'd') { e.preventDefault(); splitPane(focusedPane, 'h'); }
        if (e.key === 'E' || e.key === 'e') { e.preventDefault(); splitPane(focusedPane, 'v'); }
    }
});

window.openGitBash = openGitBash;
window.openSsh = openSsh;
window.filterHosts = filterHosts;
window.openConnForm = openConnForm;
window.closeConnForm = closeConnForm;
window.setAuth = setAuth;
window.togglePw = togglePw;
window.pickKey = pickKey;
window.saveConn = saveConn;
window.deleteConn = deleteConn;
window.toggleSidebar = toggleSidebar;
window.toggleAppearance = toggleAppearance;
window.changeFont = changeFont;
window.resetFont = resetFont;
window.closeSnipForm = closeSnipForm;
window.saveSnippet = saveSnippet;
window.deleteSnippetFromForm = deleteSnippetFromForm;

// Inicializa o arraste do sidebar e carrega prefs + conexoes + pastas + snippets
// loadPrefs primeiro para que novas sessoes ja abram com o tema/fonte salvo
initSidebarResizer();
post({ type: 'loadPrefs' });
post({ type: 'loadConns' });
post({ type: 'loadFolders' });
post({ type: 'loadSnippets' });

// Attach DOM handlers that avoid inline event usage (prevents TS checking issues)
try {
    const search = document.getElementById('search-input');
    if (search) search.addEventListener('input', (e) => { try { filterHosts(e.target && e.target.value || ''); } catch(_){} });
    const overlay = document.getElementById('overlay');
    if (overlay) overlay.addEventListener('click', (e) => { if (e.target === e.currentTarget) closeConnForm(); });
    const snipOverlay = document.getElementById('snip-overlay');
    if (snipOverlay) snipOverlay.addEventListener('click', (e) => { if (e.target === e.currentTarget) closeSnipForm(); });
} catch(_) {}
