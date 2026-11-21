/* Local-only controller. All assets and commands stay on the laptop's LAN. */
(() => {
  "use strict";
  const t = window.RemoteTranslate || (text => text);
  const $ = id => document.getElementById(id);
  const pad = $("touchpad"), pairing = $("pairing"), code = $("code");
  const controls = $('controlScreen'), openControls = $('openControls');
  const connectButton = $("connect"), disconnectButton = $("disconnect");
  const volumeButtons = [...document.querySelectorAll("[data-volume]")];
  const themeKey = 'remote.theme';
  const lightTheme = $('themeLight'), darkTheme = $('themeDark');

  function applyTheme(theme) {
    const light = theme === 'light';
    document.documentElement.dataset.theme = light ? 'light' : 'dark';
    $('themeColor').content = light ? '#e9eefa' : '#202c43';
    lightTheme.checked = light;
    darkTheme.checked = !light;
  }

  let savedTheme;
  try { savedTheme = localStorage.getItem(themeKey); } catch (_) { }
  applyTheme(savedTheme);
  for (const option of [lightTheme, darkTheme]) option.addEventListener('change', () => {
    if (!option.checked) return;
    applyTheme(option.value);
    try { localStorage.setItem(themeKey, option.value); } catch (_) { }
  });

  const capturedPointers = new Set();
  const screenViewer = new RemoteScreen.Viewer({
    image: $('screenImage'), control: message => send(message),
    schedule: (fn, delay) => setTimeout(fn, delay), cancel: timer => clearTimeout(timer),
    socketFactory: () => new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws`),
    size: () => { const rect = pad.getBoundingClientRect(); return { width: rect.width, height: rect.height }; },
    changed: viewer => {
      $('screenEnabled').disabled = !viewer.supported;
      $('screenEnabled').checked = viewer.enabled;
      pad.classList.toggle('screen-active', viewer.running);
      $('screenStatus').hidden = !viewer.enabled && !viewer.message;
      const status = t(viewer.message) || (viewer.running ? (viewer.fps ? viewer.fps + t(' кадр/с') : t('Трансляция · до 60 кадр/с')) : t('Подключаем экран…'));
      if ($('screenStatus').textContent !== status) $('screenStatus').textContent = status;
    }
  });
  function updateScreenVisibility() { screenViewer.setVisible(!controls.open && !$('keyboardDialog').open && !document.hidden); }
  $('screenEnabled').addEventListener('change', () => screenViewer.toggle($('screenEnabled').checked));
  // Handle a completed finger tap directly; Safari may omit the later click
  // after a multi-touch gesture. Keep native mouse/keyboard activation too.
  function activateButton(button, action) {
    let touch = null, lastTouchEnd = -Infinity;
    button.addEventListener('pointerdown', event => {
      if (event.pointerType !== 'touch' && event.pointerType !== 'pen') { lastTouchEnd = -Infinity; return; }
      if (button.disabled) return;
      touch = { id: event.pointerId, x: event.clientX, y: event.clientY, moved: false };
    });
    button.addEventListener('pointermove', event => {
      if (touch && touch.id === event.pointerId && Math.hypot(event.clientX - touch.x, event.clientY - touch.y) > 14) touch.moved = true;
    });
    button.addEventListener('pointercancel', () => { touch = null; lastTouchEnd = Date.now(); });
    button.addEventListener('pointerup', event => {
      if (!touch || touch.id !== event.pointerId) return;
      const tap = touch; touch = null; lastTouchEnd = Date.now();
      const rect = button.getBoundingClientRect();
      const inside = event.clientX >= rect.left && event.clientX <= rect.right && event.clientY >= rect.top && event.clientY <= rect.bottom;
      event.preventDefault();
      if (!button.disabled && !tap.moved && inside && Math.hypot(event.clientX - tap.x, event.clientY - tap.y) <= 14) action();
    });
    button.addEventListener('click', event => {
      if (Date.now() - lastTouchEnd < 800 && (event.detail !== 0 || event.pointerType === 'touch' || event.pointerType === 'pen')) { event.preventDefault(); return; }
      if (!button.disabled) action();
    });
  }
  let lastAppHeight = 0;
  let keyboardSupported = false, keyboardActive = false, keyboardClosing = false, keyboardComposing = false;
  let confirmedText = '', textFlight = null, textSequence = 0, keyboardTimer = null;
  let magnifierSupported = false, magnifierEnabled = false, magnifierFlight = null, magnifierTimer = null;
  let magnifierApplied = '', magnifierRadius = 160, magnifierZoom = 200, magnifierMode = 'center';
  try {
    const prefs = JSON.parse(localStorage.getItem('remote.magnifier') || 'null');
    if (prefs && Number.isInteger(prefs.radius) && prefs.radius >= 80 && prefs.radius <= 280
      && Number.isInteger(prefs.zoom) && prefs.zoom >= 150 && prefs.zoom <= 400
      && ['center', 'offset'].includes(prefs.mode)) {
      magnifierRadius = prefs.radius; magnifierZoom = prefs.zoom; magnifierMode = prefs.mode;
    }
  } catch (_) { }
  $('magnifierRadius').value = magnifierRadius;
  $('magnifierZoom').value = magnifierZoom;
  $('magnifierMode').value = magnifierMode;
  function renderMagnifier() {
    $('magnifierEnabled').disabled = !ready || !magnifierSupported;
    $('magnifierEnabled').checked = magnifierEnabled;
    $('magnifierRadiusValue').value = magnifierRadius + ' px';
    $('magnifierZoomValue').value = (magnifierZoom / 100).toFixed(1) + '×';
    $('magnifierStatus').textContent = !ready ? t('Подключитесь к ноутбуку, чтобы включить лупу.')
      : !magnifierSupported ? t('Обновите приложение на ноутбуке для поддержки лупы.')
      : magnifierFlight ? t('Применяем…') : magnifierEnabled ? t('Лупа включена. При отключении телефона она выключится.') : t('Лупа выключена. Включить можно на главном экране. Настройки сохраняются.');
  }
  function pumpMagnifier() {
    if (!ready || !magnifierSupported || magnifierFlight) return;
    const command = { type: 'magnifier', enabled: magnifierEnabled, radius: magnifierRadius, zoom: magnifierZoom, mode: magnifierMode };
    const key = JSON.stringify(command);
    if (key === magnifierApplied || (!magnifierEnabled && !magnifierApplied)) return;
    magnifierFlight = key;
    if (!send(command)) return;
    magnifierTimer = setTimeout(() => connectionFailed(t('Лупа не отвечает. Подключитесь снова.'), false), 3500);
    renderMagnifier();
  }
  $('magnifierEnabled').addEventListener('change', () => {
    if (!ready || !magnifierSupported) return;
    magnifierEnabled = $('magnifierEnabled').checked;
    pumpMagnifier(); renderMagnifier();
  });
  for (const id of ['magnifierRadius', 'magnifierZoom', 'magnifierMode']) $(id).addEventListener(id === 'magnifierMode' ? 'change' : 'input', () => {
    magnifierRadius = Math.max(80, Math.min(280, Math.round(Number($('magnifierRadius').value) || 160)));
    magnifierZoom = Math.max(150, Math.min(400, Math.round(Number($('magnifierZoom').value) || 200)));
    magnifierMode = $('magnifierMode').value === 'offset' ? 'offset' : 'center';
    try { localStorage.setItem('remote.magnifier', JSON.stringify({ radius: magnifierRadius, zoom: magnifierZoom, mode: magnifierMode })); } catch (_) { }
    pumpMagnifier(); renderMagnifier();
  });
  function updateViewport() {
    document.documentElement.style.setProperty('--home-gesture-min', navigator.standalone === true ? '34px' : '0px');
    const screen = window.screen || {};
    const orientation = screen.orientation && screen.orientation.type;
    const landscape = orientation ? orientation.startsWith('landscape') : Math.abs(window.orientation || 0) === 90;
    const height = RemoteViewport.appHeight({ innerHeight: window.innerHeight,
      visualHeight: window.visualViewport && window.visualViewport.height,
      screenWidth: screen.width, screenHeight: screen.height, landscape,
      standalone: typeof navigator !== 'undefined' && navigator.standalone === true });
    const visual = window.visualViewport;
    const visibleHeight = visual && visual.height > 0 ? visual.height : window.innerHeight;
    if (visibleHeight > 0) document.documentElement.style.setProperty('--visual-height', visibleHeight + 'px');
    document.documentElement.style.setProperty('--visual-top', Math.max(0, visual && visual.offsetTop || 0) + 'px');
    if (height && height !== lastAppHeight) {
      document.documentElement.style.setProperty('--app-height', height + 'px');
      lastAppHeight = height;
    }
  }
  let socket = null, ready = false, connecting = false, generation = 0;
  let heartbeat = null, handshakeTimeout = null, lastReply = 0;
  let wheel = 0, sensitivity = 1.5, smoothing = true;
  const motion = new RemoteGestures.MotionFilter();
  let pinchScale = null, pinching = false, pinchSupported = false, dragSupported = false;
  const deviceStorageKey = 'remote.device.v1';
  let remembered = null, userStopped = false, retryTimer = null, retryDelay = 2000;
  const validCredential = value => value && typeof value.deviceId === 'string' && typeof value.token === 'string'
    && /^[0-9a-f]{32}$/.test(value.deviceId) && /^[0-9A-F]{64}$/.test(value.token);
  try {
    const saved = Number(localStorage.getItem("remote.sensitivity"));
    if (saved >= 0.5 && saved <= 3) sensitivity = saved;
    smoothing = localStorage.getItem('remote.smoothing') !== 'off';
    const device = JSON.parse(localStorage.getItem(deviceStorageKey) || 'null');
    if (validCredential(device)) remembered = device;
  } catch (_) { /* Safari can restrict storage. The controller still works. */ }
  $("sensitivity").value = sensitivity;
  $("sensitivityValue").value = sensitivity.toFixed(1) + "×";
  $("smoothing").checked = smoothing;

  function render(message) {
    $("status").textContent = t(message);
    $("pairStatus").textContent = t(message);
    $("badge").classList.toggle("online", ready);
    $("badgeText").textContent = ready ? t("В сети") : connecting ? t("Связь…") : t("Офлайн");
    $("touchConnection").textContent = ready ? t('Подключено') : connecting ? t('Подключение…') : t('Офлайн');
    $("touchConnection").classList.toggle('online', ready);
    pad.classList.toggle("ready", ready);
    connectButton.disabled = ready || connecting;
    disconnectButton.disabled = !ready && !connecting && !retryTimer;
    volumeButtons.forEach(button => { button.disabled = !ready; });
    $("pairSubmit").disabled = connecting;
    $("pairSubmit").textContent = connecting ? t("Подключение…") : t("Подключиться");
    code.disabled = connecting;
    $("remember").disabled = connecting;
    $("deviceName").disabled = connecting;
    $("connectLabel").textContent = ready ? t('Подключено') : remembered ? t('Подключить без кода') : t('Подключить ноутбук');
    $("rememberedInfo").hidden = !remembered;
    renderKeyboard();
    renderMagnifier();
  }
  function renderKeyboard() {
    $("openKeyboard").disabled = !ready || !keyboardSupported || keyboardClosing || (!!textFlight && !keyboardActive);
    $("keyboardText").readOnly = !ready || !keyboardActive;
    $("keyboardText").disabled = keyboardClosing;
    $("keyboardDone").disabled = keyboardClosing;
    $("closeKeyboard").disabled = keyboardClosing;
  }
  function stopKeyboard(message) {
    clearTimeout(keyboardTimer); keyboardTimer = null;
    keyboardActive = false; keyboardClosing = false; keyboardComposing = false; textFlight = null;
    if ($("keyboardDialog").open) $("keyboardStatus").textContent = t(message);
    renderKeyboard();
  }
  function closeKeyboardNow() {
    keyboardActive = false; keyboardClosing = false; keyboardComposing = false;
    $("keyboardText").blur(); $("keyboardDialog").close();
    updateScreenVisibility();
    renderKeyboard(); updateViewport();
  }
  function pumpKeyboard() {
    if (!ready || !keyboardActive || !keyboardSupported || keyboardComposing || textFlight) return;
    const field = $("keyboardText");
    const desired = RemoteKeyboard.normalize(field.value);
    if (desired !== field.value) field.value = desired;
    const edit = RemoteKeyboard.nextEdit(confirmedText, desired);
    if (!edit) {
      $("keyboardStatus").textContent = desired ? t('Текст введён на ноутбуке.') : t('Начните печатать на клавиатуре iPhone.');
      if (keyboardClosing) closeKeyboardNow();
      return;
    }
    const sequence = ++textSequence;
    textFlight = { seq: sequence, result: edit.result };
    if (!send({type:'text_edit', seq:sequence, deletions:edit.deletions, text:edit.text})) return;
    $("keyboardStatus").textContent = t('Ввод на ноутбук…');
    keyboardTimer = setTimeout(() => {
      if (textFlight && textFlight.seq === sequence) connectionFailed(t('Нет подтверждения ввода. Проверьте соединение.'));
    }, 3500);
  }
  function requestKeyboardClose() {
    if (!keyboardActive || !ready) { closeKeyboardNow(); return; }
    keyboardComposing = false; keyboardClosing = true;
    $("keyboardText").blur(); renderKeyboard(); pumpKeyboard();
  }
  function disconnect(message = t("Отключено. Можно подключиться заново.")) {
    generation++;
    clearInterval(heartbeat); clearTimeout(handshakeTimeout);
    heartbeat = null; handshakeTimeout = null;
    clearTimeout(retryTimer); retryTimer = null;
    const previous = socket;
    socket = null; ready = false; connecting = false;
    screenViewer.setSupported(false);
    clearTimeout(magnifierTimer); magnifierTimer = null;
    magnifierSupported = false; magnifierEnabled = false; magnifierFlight = null; magnifierApplied = '';
    stopKeyboard(t('Связь прервана. Закройте и откройте клавиатуру после подключения, чтобы начать новый ввод.'));
    keyboardSupported = false; textSequence = 0; confirmedText = '';
    if (previous) {
      previous.onopen = previous.onmessage = previous.onerror = previous.onclose = null;
      previous.close();
    }
    motion.clear(); wheel = 0;
    pinchScale = null; pinching = false; pinchSupported = false;
    cancelGesture(); dragSupported = false;
    render(message);
  }
  function send(message, handshake = false) {
    if ((!ready && !handshake) || !socket || socket.readyState !== WebSocket.OPEN) return false;
    if (socket.bufferedAmount > 16384) { connectionFailed(t("Сеть перегружена. Повторное подключение при восстановлении связи.")); return false; }
    try { socket.send(JSON.stringify(message)); return true; }
    catch (_) { connectionFailed(t("Не удалось отправить команду. Проверяю связь…")); return false; }
  }
  function removeRemembered() {
    remembered = null;
    try { localStorage.removeItem(deviceStorageKey); } catch (_) { }
  }
  function scheduleReconnect(delay = retryDelay) {
    if (!remembered || userStopped || document.hidden || (typeof navigator !== 'undefined' && navigator.onLine === false)) return;
    clearTimeout(retryTimer);
    retryTimer = setTimeout(() => { retryTimer = null; connectSaved(); }, delay);
    disconnectButton.disabled = false;
    retryDelay = Math.min(15000, retryDelay * 2);
  }
  function connectionFailed(message, retry = true, delay) {
    disconnect(message);
    if (retry) scheduleReconnect(delay);
  }
  function flush(immediate = true, elapsed = 1000 / 60) {
    if (!ready || !socket) return;
    // Drop stale motion rather than replaying a jump after network congestion.
    if (socket.bufferedAmount > 4096) { motion.clear(); wheel = 0; return; }
    const { dx: x, dy: y } = motion.step(elapsed, immediate || !smoothing);
    const delta = Math.trunc(wheel);
    if (x || y) send({ type: "move", dx: x, dy: y });
    if (delta) { wheel -= delta; send({ type: "scroll", delta }); }
    if (pinching && pinchScale !== null) {
      const scale = pinchScale; pinchScale = null;
      send({ type: "pinch", phase: "update", scale });
    }
  }
  const clamp = (n, max) => Math.max(-max, Math.min(max, n));
  const gestures = new RemoteGestures.GestureTracker({
    move(x, y) { if (ready) motion.add(x * sensitivity, y * sensitivity); },
    scroll(y) { if (ready) wheel = clamp(wheel + y * 5, 1200); },
    click(right) { if (ready) { flush(); send({ type: "click", button: right ? "right" : "left" }); } },
    drag(phase) {
      if (!ready || !dragSupported) return;
      flush(); send({ type: 'drag', phase });
    },
    dragLock(waiting) {
      $('padState').textContent = waiting ? t('ПЕРЕСТАВЬТЕ ПАЛЕЦ') : '';
      $('padState').hidden = !waiting;
    },
    pinch(phase, scale) {
      if (!ready) return;
      if (!pinchSupported) {
        if (phase === 'start') render(t('Эта версия Windows не поддерживает виртуальный precision-тачпад.'));
        return;
      }
      if (phase === 'start') {
        flush(); motion.clear(); wheel = 0;
        pinching = send({ type: 'pinch', phase: 'start' });
      } else if (phase === 'update' && pinching) {
        pinchScale = Math.max(100, Math.min(4000, Math.round(scale * 1000)));
      } else if (phase === 'end' && pinching) {
        flush(); send({ type: 'pinch', phase: 'end' });
        pinching = false; pinchScale = null;
      }
    }
  }, { schedule: (fn, delay) => setTimeout(fn, delay), cancel: timer => clearTimeout(timer) });
  let lastFrame = null;
  function frame(time) {
    const elapsed = lastFrame === null ? 1000 / 60 : time - lastFrame;
    lastFrame = time;
    // A strict 16.667 ms gate skips frames when a 60 Hz display's intervals
    // are slightly shorter. Send on every display frame instead.
    flush(false, elapsed);
    requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);

  function connectSaved() {
    if (!remembered || userStopped || document.hidden || ready || connecting) return;
    connect(true);
  }
  function connect(saved = false) {
    if (saved && !remembered) return;
    const pairingCode = code.value.trim().toUpperCase();
    if (!saved && !/^[0-9A-F]{12}$/.test(pairingCode)) { $("pairStatus").textContent = t("Введите 12 символов кода: 0–9, A–F."); return; }
    disconnect("");
    const hello = saved ? { type: 'hello', version: 1, deviceId: remembered.deviceId, token: remembered.token }
      : { type: 'hello', version: 1, code: pairingCode };
    if (!saved && $("remember").checked) { hello.remember = true; hello.deviceName = $("deviceName").value.trim() || t('Мой iPhone'); }
    connecting = true;
    render(t("Подключение к ноутбуку…"));
    const current = generation;
    try { socket = new WebSocket(`${location.protocol === "https:" ? "wss:" : "ws:"}//${location.host}/ws`); }
    catch (_) { disconnect(t("Не удалось открыть соединение. Откройте адрес из окна PocketPointer.")); return; }
    socket.onopen = () => { if (current === generation) send(hello, true); };
    socket.onmessage = event => {
      if (current !== generation) return;
      let message;
      try { if (typeof event.data !== "string" || event.data.length > 1024) throw new Error(); message = JSON.parse(event.data); }
      catch (_) { disconnect(t("Некорректный ответ ноутбука.")); return; }
      if (message?.type === "error") {
        if (message.code === 'invalid_device') removeRemembered();
        if (message.code === 'disconnected') userStopped = true;
        connectionFailed(typeof message.message === "string" ? message.message : t("Подключение отклонено."),
          saved && ['busy', 'rate_limited'].includes(message.code), message.code === 'rate_limited' ? 30000 : 15000);
        return;
      }
      if (message?.type === "ready" && connecting && message.version === 1) {
        clearTimeout(handshakeTimeout);
        ready = true; connecting = false; lastReply = Date.now();
        pinchSupported = message.pinch === true;
        dragSupported = message.drag === true;
        gestures.dragEnabled = dragSupported;
        keyboardSupported = message.keyboard === true;
        magnifierSupported = message.magnifier === true;
        screenViewer.setSupported(message.screen === true);
        let storageFailed = false;
        if (message.remembered === true && validCredential(message)) {
          remembered = { deviceId: message.deviceId, token: message.token };
          try { localStorage.setItem(deviceStorageKey, JSON.stringify(remembered)); } catch (_) { storageFailed = true; }
        }
        retryDelay = 2000;
        $("computer").textContent = typeof message.name === "string" ? message.name : "Windows 11";
        code.value = "";
        pairing.close();
        render(storageFailed ? t('Подключено, но браузер не сохранил ключ. После закрытия страницы понадобится код.')
          : remembered ? t('Подключено. Телефон запомнен — следующий раз без кода.') : t("Подключено. Ноутбук готов к управлению."));
        heartbeat = setInterval(() => {
          if (Date.now() - lastReply > 7000) connectionFailed(t("Связь потеряна. Ожидаю ноутбук…"));
          else send({ type: "ping" });
        }, 2000);
      } else if (['screen_ready', 'screen_stopped'].includes(message?.type) && ready) {
        screenViewer.accept(message);
      } else if (message?.type === 'magnifier_state' && ready && magnifierFlight) {
        if (message.enabled !== JSON.parse(magnifierFlight).enabled) { connectionFailed(t('Некорректное подтверждение лупы.'), false); return; }
        clearTimeout(magnifierTimer); magnifierTimer = null;
        magnifierApplied = magnifierFlight; magnifierFlight = null;
        pumpMagnifier(); renderMagnifier();
      } else if (message?.type === 'notice' && ready && message.feature === 'magnifier') {
        clearTimeout(magnifierTimer); magnifierTimer = null;
        magnifierEnabled = false; magnifierFlight = null; magnifierApplied = '';
        renderMagnifier(); $('magnifierStatus').textContent = t(message.message) || t('Лупа недоступна.');
      } else if (message?.type === 'text_ack' && ready) {
        if (!textFlight || message.seq !== textFlight.seq) { connectionFailed(t('Некорректное подтверждение ввода.'), false); return; }
        clearTimeout(keyboardTimer); keyboardTimer = null;
        confirmedText = textFlight.result; textFlight = null;
        pumpKeyboard(); renderKeyboard();
      } else if (message?.type === 'notice' && ready && message.feature === 'keyboard') {
        stopKeyboard(typeof message.message === 'string' ? message.message : t('Ввод остановлен. Откройте клавиатуру заново.'));
      } else if (message?.type === 'notice' && ready && message.feature === 'pinch') {
        pinching = false; pinchScale = null; gestures.cancel();
        render(typeof message.message === 'string' ? message.message : t('Не удалось выполнить жест растягивания.'));
      } else if (message?.type === "pong" && ready) lastReply = Date.now();
      else disconnect(t("Неожиданный ответ ноутбука."));
    };
    socket.onerror = () => { if (current === generation) connectionFailed(t("Нет связи. Проверьте Wi-Fi, приём и брандмауэр Windows."), saved || !!remembered); };
    socket.onclose = () => { if (current === generation) connectionFailed(t("Ноутбук отключился. Ожидаю подключения…"), saved || !!remembered); };
    handshakeTimeout = setTimeout(() => { if (current === generation) connectionFailed(t("Нет ответа от ноутбука. Проверьте Wi-Fi и брандмауэр."), saved); }, 8000);
  }

  function touchPoint(event) {
    const rect = pad.getBoundingClientRect();
    $("touchDot").style.left = `${event.clientX - rect.left}px`;
    $("touchDot").style.top = `${event.clientY - rect.top}px`;
    $("touchDot").hidden = false;
  }
  pad.addEventListener("pointerdown", event => {
    if (!ready || controls.open || $('keyboardDialog').open || pairing.open || (event.pointerType === 'mouse' && event.button !== 0)) return;
    event.preventDefault();
    try { pad.setPointerCapture(event.pointerId); capturedPointers.add(event.pointerId); } catch (_) { }
    gestures.start(event.pointerId, event.clientX, event.clientY, event.timeStamp);
    touchPoint(event);
  });
  pad.addEventListener("pointermove", event => {
    if (!ready || !gestures.points.has(event.pointerId)) return;
    event.preventDefault();
    const samples = typeof event.getCoalescedEvents === 'function' ? event.getCoalescedEvents() : [];
    for (const sample of samples.length ? samples : [event])
      gestures.move(event.pointerId, sample.clientX, sample.clientY);
    touchPoint(event);
  });
  pad.addEventListener("pointerup", event => {
    if (!gestures.points.has(event.pointerId)) return;
    gestures.move(event.pointerId, event.clientX, event.clientY);
    gestures.end(event.pointerId, event.timeStamp);
    flush();
    releasePointer(event.pointerId);
    if (!gestures.points.size) $("touchDot").hidden = true;
  });
  function releasePointer(id) {
    capturedPointers.delete(id);
    try { if (pad.hasPointerCapture(id)) pad.releasePointerCapture(id); } catch (_) { }
  }
  const cancelGesture = () => {
    gestures.cancel(); motion.clear(); wheel = 0; $("touchDot").hidden = true;
    for (const id of [...capturedPointers]) releasePointer(id);
  };
  for (const id of ['openKeyboard', 'openControls', 'magnifierToggle', 'screenToggle']) $(id).addEventListener('pointerdown', cancelGesture);
  activateButton($("openKeyboard"), () => {
    if (!ready || !keyboardSupported || textFlight || keyboardClosing) return;
    cancelGesture();
    keyboardActive = true; keyboardClosing = false; keyboardComposing = false; confirmedText = '';
    $("keyboardText").value = '';
    $("keyboardStatus").textContent = t('Начните печатать на клавиатуре iPhone.');
    renderKeyboard(); $("keyboardDialog").showModal();
    updateScreenVisibility();
    // Keep focus synchronous with the tap so iOS opens its standard keyboard.
    $("keyboardText").focus(); updateViewport();
  });
  $("keyboardText").addEventListener('compositionstart', () => { keyboardComposing = true; });
  $("keyboardText").addEventListener('compositionend', () => { keyboardComposing = false; pumpKeyboard(); });
  $("keyboardText").addEventListener('input', event => { if (!event.isComposing) pumpKeyboard(); });
  activateButton($("closeKeyboard"), requestKeyboardClose);
  activateButton($("keyboardDone"), requestKeyboardClose);
  $("keyboardDialog").addEventListener('cancel', event => { event.preventDefault(); requestKeyboardClose(); });
  activateButton(openControls, () => {
    if (controls.open) return;
    cancelGesture();
    updateViewport();
    controls.showModal();
    $('controlBody').scrollTop = 0;
    updateScreenVisibility();
  });
  activateButton($('backToTouchpad'), () => controls.close());
  controls.addEventListener('close', () => {
    if (controls.open) return;
    updateScreenVisibility();
    openControls.focus({ preventScroll: true });
  });
  let pressedBackdrop = false;
  controls.addEventListener('pointerdown', event => { pressedBackdrop = event.target === controls; });
  controls.addEventListener('click', event => {
    if (pressedBackdrop && event.target === controls) controls.close();
    pressedBackdrop = false;
  });
  const handle = $('controlHandle');
  let sheetSwipe = null;
  handle.addEventListener('pointerdown', event => {
    if (!event.isPrimary || event.button !== 0) return;
    handle.setPointerCapture(event.pointerId);
    sheetSwipe = { id: event.pointerId, x: event.clientX, y: event.clientY };
  });
  handle.addEventListener('pointerup', event => {
    if (!sheetSwipe || sheetSwipe.id !== event.pointerId) return;
    const distance = event.clientY - sheetSwipe.y;
    const horizontal = Math.abs(event.clientX - sheetSwipe.x);
    sheetSwipe = null;
    if (distance >= 45 && distance > horizontal) controls.close();
  });
  handle.addEventListener('lostpointercapture', event => {
    if (sheetSwipe && sheetSwipe.id === event.pointerId) sheetSwipe = null;
  });
  pad.addEventListener("pointercancel", cancelGesture);
  pad.addEventListener("lostpointercapture", event => { if (gestures.points.has(event.pointerId)) cancelGesture(); });
  pad.addEventListener("contextmenu", event => { event.preventDefault(); });
  // Prevent Safari's page gestures only on the pad. Settings remain scrollable.
  pad.addEventListener("touchmove", event => { event.preventDefault(); }, { passive: false });
  pad.addEventListener("gesturestart", event => { event.preventDefault(); });
  activateButton(connectButton, () => {
    userStopped = false; retryDelay = 2000;
    if (remembered) connectSaved();
    else { $("pairStatus").textContent = ""; pairing.showModal(); }
  });
  activateButton(disconnectButton, () => { userStopped = true; disconnect(); });
  activateButton($("closePairing"), () => { if (connecting) disconnect(); pairing.close(); });
  pairing.addEventListener("cancel", () => { if (connecting) disconnect(); });
  $("pairingForm").addEventListener("submit", event => { event.preventDefault(); userStopped = false; connect(); });
  volumeButtons.forEach(button => { activateButton(button, () => { send({ type: "volume", action: button.dataset.volume }); }); });
  $("sensitivity").addEventListener("input", event => {
    sensitivity = Number(event.target.value);
    $("sensitivityValue").value = sensitivity.toFixed(1) + "×";
    try { localStorage.setItem("remote.sensitivity", String(sensitivity)); } catch (_) { }
  });
  $("smoothing").addEventListener('change', event => {
    smoothing = event.target.checked;
    try { localStorage.setItem('remote.smoothing', smoothing ? 'on' : 'off'); } catch (_) { }
  });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) disconnect(t("Страница свёрнута. При возвращении сохранённый телефон подключится автоматически."));
    else { updateViewport(); connectSaved(); }
  });
  window.addEventListener("pagehide", () => { disconnect(); });
  window.addEventListener("offline", () => { disconnect(t("Сеть недоступна. Проверьте Wi-Fi.")); });
  window.addEventListener("online", connectSaved);
  window.addEventListener("pageshow", () => { updateViewport(); connectSaved(); });
  window.addEventListener('resize', updateViewport);
  window.addEventListener('orientationchange', updateViewport);
  if (window.visualViewport) window.visualViewport.addEventListener('resize', updateViewport);
  updateViewport();
  render(t("Откройте PocketPointer на ноутбуке и подключитесь по коду."));
  connectSaved();
})();
