(function (root) {
  "use strict";
  class GestureTracker {
    constructor(handlers, timers = {}) {
      this.handlers = handlers; this.lastTap = null; this.dragEnabled = !!handlers.drag; this.dragging = false;
      this.schedule = timers.schedule || ((fn, delay) => setTimeout(fn, delay));
      this.clearTimer = timers.cancel || (timer => clearTimeout(timer));
      this.lockVersion = 0; this.dragWaiting = false; this.reset();
    }
    reset() { this.points = new Map(); this.maxFingers = 0; this.moved = false; this.cursorStarted = false; this.started = 0; this.mode = null; this.initialSpan = 0; this.initialCenter = null; }
    span() { const [a, b] = this.points.values(); return a && b ? Math.hypot(a.x - b.x, a.y - b.y) : 0; }
    finishPinch() { if (this.mode === 'pinch') { this.mode = 'finished'; this.handlers.pinch?.('end', 1); } }
    clearDragWait() {
      ++this.lockVersion; this.clearTimer(this.holdTimer); this.holdTimer = null;
      if (this.dragWaiting) { this.dragWaiting = false; this.handlers.dragLock?.(false); }
    }
    finishDrag() { this.clearDragWait(); if (this.dragging) { this.dragging = false; this.handlers.drag?.('end'); } }
    waitForDrag(time) {
      this.clearDragWait(); this.dragWaiting = true; this.dragDeadline = time + 800;
      this.handlers.dragLock?.(true);
      const version = this.lockVersion;
      this.holdTimer = this.schedule(() => {
        if (version !== this.lockVersion || !this.dragWaiting) return;
        this.finishDrag(); this.reset();
      }, 800);
    }
    center() {
      let x = 0, y = 0;
      for (const point of this.points.values()) { x += point.x; y += point.y; }
      return { x: x / this.points.size, y: y / this.points.size };
    }
    start(id, x, y, time) {
      if (this.points.size === 0) {
        if (this.dragWaiting && time > this.dragDeadline) this.finishDrag();
        const continuing = this.dragging;
        this.clearDragWait();
        this.reset(); this.started = time;
        const previous = this.lastTap; this.lastTap = null;
        if (!continuing && this.dragEnabled && previous && time >= previous.time && time - previous.time <= 330 && Math.hypot(x - previous.x, y - previous.y) <= 32) {
          this.dragging = true; this.handlers.drag?.('start');
        }
      } else if (this.dragging) {
        this.finishDrag(); this.moved = true; this.mode = 'finished';
      }
      this.points.set(id, { x, y, startX: x, startY: y });
      this.maxFingers = Math.max(this.maxFingers, this.points.size);
      if (this.points.size === 2 && this.maxFingers === 2) {
        this.initialSpan = Math.max(1, this.span()); this.initialCenter = this.center();
      }
      if (this.maxFingers > 2) { this.finishPinch(); this.moved = true; this.mode = 'finished'; }
    }
    move(id, x, y) {
      const point = this.points.get(id);
      if (!point) return;
      const before = this.center();
      point.x = x; point.y = y;
      if (Math.hypot(x - point.startX, y - point.startY) > 7) this.moved = true;
      const after = this.center();
      if (this.maxFingers === 1) {
        if (!this.cursorStarted && Math.hypot(x - point.startX, y - point.startY) >= 2) {
          this.cursorStarted = true;
          this.handlers.move(x - point.startX, y - point.startY);
        } else if (this.cursorStarted) this.handlers.move(after.x - before.x, after.y - before.y);
      } else if (this.maxFingers === 2 && this.points.size === 2 && this.mode !== 'finished') {
        const span = this.span(), spread = Math.abs(span - this.initialSpan);
        const travel = Math.hypot(after.x - this.initialCenter.x, after.y - this.initialCenter.y);
        if (!this.mode) {
          if (spread >= Math.max(8, this.initialSpan * 0.06) && spread > travel * 1.25) {
            this.mode = 'pinch'; this.moved = true; this.handlers.pinch?.('start', 1);
          } else if (Math.abs(after.y - this.initialCenter.y) >= 6) {
            this.mode = 'scroll'; this.moved = true;
            this.handlers.scroll(after.y - this.initialCenter.y);
            return;
          }
        }
        if (this.mode === 'pinch') this.handlers.pinch?.('update', Math.max(0.1, Math.min(4, span / this.initialSpan)));
        else if (this.mode === 'scroll') this.handlers.scroll(after.y - before.y);
      }
    }
    end(id, time) {
      if (!this.points.has(id)) return;
      const point = this.points.get(id);
      const wasDragging = this.dragging;
      this.finishPinch();
      this.points.delete(id);
      if (this.points.size !== 0) return;
      if (wasDragging) {
        const keep = this.dragEnabled && this.maxFingers === 1 && this.moved;
        this.reset();
        if (keep) this.waitForDrag(time); else this.finishDrag();
        return;
      }
      if (!wasDragging && !this.moved && time - this.started <= 320 && this.maxFingers <= 2) {
        this.handlers.click(this.maxFingers === 2);
        if (this.maxFingers === 1) this.lastTap = { time, x: point.x, y: point.y };
      }
      this.reset();
    }
    cancel() { this.finishDrag(); this.finishPinch(); this.lastTap = null; this.reset(); }
  }
  class MotionFilter {
    constructor() { this.clear(); }
    clear() { this.x = this.y = this.carryX = this.carryY = 0; }
    add(x, y) {
      this.x = Math.max(-500, Math.min(500, this.x + x));
      this.y = Math.max(-500, Math.min(500, this.y + y));
    }
    step(elapsed, immediate = false) {
      // Short time-based smoothing without velocity prediction or inertia.
      // Carry fractional motion instead of rounding away individual samples.
      const alpha = immediate ? 1 : 1 - Math.exp(-Math.max(1, Math.min(32, elapsed)) / 8);
      const x = Math.abs(this.x) < 0.001 ? this.x : this.x * alpha;
      const y = Math.abs(this.y) < 0.001 ? this.y : this.y * alpha;
      this.x -= x; this.y -= y;
      this.carryX += x; this.carryY += y;
      const dx = Math.trunc(this.carryX + Math.sign(this.carryX) * 1e-9) || 0;
      const dy = Math.trunc(this.carryY + Math.sign(this.carryY) * 1e-9) || 0;
      this.carryX -= dx; this.carryY -= dy;
      return { dx, dy };
    }
  }
  if (typeof module !== "undefined" && module.exports) module.exports = { GestureTracker, MotionFilter };
  else root.RemoteGestures = { GestureTracker, MotionFilter };
})(globalThis);
