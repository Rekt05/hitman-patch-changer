const element = (id) => document.getElementById(id);
const state = { patches: [], ui: null, pendingSwitch: null };

async function api(path, opts) {
  const res = await fetch(path, {
    headers: { "content-type": "application/json" },
    ...opts,
    body: opts?.body ? JSON.stringify(opts.body) : undefined
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || res.statusText);
  return data;
}

function installReady(s) {
  return !!s?.config?.installDir;
}

function toolReady(s) {
  if (!s?.config?.toolDir) return false;
  if (s.depotDownloader === "failed") return false;
  if (s.toolDirExists === false) return true;
  if (s.depotDownloader === "ready") return true;
  return !!s.toolDirExists;
}

function setupReady(s) {
  return installReady(s) && toolReady(s);
}

function authWait(sess) {
  return !!sess?.busy && ["guard", "email", "mobile", "qr", "password"].includes(sess.prompt || "none");
}

function switchDownloadInProgress(sess) {
  return !!sess?.busy && sess.job === "switch" && !authWait(sess);
}

function syncActions() {
  const ready = setupReady(state.ui);
  const locked = switchDownloadInProgress(state.ui?.session);
  element("btn-login").disabled = !ready || locked;
  element("btn-qr").disabled = !ready || locked;
  element("btn-custom").disabled = !ready || locked;
  const wait = locked ? "A download is in progress. Cancel it first." : "";
  element("btn-login").title = wait;
  element("btn-qr").title = wait;
  element("btn-custom").title = wait;
}

function applyState(s) {
  if (!s || !s.config) return;
  state.ui = s;
  const cfg = s.config;
  const linux = s.platform === "linux";
  element("install-dir").placeholder = linux ? "/home/you/HitmanDownpatch" : "C:\\HitmanDownpatch";
  element("tool-path").placeholder = linux ? "/home/you/DepotDownloader" : "C:\\DepotDownloader";
  element("tool-help").textContent = linux
    ? "Choose the folder that contains the DepotDownloader binary from the Linux zip. An empty folder is fine if you don't have it yet, just select a folder and click Install afterwards."
    : "Choose the folder that contains DepotDownloader.exe. An empty folder is fine if you don't have it yet, just select a folder and click Install afterwards.";
  if (document.activeElement !== element("install-dir")) element("install-dir").value = cfg.installDir || "";
  if (document.activeElement !== element("username")) element("username").value = cfg.username || "";
  const toolInput = element("tool-path");
  if (document.activeElement !== toolInput) toolInput.value = cfg.toolDir || "";

  element("status-install").textContent = !cfg.installDir ? "not set"
    : (!s.installExists ? "missing" : (s.installCompatible === false ? "no game" : "ready"));
  element("status-tool").textContent = !cfg.toolDir ? "not set"
    : (s.toolDirExists === false ? "missing"
      : (s.depotDownloader === "downloading" ? "downloading"
        : (s.depotDownloader === "failed" ? "won't start"
          : (s.depotDownloader === "ready" ? "ready" : (s.toolDirExists ? "not installed" : "missing")))));
  const installMissing = !!(cfg.installDir && !s.installExists);
  const installWrong = !!(cfg.installDir && s.installExists && s.installCompatible === false);
  element("install-anew").hidden = !(installMissing || installWrong);
  element("install-anew").textContent = installMissing
    ? "This folder doesn't exist yet, it will be created and the game will be installed here"
    : "This folder doesn't contain the game, the game will be installed here";
  const toolNeedsInstall = s.toolDirExists === false
    || (s.toolDirExists && s.depotDownloader !== "ready" && s.depotDownloader !== "failed" && s.depotDownloader !== "downloading");
  element("tool-anew").hidden = !(cfg.toolDir && toolNeedsInstall);
  element("tool-anew").textContent = s.toolDirExists === false
    ? "This folder doesn't exist yet. Click Install to create the folder and install it there"
    : "DepotDownloader isn't in this folder yet. Click Install to install it here";
  element("status-version").textContent = s.detectedVersion || "unknown";
  element("status-store").textContent = s.sessionLikelyValid ? "signed in" : "not signed in";
  element("setup-hint").hidden = setupReady(s);
  renderSession(s.session || {});
  renderPatches();
}

function renderSession(sess) {
  const prompt = element("prompt-card");
  const job = element("job-card");
  const kind = sess.prompt || "none";
  const busy = !!sess.busy;
  job.hidden = !busy && !sess.lastError && sess.phase !== "done";
  element("job-title").textContent = busy
    ? (sess.phase === "retrying" ? "Waiting to retry..."
      : sess.job === "login" ? "Signing in..." : "Switching...")
    : (sess.success ? "Done" : (sess.lastError ? "Stopped" : "Job"));
  element("job-cmd").textContent = sess.commandPreview || sess.lastLine || "";
  element("btn-cancel").hidden = !busy;
  if (sess.percent) {
    element("meter").hidden = false;
    element("meter-bar").style.width = `${sess.percent}%`;
  } else {
    element("meter").hidden = true;
  }
  const err = element("job-error");
  if (sess.lastError) {
    err.hidden = false;
    err.classList.toggle("warn", sess.lastErrorCode === "cdn");
    err.textContent = sess.lastError;
    job.hidden = false;
  } else if (!err.classList.contains("warn")) {
    err.hidden = true;
  }

  const needsUi = busy && ["guard", "email", "mobile", "qr", "password"].includes(kind);
  prompt.hidden = !needsUi;
  element("qr-wait").hidden = kind !== "qr" || (sess.qrModules && sess.qrModules.length >= 21);
  const titles = {
    guard: "Sign in to continue",
    email: "Sign in to continue",
    mobile: "Approve in Steam mobile",
    qr: "Log in via QR code",
    password: "Sign in to continue"
  };
  element("prompt-title").textContent = titles[kind] || "Sign in";
  element("prompt-body").textContent = sess.promptDetail
    || (kind === "qr" ? "Scan the qr code using the steam app on your phone" : "")
    || (kind === "mobile" ? "Open the Steam app and approve the sign-in." : "");
  if (kind === "qr") drawQr(sess.qrModules);
  else element("qr-canvas").hidden = true;
  syncActions();
  if (!busy) renderPatches();
  watchBusy(sess);
}

function drawQr(modules) {
  const canvas = element("qr-canvas");
  if (!modules || modules.length < 21) {
    canvas.hidden = true;
    return;
  }
  const n = modules.length;
  const scale = Math.max(4, Math.min(8, Math.floor(280 / n)));
  canvas.hidden = false;
  canvas.width = n * scale;
  canvas.height = n * scale;
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = "#fff";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = "#000";
  for (let y = 0; y < n; y++) {
    const row = modules[y] || [];
    for (let x = 0; x < row.length; x++) {
      if (row[x]) ctx.fillRect(x * scale, y * scale, scale, scale);
    }
  }
}

function renderPatches() {
  const list = element("patches");
  const current = state.ui?.detectedVersion;
  const ready = setupReady(state.ui);
  list.innerHTML = "";
  for (const p of state.patches) {
    if (!p.steamManifestId) continue;
    const row = document.createElement("div");
    row.className = "patch";
    if (current && p.version === current) row.classList.add("current");
    const label = p.id !== p.version ? p.id : p.version;
    const locked = switchDownloadInProgress(state.ui?.session);
    const btn = document.createElement("button");
    btn.type = "button";
    btn.textContent = "Switch";
    btn.disabled = !ready || locked;
    btn.title = locked ? "A download is in progress. Cancel it first." : "";
    btn.onclick = () => confirmSwitch(p);
    const name = document.createElement("strong");
    name.textContent = label;
    row.append(name, btn);
    list.appendChild(row);
  }
}

function confirmSwitch(patch, manifestId) {
  const dialog = element("confirm");
  const label = patch?.id || "custom";
  element("confirm-title").textContent = `Switch to ${label}?`;
  element("confirm-ok").textContent = "Switch";
  state.pendingSwitch = { patchId: patch?.id, manifestId: manifestId || patch?.steamManifestId };
  dialog.showModal();
}

element("confirm").addEventListener("close", async () => {
  const pendingSwitch = state.pendingSwitch;
  state.pendingSwitch = null;
  if (element("confirm").returnValue !== "ok") return;
  if (!pendingSwitch) return;
  await saveSettings();
  try {
    await api("/api/switch", { method: "POST", body: { patchId: pendingSwitch.patchId, manifestId: pendingSwitch.manifestId, confirm: true } });
  } catch (e) {
    alert(e.message);
  }
});

element("settings-form").onsubmit = (e) => e.preventDefault();

const pathSaveVersion = { install: 0, tool: 0 };
const pathSaveTimer = {};

function watchPath(id, which) {
  const input = element(id);
  const save = async () => {
    const saveVersion = ++pathSaveVersion[which];
    const body = which === "install"
      ? { installDir: input.value }
      : { toolDir: input.value };
    try {
      const s = await api("/api/settings", { method: "POST", body });
      if (saveVersion !== pathSaveVersion[which]) return;
      applyState(s);
    } catch { }
  };
  input.addEventListener("input", () => {
    clearTimeout(pathSaveTimer[which]);
    pathSaveTimer[which] = setTimeout(save, 200);
  });
}

watchPath("install-dir", "install");
watchPath("tool-path", "tool");

async function saveSettings() {
  return api("/api/settings", {
    method: "POST",
    body: {
      installDir: element("install-dir").value,
      toolDir: element("tool-path").value,
      username: element("username").value,
      createIfMissing: true
    }
  });
}

async function login({ qr = false } = {}) {
  try {
    await saveSettings();
    await api("/api/login", {
      method: "POST",
      body: { username: qr ? null : element("username").value, password: qr ? null : element("password").value, qr }
    });
    element("password").value = "";
  } catch (e) {
    alert(e.message);
  }
}

element("btn-login").onclick = () => login({});
element("btn-qr").onclick = () => login({ qr: true });
element("btn-custom").onclick = () => {
  const id = element("custom-manifest").value.trim();
  if (!id) return;
  confirmSwitch(null, id);
};
element("btn-cancel").onclick = () => api("/api/cancel", { method: "POST", body: {} });
element("btn-open").onclick = () => api("/api/open-folder", { method: "POST", body: { which: "install" } });
element("btn-tool-download").onclick = async () => {
  const btn = element("btn-tool-download");
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Installing...";
  try {
    await saveSettings();
    applyState(await api("/api/setup-tool", { method: "POST", body: {} }));
  } catch (e) {
    alert(e.message);
  } finally {
    btn.disabled = false;
    btn.textContent = label;
  }
};

async function browse(kind) {
  const buttons = [element("btn-tool-browse"), element("btn-browse-dir")];
  buttons.forEach(b => b.disabled = true);
  try {
    return await api("/api/browse", { method: "POST", body: { kind } });
  } finally {
    buttons.forEach(b => b.disabled = false);
  }
}

element("btn-tool-browse").onclick = async () => {
  try {
    const res = await browse("folder");
    if (res.path) {
      element("tool-path").value = res.path;
      applyState(await api("/api/setup-tool", { method: "POST", body: { path: res.path } }));
    }
  } catch (e) {
    alert(e.message);
  }
};
element("btn-browse-dir").onclick = async () => {
  try {
    const res = await browse("folder");
    if (res.path) {
      element("install-dir").value = res.path;
      element("install-dir").dispatchEvent(new Event("input"));
    }
  } catch (e) {
    alert(e.message);
  }
};
function appendLog(line) {
  const el = element("log");
  el.textContent += (line.text || "") + "\n";
  el.scrollTop = el.scrollHeight;
}

let refreshTimer;
function refreshState() {
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(async () => {
    try { applyState(await api("/api/state")); } catch { }
  }, 400);
}

let busyPoll;
function watchBusy(sess) {
  if (!sess?.busy) {
    clearInterval(busyPoll);
    busyPoll = null;
    return;
  }
  if (busyPoll) return;
  busyPoll = setInterval(async () => {
    try {
      const s = await api("/api/state");
      if (!s.session?.busy) {
        clearInterval(busyPoll);
        busyPoll = null;
        applyState(s);
      }
    } catch { }
  }, 2000);
}

async function boot() {
  const [patches, ui, logs] = await Promise.all([
    api("/api/patches"),
    api("/api/state"),
    api("/api/logs")
  ]);
  state.patches = patches.patches || [];
  applyState(ui);
  for (const line of logs) appendLog(line);
  const es = new EventSource("/api/events");
  es.addEventListener("state", (ev) => {
    const data = JSON.parse(ev.data);
    if (data.config) { applyState(data); return; }
    if (!state.ui) return;
    const wasLocked = switchDownloadInProgress(state.ui.session);
    const wasBusy = !!state.ui.session?.busy;
    state.ui.session = data;
    renderSession(data);
    if (switchDownloadInProgress(data) !== wasLocked) renderPatches();
    if (wasBusy && !data.busy) refreshState();
  });
  es.addEventListener("log", (ev) => appendLog(JSON.parse(ev.data)));
}

boot().catch((e) => {
  element("status-install").textContent = e.message;
});
