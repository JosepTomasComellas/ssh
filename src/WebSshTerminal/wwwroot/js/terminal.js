// Client del terminal. Sense dependències a part d'xterm.js. Cap contingut de terminal, fitxer o
// porta-retalls es desa ni s'envia enlloc més que al servidor SSH/SFTP configurat.
(function () {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const enc = new TextEncoder();
  const headers = { 'X-Requested-With': 'WebSsh' };

  const statusEl = $('status');
  const errorEl = $('terminal-error');
  const select = $('session-select');
  const btn = {
    newSession: $('btn-new'), connect: $('btn-connect'), disconnect: $('btn-disconnect'),
    close: $('btn-close'), copy: $('btn-copy'), paste: $('btn-paste'),
  };

  const term = new window.Terminal({
    cursorBlink: true,
    scrollback: 5000,
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
    fontSize: 15,
    convertEol: false,
    allowProposedApi: false,
  });
  const fit = new window.FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open($('terminal'));
  fit.fit();

  let ws = null;
  let currentId = null;
  let manualClose = false;
  let retries = 0;

  const setStatus = (text) => { statusEl.textContent = text; };
  const showError = (text) => { errorEl.textContent = text; errorEl.hidden = !text; };
  const setConnected = (on) => {
    btn.connect.disabled = on;
    btn.disconnect.disabled = !on;
  };

  async function api(method, url, body) {
    const res = await fetch(url, { method, headers, credentials: 'same-origin', body });
    return res;
  }

  async function refreshSessions(selectId) {
    const res = await api('GET', '/api/sessions');
    if (!res.ok) { showError('No s\'han pogut carregar les sessions.'); return; }
    const list = await res.json();
    select.replaceChildren(...list.map((s, i) => {
      const o = document.createElement('option');
      o.value = s.id;
      o.textContent = 'Sessió ' + (i + 1) + (s.attached ? ' (connectada)' : '') + ' · ' + s.id.slice(0, 8);
      return o;
    }));
    if (selectId) select.value = selectId;
  }

  async function newSession() {
    showError('');
    const res = await api('POST', '/api/sessions');
    if (res.status === 409) { showError('El teu compte no té cap identitat SSH assignada. Demana-ho a un administrador.'); return; }
    if (res.status === 429) { showError('Has arribat al límit de sessions simultànies.'); return; }
    if (!res.ok) { showError('No s\'ha pogut crear la sessió.'); return; }
    const { id } = await res.json();
    await refreshSessions(id);
    connect(id);
  }

  // Quan el WebSocket no arriba a obrir-se, el navegador no diu per què: es demana al servidor una diagnosi
  // (només categories segures, mai detalls del servidor ni de la credencial).
  const REASONS = {
    ok: 'El servidor SSH respon correctament, però el WebSocket no s\'obre: revisa el proxy (Traefik/Nginx) i els orígens permesos.',
    unreachable: 'L\'app no arriba al servidor SSH (port, adreça o firewall). Avisa l\'administrador.',
    hostkey: 'La clau del servidor SSH no coincideix amb la configurada (known_hosts). Avisa l\'administrador.',
    authfailed: 'El servidor SSH no accepta la clau d\'aquesta identitat (clau no autoritzada, compte incorrecte o restricció from=).',
    other: 'Error inesperat en connectar amb el servidor SSH. Avisa l\'administrador.',
    'no-identity': 'El teu compte no té cap identitat SSH assignada. Demana-ho a un administrador.',
    'not-found': 'Aquesta sessió ja no existeix. Crea\'n una de nova.',
    cooldown: 'Espera uns segons abans de tornar-ho a provar.',
  };

  async function diagnose(id) {
    setStatus('Connexió fallida');
    try {
      const res = await api('POST', '/api/sessions/' + id + '/diagnose');
      const body = await res.json().catch(() => ({}));
      const reason = res.status === 404 ? 'not-found' : res.status === 429 ? 'cooldown' : (body.reason || 'other');
      showError((REASONS[reason] || REASONS.other) + ' [codi: ' + reason + ']');
    } catch (_) {
      showError('No s\'ha pogut connectar. Comprova la teva connexió i torna-ho a provar.');
    }
  }
  function connect(id) {
    if (!id) { showError('Tria o crea una sessió.'); return; }
    disconnect(true);
    showError('');
    currentId = id;
    manualClose = false;
    setStatus('Connectant…');
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    let opened = false;
    ws = new WebSocket(proto + '://' + location.host + '/ws/terminal/' + id);
    ws.binaryType = 'arraybuffer';
    ws.onopen = () => {
      opened = true;
      retries = 0;
      setConnected(true);
      setStatus('Connectat');
      $('session-id').textContent = id.slice(0, 8);
      term.reset();
      fit.fit();
      sendResize();
      term.focus();
    };
    ws.onmessage = (ev) => { term.write(new Uint8Array(ev.data)); };
    ws.onclose = () => {
      setConnected(false);
      ws = null;
      if (manualClose) { setStatus('Desconnectat'); return; }
      if (!opened) { diagnose(id); return; } // no s'ha arribat a connectar: reintentar no serveix de res
      if (retries < 3) {
        retries += 1;
        setStatus('Connexió perduda. Reintentant (' + retries + '/3)…');
        setTimeout(() => connect(id), 1000 * retries);
      } else {
        setStatus('Connexió perduda');
        showError('No s\'ha pogut reconnectar. Prem «Connecta» per tornar-ho a provar.');
      }
    };
    ws.onerror = () => { /* onclose gestiona l'estat */ };
  }

  function disconnect(silent) {
    manualClose = true;
    if (ws) { try { ws.close(1000, 'client'); } catch (_) { /* ja tancat */ } }
    if (!silent) setStatus('Desconnectat');
  }

  function sendResize() {
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'resize', cols: term.cols, rows: term.rows }));
    }
  }

  // Evita la trampa de teclat (WCAG 2.1.2): xterm consumeix Tab per enviar-lo a la shell.
  let tabMovesFocus = false;
  const tabBtn = $('btn-tabfocus');
  const setTabMode = (on) => {
    tabMovesFocus = on;
    tabBtn.setAttribute('aria-pressed', String(on));
    setStatus(on ? 'Tab mou el focus fora del terminal' : 'Tab s\'envia a la shell');
  };
  tabBtn.addEventListener('click', () => setTabMode(!tabMovesFocus));
  term.attachCustomKeyEventHandler((ev) => {
    if (ev.type === 'keydown' && ev.ctrlKey && ev.shiftKey && ev.key.toLowerCase() === 'm') {
      setTabMode(!tabMovesFocus);
      return false;
    }
    if (ev.type === 'keydown' && ev.ctrlKey && ev.shiftKey && !ev.altKey && ev.key.toLowerCase() === 'c') {
      ev.preventDefault();
      copySelection(false);
      return false;
    }
    if (tabMovesFocus && ev.key === 'Tab') return false; // el navegador mou el focus
    return true;
  });

  term.onData((data) => {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(enc.encode(data));
  });
  term.onResize(sendResize);
  let resizeTimer = null;
  window.addEventListener('resize', () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => fit.fit(), 100);
  });

  btn.newSession.addEventListener('click', newSession);
  btn.connect.addEventListener('click', () => connect(select.value));
  btn.disconnect.addEventListener('click', () => disconnect(false));
  btn.close.addEventListener('click', async () => {
    const id = select.value;
    if (!id) return;
    disconnect(true);
    const res = await api('DELETE', '/api/sessions/' + id);
    if (!res.ok) showError('No s\'ha pogut tancar la sessió.');
    else setStatus('Sessió tancada');
    await refreshSessions();
  });

  // Porta-retalls: només per acció explícita de l'usuari (botó, drecera o clic dret); el contingut no es desa ni s'envia enlloc.
  async function copySelection(clearAfter) {
    const sel = term.getSelection();
    if (!sel) { setStatus('No hi ha cap selecció'); return; }
    try {
      await navigator.clipboard.writeText(sel);
      setStatus('Copiat');
      if (clearAfter) term.clearSelection();
    } catch (_) { showError('El navegador no ha permès copiar al porta-retalls.'); }
  }
  async function pasteFromClipboard() {
    try { term.paste(await navigator.clipboard.readText()); setStatus('Enganxat'); }
    catch (_) { showError('El navegador no ha permès llegir el porta-retalls. Prova Ctrl+Maj+V.'); }
  }
  btn.copy.addEventListener('click', () => copySelection(false));
  btn.paste.addEventListener('click', pasteFromClipboard);

  // Clic dret com a Windows Terminal/PuTTY: amb selecció la copia; sense selecció, enganxa.
  // Maj+clic dret obre el menú natiu del navegador.
  $('terminal').addEventListener('contextmenu', (ev) => {
    if (ev.shiftKey) return;
    ev.preventDefault();
    if (term.hasSelection()) copySelection(true); else pasteFromClipboard();
  });
  // ---- Fitxers ----
  const pathInput = $('files-path');
  const listEl = $('files-list');
  const filesStatus = $('files-status');
  const overwriteBox = $('overwrite-box');
  let pendingUpload = null;

  const join = (dir, name) => (dir ? dir.replace(/\/+$/, '') + '/' : '') + name;

  async function listFiles(keepStatus) {
    if (keepStatus !== true) filesStatus.textContent = '';
    const res = await api('GET', '/api/files/list?path=' + encodeURIComponent(pathInput.value));
    if (res.status === 400) { filesStatus.textContent = 'Ruta no permesa.'; return; }
    if (!res.ok) { filesStatus.textContent = 'No s\'ha pogut llistar la carpeta.'; return; }
    const items = await res.json();
    listEl.replaceChildren(...items.map((e) => {
      const li = document.createElement('li');
      if (e.directory) {
        const b = document.createElement('button');
        b.type = 'button';
        b.textContent = e.name + '/';
        b.addEventListener('click', () => { pathInput.value = join(pathInput.value, e.name); listFiles(); });
        li.append(b);
      } else if (e.link) {
        li.textContent = e.name + ' (enllaç simbòlic, no accessible)';
      } else {
        const a = document.createElement('a');
        a.href = '/api/files/download?path=' + encodeURIComponent(join(pathInput.value, e.name));
        a.textContent = e.name;
        a.setAttribute('download', e.name);
        li.append(a, document.createTextNode(' (' + e.size + ' B)'));
      }
      return li;
    }));
  }

  async function upload(file, overwrite) {
    const target = join(pathInput.value, file.name);
    const url = '/api/files/upload?path=' + encodeURIComponent(target) + '&overwrite=' + (overwrite ? 'true' : 'false');
    filesStatus.textContent = 'Pujant…';
    const res = await api('POST', url, file);
    if (res.status === 409) {
      const body = await res.json().catch(() => ({}));
      if (body.error === 'exists') { pendingUpload = file; overwriteBox.hidden = false; filesStatus.textContent = ''; return; }
      filesStatus.textContent = 'La destinació és una carpeta.'; return;
    }
    if (res.status === 413) { filesStatus.textContent = 'El fitxer supera la mida màxima permesa.'; return; }
    if (res.status === 400) { filesStatus.textContent = 'Nom o ruta no permesos.'; return; }
    if (!res.ok) { filesStatus.textContent = 'No s\'ha pogut pujar el fitxer.'; return; }
    await listFiles(true);
    filesStatus.textContent = 'Fitxer pujat.';
  }

  $('btn-files-list').addEventListener('click', () => listFiles());
  $('upload-file').addEventListener('change', (ev) => {
    const f = ev.target.files[0];
    if (f) upload(f, false);
    ev.target.value = '';
  });
  $('btn-overwrite').addEventListener('click', () => {
    overwriteBox.hidden = true;
    if (pendingUpload) { const f = pendingUpload; pendingUpload = null; upload(f, true); }
  });
  $('btn-overwrite-cancel').addEventListener('click', () => { overwriteBox.hidden = true; pendingUpload = null; filesStatus.textContent = 'Pujada cancel·lada.'; });

  window.addEventListener('beforeunload', () => { manualClose = true; if (ws) ws.close(1000, 'unload'); });

  // El panell de fitxers és amagat per defecte i es carrega només quan s'obre.
  const filesBtn = $('btn-files');
  const filesPanel = $('files-panel');
  const page = document.querySelector('.terminal-page');
  let filesLoaded = false;
  filesBtn.addEventListener('click', () => {
    const open = filesPanel.hidden;
    filesPanel.hidden = !open;
    page.classList.toggle('files-open', open);
    filesBtn.setAttribute('aria-expanded', String(open));
    if (open && !filesLoaded) { filesLoaded = true; listFiles(); }
    setTimeout(() => fit.fit(), 50); // el terminal canvia d'ample
  });

  refreshSessions();
})();
