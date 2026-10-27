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

  function connect(id) {
    if (!id) { showError('Tria o crea una sessió.'); return; }
    disconnect(true);
    showError('');
    currentId = id;
    manualClose = false;
    setStatus('Connectant…');
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    ws = new WebSocket(proto + '://' + location.host + '/ws/terminal/' + id);
    ws.binaryType = 'arraybuffer';
    ws.onopen = () => {
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

  // Porta-retalls: només per acció explícita de l'usuari (clic); el contingut no es desa.
  btn.copy.addEventListener('click', async () => {
    const sel = term.getSelection();
    if (!sel) { setStatus('No hi ha cap selecció'); return; }
    try { await navigator.clipboard.writeText(sel); setStatus('Copiat'); }
    catch (_) { showError('El navegador no ha permès copiar al porta-retalls.'); }
  });
  btn.paste.addEventListener('click', async () => {
    try { term.paste(await navigator.clipboard.readText()); }
    catch (_) { showError('El navegador no ha permès llegir el porta-retalls.'); }
  });

  // ---- Fitxers ----
  const pathInput = $('files-path');
  const listEl = $('files-list');
  const filesStatus = $('files-status');
  const overwriteBox = $('overwrite-box');
  let pendingUpload = null;

  const join = (dir, name) => (dir ? dir.replace(/\/+$/, '') + '/' : '') + name;

  async function listFiles() {
    filesStatus.textContent = '';
    const res = await api('GET', '/api/files/list?path=' + encodeURIComponent(pathInput.value));
    if (res.status === 400) { filesStatus.textContent = 'Ruta no permitida.'; return; }
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
    filesStatus.textContent = 'Fitxer pujat.';
    listFiles();
  }

  $('btn-files-list').addEventListener('click', listFiles);
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

  refreshSessions().then(() => { listFiles(); });
})();
