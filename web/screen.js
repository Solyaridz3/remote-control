(function (root) {
  'use strict';
  class FrameRate {
    constructor() { this.fps = 60; this.quality = 80; this.average = 0; this.slow = 0; this.fast = 0; }
    record(milliseconds, bytes) {
      this.average = this.average ? this.average * .85 + milliseconds * .15 : milliseconds;
      const budget = 1000 / this.fps;
      if (this.average > budget * 1.15 || bytes > 300000) {
        this.fast = 0;
        if (++this.slow >= 4) { this.fps = Math.max(10, this.fps - 5); this.quality = Math.max(45, this.quality - 5); this.slow = 0; }
      } else {
        this.slow = 0;
        if (this.average < budget * .85 && ++this.fast >= 30) { this.fps = Math.min(60, this.fps + 5); this.quality = Math.min(85, this.quality + 2); this.fast = 0; }
      }
    }
  }
  class Viewer {
    constructor(options) {
      this.options = options; this.image = options.image;
      this.now = options.now || (() => Date.now());
      this.schedule = options.schedule || ((fn, delay) => setTimeout(fn, delay));
      this.cancel = options.cancel || (timer => clearTimeout(timer));
      this.urls = options.urls || root.URL;
      this.isFrame = options.isFrame || (value => value instanceof root.Blob);
      this.supported = false; this.enabled = false; this.visible = true; this.running = false;
      this.pending = false; this.socket = null; this.generation = 0; this.requestId = 0;
      this.urlsInUse = new Set(); this.message = ''; this.fps = 0;
    }
    changed() { this.options.changed?.(this); }
    setSupported(value) { this.supported = value; if (!value) this.disable(false); else this.changed(); }
    setVisible(value) {
      if (this.visible === value) return;
      this.visible = value;
      if (!value) { this.endSession(true); this.message = ''; this.changed(); }
      else if (this.enabled) this.start();
    }
    toggle(value) {
      if (!value) { this.disable(true); return; }
      if (!this.supported) return;
      this.enabled = true; this.message = ''; this.start(); this.changed();
    }
    disable(notify = true) { this.enabled = false; this.endSession(notify); this.message = ''; this.changed(); }
    endSession(notify) {
      const active = this.pending || !!this.socket;
      ++this.generation; this.pending = false; this.connected = false; this.running = false; this.inFlight = false; this.decoding = false; this.fps = 0;
      this.cancel(this.timer); this.cancel(this.timeout); this.timer = this.timeout = null;
      if (this.socket) { this.socket.onopen = this.socket.onmessage = this.socket.onerror = this.socket.onclose = null; this.socket.close(); this.socket = null; }
      this.image.onload = this.image.onerror = null; this.image.hidden = true; this.image.removeAttribute('src');
      for (const url of this.urlsInUse) this.urls.revokeObjectURL(url);
      this.urlsInUse.clear(); this.objectUrl = null;
      if (notify && active) this.options.control({ type: 'screen_stop' });
    }
    fail(message) { this.disable(true); this.message = message; this.changed(); }
    start() {
      if (!this.enabled || !this.supported || !this.visible || this.pending || this.socket) return;
      this.pending = true; this.rate = new FrameRate(); this.sequence = 0; this.requestId++;
      this.windowStarted = this.now(); this.windowFrames = 0;
      const generation = this.generation;
      this.timeout = this.schedule(() => { if (generation === this.generation) this.fail('Нет ответа трансляции. Попробуйте включить её снова.'); }, 6000);
      if (!this.options.control({ type: 'screen_start', id: this.requestId })) this.disable(false);
      this.changed();
    }
    accept(message) {
      if (message.type === 'screen_stopped') return;
      if (message.type !== 'screen_ready' || !this.pending || message.id !== this.requestId) return;
      if (!/^[0-9A-F]{64}$/.test(message.ticket)) { this.fail('Некорректный ответ трансляции.'); return; }
      this.pending = false;
      const generation = this.generation;
      const current = () => generation === this.generation && this.enabled && this.visible;
      try {
        const socket = this.options.socketFactory(); this.socket = socket; socket.binaryType = 'blob';
        socket.onopen = () => { if (current()) socket.send(JSON.stringify({ type: 'screen_hello', ticket: message.ticket })); };
        socket.onmessage = event => {
          if (!current()) return;
          if (typeof event.data === 'string') {
            let reply; try { if (event.data.length > 1024) throw new Error(); reply = JSON.parse(event.data); } catch (_) { this.fail('Некорректный ответ трансляции.'); return; }
            if (reply.type === 'screen_connected' && !this.connected) { this.connected = true; this.cancel(this.timeout); this.next(); }
            else this.fail(reply.message || 'Трансляция остановлена. Включите её снова.');
            return;
          }
          if (!this.isFrame(event.data) || event.data.size < 1 || event.data.size > 2097152 || !this.inFlight || this.decoding) { this.fail('Некорректный кадр трансляции.'); return; }
          this.decoding = true;
          const previous = this.objectUrl;
          const url = this.urls.createObjectURL(event.data); this.urlsInUse.add(url); this.objectUrl = url;
          this.image.onload = () => {
            if (!current() || this.objectUrl !== url) return;
            this.image.onload = this.image.onerror = null;
            if (previous) { this.urls.revokeObjectURL(previous); this.urlsInUse.delete(previous); }
            this.cancel(this.timeout); this.inFlight = false; this.decoding = false; this.running = true; this.image.hidden = false;
            const elapsed = this.now() - this.frameStarted;
            this.rate.record(elapsed, event.data.size);
            this.windowFrames++;
            if (this.now() - this.windowStarted >= 1000) { this.fps = Math.round(this.windowFrames * 1000 / (this.now() - this.windowStarted)); this.windowFrames = 0; this.windowStarted = this.now(); }
            this.changed();
            this.timer = this.schedule(() => { if (current()) this.next(); }, Math.max(0, 1000 / this.rate.fps - elapsed));
          };
          this.image.onerror = () => { if (current()) this.fail('Не удалось показать кадр экрана.'); };
          this.image.src = url;
        };
        socket.onerror = socket.onclose = () => { if (current()) this.fail('Трансляция прервалась. Тачпад продолжает работать.'); };
      } catch (_) { this.fail('Не удалось открыть трансляцию.'); }
    }
    next() {
      if (!this.socket || this.socket.readyState !== 1 || !this.enabled || !this.visible || this.inFlight) return;
      const size = this.options.size();
      const width = Math.max(1, size.width), height = Math.max(1, size.height);
      const scale = Math.min(1, 960 / width, 1280 / height);
      const request = { type: 'screen_next', seq: ++this.sequence, width: Math.max(160, Math.round(width * scale)), height: Math.max(120, Math.round(height * scale)), quality: this.rate.quality, fps: this.rate.fps };
      this.inFlight = true; this.frameStarted = this.now();
      this.timeout = this.schedule(() => this.fail('Кадры не поступают. Включите трансляцию снова.'), 5000);
      try { this.socket.send(JSON.stringify(request)); } catch (_) { this.fail('Не удалось запросить кадр.'); }
    }
  }
  if (typeof module !== 'undefined' && module.exports) module.exports = { FrameRate, Viewer };
  else root.RemoteScreen = { FrameRate, Viewer };
})(globalThis);
