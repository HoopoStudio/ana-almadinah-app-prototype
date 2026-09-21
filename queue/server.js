#!/usr/bin/env node
// Ana Al-Madinah VR screening – queue server.
// Zero dependencies: Node 18+ built-ins only.
//
//   node queue/server.js
//   PORT=3000 STAFF_PIN=2468 node queue/server.js
//
// Routes
//   /            guest join page (this is what the QR code points to)
//   /staff       staff queue board (PIN protected)
//   /display     big-screen "now serving" board
//   /qr          printable QR poster
//   /api/...     JSON API (see below)

const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const odoo = require('./odoo');

const PORT = Number(process.env.PORT || 3000);
const STAFF_PIN = process.env.STAFF_PIN || '1234';
const DATA_FILE = process.env.QUEUE_DATA || path.join(__dirname, 'data', 'queue.json');
const PUBLIC_DIR = path.join(__dirname, 'public');
// Logos: a standalone checkout keeps them in public/assets; inside the app prototype repo they live one level up.
const REPO_ASSETS = fs.existsSync(path.join(PUBLIC_DIR, 'assets')) ? path.join(PUBLIC_DIR, 'assets') : path.join(__dirname, '..', 'assets');

// ---------------------------------------------------------------- state ----

const defaultSettings = {
  eventName: 'أنا المدينة – تجربة الواقع الافتراضي',
  eventNameEn: 'Ana Al-Madinah – VR Experience',
  headsets: 6,          // how many guests can watch at the same time
  sessionMinutes: 12,   // length of one screening incl. seating/cleaning
  open: true,           // false = QR page says the queue is closed
  ticketPrefix: 'A',
};

let state = { settings: { ...defaultSettings }, seq: 0, tickets: [], day: today() };

function today() { return new Date().toISOString().slice(0, 10); }

function load() {
  try {
    const raw = JSON.parse(fs.readFileSync(DATA_FILE, 'utf8'));
    state = { ...state, ...raw, settings: { ...defaultSettings, ...(raw.settings || {}) } };
  } catch (_) { /* first run */ }
}

let saveTimer = null;
function save() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    fs.mkdirSync(path.dirname(DATA_FILE), { recursive: true });
    const tmp = DATA_FILE + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(state, null, 2));
    fs.renameSync(tmp, DATA_FILE);
  }, 50);
}

// ------------------------------------------------------------- queue ops ----

const ACTIVE = new Set(['waiting', 'called', 'in_session']);

function waitingList() {
  return state.tickets
    .filter(t => t.status === 'waiting')
    .sort((a, b) => (b.priority - a.priority) || a.joinedAt.localeCompare(b.joinedAt));
}

function positionOf(ticket) {
  if (ticket.status !== 'waiting') return 0;
  return waitingList().findIndex(t => t.id === ticket.id) + 1;
}

function estimateMinutes(position) {
  if (position <= 0) return 0;
  const ahead = waitingList().slice(0, position - 1).reduce((n, t) => n + t.party, 0);
  const { headsets, sessionMinutes } = state.settings;
  const busy = state.tickets.filter(t => t.status === 'in_session' || t.status === 'called').reduce((n, t) => n + t.party, 0);
  const rounds = Math.ceil((ahead + busy + 1) / Math.max(1, headsets));
  return Math.max(0, (rounds - 1) * sessionMinutes) + Math.round(sessionMinutes / 2);
}

function nextNumber() {
  state.seq += 1;
  return `${state.settings.ticketPrefix}-${String(state.seq).padStart(3, '0')}`;
}

function publicTicket(t) {
  const position = positionOf(t);
  return {
    id: t.id, number: t.number, name: t.name, party: t.party, lang: t.lang,
    status: t.status, joinedAt: t.joinedAt, calledAt: t.calledAt,
    position, estimateMinutes: estimateMinutes(position),
  };
}

function summary() {
  const waiting = waitingList();
  const nowServing = state.tickets.filter(t => t.status === 'called').sort((a, b) => a.calledAt.localeCompare(b.calledAt));
  const inSession = state.tickets.filter(t => t.status === 'in_session');
  const done = state.tickets.filter(t => t.status === 'done');
  const waits = done.filter(t => t.calledAt).map(t => (new Date(t.calledAt) - new Date(t.joinedAt)) / 60000);
  return {
    eventName: state.settings.eventName,
    eventNameEn: state.settings.eventNameEn,
    open: state.settings.open,
    headsets: state.settings.headsets,
    sessionMinutes: state.settings.sessionMinutes,
    waitingGroups: waiting.length,
    waitingPeople: waiting.reduce((n, t) => n + t.party, 0),
    inSessionPeople: inSession.reduce((n, t) => n + t.party, 0),
    servedPeople: done.reduce((n, t) => n + t.party, 0),
    avgWaitMinutes: waits.length ? Math.round(waits.reduce((a, b) => a + b, 0) / waits.length) : null,
    nowServing: nowServing.map(t => t.number),
    upNext: waiting.slice(0, 5).map(t => ({ number: t.number, party: t.party })),
    estimateForNew: estimateMinutes(waiting.length + 1),
    odoo: odoo.enabled(),
    updatedAt: new Date().toISOString(),
  };
}

function findTicket(id) {
  return state.tickets.find(t => t.id === id || t.number === id);
}

function setStatus(ticket, status) {
  const now = new Date().toISOString();
  ticket.status = status;
  if (status === 'called') ticket.calledAt = ticket.calledAt || now;
  if (status === 'in_session') { ticket.calledAt = ticket.calledAt || now; ticket.startedAt = now; }
  if (status === 'done' || status === 'no_show' || status === 'cancelled') ticket.finishedAt = now;
  if (status === 'waiting') { ticket.calledAt = null; ticket.startedAt = null; ticket.finishedAt = null; }
  save();
  broadcast();
  if (['done', 'no_show', 'cancelled'].includes(status)) syncStatusLater(ticket);
}

// ------------------------------------------------------------- Odoo sync ----

function syncJoinLater(ticket) {
  if (!odoo.enabled()) { ticket.odooSync = 'off'; return; }
  ticket.odooSync = 'pending';
  odoo.syncJoin(ticket).then(({ partnerId, registrationId }) => {
    ticket.odooPartnerId = partnerId;
    ticket.odooRegistrationId = registrationId;
    ticket.odooSync = 'ok';
    ticket.odooError = null;
  }).catch(err => {
    ticket.odooSync = 'error';
    ticket.odooError = err.message;
    console.error(`[odoo] join sync failed for ${ticket.number}: ${err.message}`);
  }).finally(() => { save(); broadcast(); });
}

function syncStatusLater(ticket) {
  if (!odoo.enabled() || !ticket.odooRegistrationId) return;
  odoo.syncStatus(ticket).catch(err => console.error(`[odoo] status sync failed for ${ticket.number}: ${err.message}`));
}

// -------------------------------------------------------------- SSE bus ----

const clients = new Set();
function broadcast() {
  const payload = `event: update\ndata: ${JSON.stringify(summary())}\n\n`;
  for (const res of clients) res.write(payload);
}
setInterval(() => { for (const res of clients) res.write(': ping\n\n'); }, 25000).unref();

// ------------------------------------------------------------- helpers ----

function json(res, status, body) {
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
  res.end(JSON.stringify(body));
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    req.on('data', c => { data += c; if (data.length > 64 * 1024) { reject(new Error('body too large')); req.destroy(); } });
    req.on('end', () => { try { resolve(data ? JSON.parse(data) : {}); } catch (e) { reject(e); } });
    req.on('error', reject);
  });
}

function isStaff(req, url) {
  const pin = req.headers['x-staff-pin'] || url.searchParams.get('pin');
  return typeof pin === 'string' && pin.length === STAFF_PIN.length &&
    crypto.timingSafeEqual(Buffer.from(pin), Buffer.from(STAFF_PIN));
}

const MIME = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.ico': 'image/x-icon' };

function serveFile(res, file) {
  fs.readFile(file, (err, data) => {
    if (err) { res.writeHead(404); return res.end('Not found'); }
    res.writeHead(200, { 'content-type': MIME[path.extname(file)] || 'application/octet-stream', 'cache-control': 'no-cache' });
    res.end(data);
  });
}

function clean(s, max) { return String(s || '').trim().replace(/\s+/g, ' ').slice(0, max); }

function csvEscape(v) { const s = v == null ? '' : String(v); return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; }

// --------------------------------------------------------------- router ----

const PAGES = { '/': 'join.html', '/join': 'join.html', '/staff': 'staff.html', '/display': 'display.html', '/qr': 'qr.html' };

async function handle(req, res) {
  const url = new URL(req.url, 'http://localhost');
  const p = url.pathname;

  // ---- static pages
  if (req.method === 'GET' && PAGES[p]) return serveFile(res, path.join(PUBLIC_DIR, PAGES[p]));
  if (req.method === 'GET' && p.startsWith('/assets/')) return serveFile(res, path.join(REPO_ASSETS, path.normalize(p.slice(8)).replace(/^(\.\.[/\\])+/, '')));
  if (req.method === 'GET' && p.startsWith('/static/')) return serveFile(res, path.join(PUBLIC_DIR, path.normalize(p.slice(8)).replace(/^(\.\.[/\\])+/, '')));

  // ---- public API
  if (req.method === 'GET' && p === '/api/summary') return json(res, 200, summary());

  if (req.method === 'GET' && p === '/api/events') {
    res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache', connection: 'keep-alive', 'x-accel-buffering': 'no' });
    res.write(`event: update\ndata: ${JSON.stringify(summary())}\n\n`);
    clients.add(res);
    req.on('close', () => clients.delete(res));
    return;
  }

  if (req.method === 'POST' && p === '/api/join') {
    if (!state.settings.open) return json(res, 403, { error: 'closed' });
    const body = await readBody(req);
    const name = clean(body.name, 80);
    const phone = odoo.normalisePhone(clean(body.phone, 20));
    const party = Math.min(10, Math.max(1, parseInt(body.party, 10) || 1));
    if (name.length < 2) return json(res, 400, { error: 'name' });
    if (!/^\+\d{8,15}$/.test(phone)) return json(res, 400, { error: 'phone' });
    const existing = state.tickets.find(t => t.phone === phone && ACTIVE.has(t.status));
    if (existing) return json(res, 200, { ticket: publicTicket(existing), existing: true });
    const ticket = {
      id: crypto.randomUUID(),
      number: nextNumber(),
      name, phone,
      email: clean(body.email, 120) || null,
      party,
      lang: body.lang === 'en' ? 'en' : 'ar',
      kids: Boolean(body.kids),
      consent: Boolean(body.consent),
      source: clean(body.source, 40) || null,
      priority: 0,
      status: 'waiting',
      joinedAt: new Date().toISOString(),
      calledAt: null, startedAt: null, finishedAt: null,
      odooSync: 'off', odooPartnerId: null, odooRegistrationId: null, odooError: null,
    };
    state.tickets.push(ticket);
    save();
    broadcast();
    syncJoinLater(ticket);
    return json(res, 201, { ticket: publicTicket(ticket) });
  }

  let m;
  if ((m = p.match(/^\/api\/ticket\/([\w-]+)$/)) && req.method === 'GET') {
    const t = findTicket(m[1]);
    if (!t) return json(res, 404, { error: 'not found' });
    return json(res, 200, { ticket: publicTicket(t), summary: summary() });
  }
  if ((m = p.match(/^\/api\/ticket\/([\w-]+)\/leave$/)) && req.method === 'POST') {
    const t = findTicket(m[1]);
    if (!t) return json(res, 404, { error: 'not found' });
    if (t.status === 'waiting' || t.status === 'called') setStatus(t, 'cancelled');
    return json(res, 200, { ticket: publicTicket(t) });
  }

  // ---- staff API
  if (p.startsWith('/api/staff/')) {
    if (!isStaff(req, url)) return json(res, 401, { error: 'pin' });

    if (req.method === 'GET' && p === '/api/staff/queue') {
      return json(res, 200, {
        settings: state.settings, summary: summary(),
        tickets: state.tickets.map(t => ({ ...t, position: positionOf(t) })),
      });
    }
    if (req.method === 'POST' && p === '/api/staff/settings') {
      const b = await readBody(req);
      const s = state.settings;
      if (b.eventName != null) s.eventName = clean(b.eventName, 80);
      if (b.eventNameEn != null) s.eventNameEn = clean(b.eventNameEn, 80);
      if (b.headsets != null) s.headsets = Math.min(100, Math.max(1, parseInt(b.headsets, 10) || 1));
      if (b.sessionMinutes != null) s.sessionMinutes = Math.min(180, Math.max(1, parseInt(b.sessionMinutes, 10) || 1));
      if (b.open != null) s.open = Boolean(b.open);
      if (b.ticketPrefix != null) s.ticketPrefix = clean(b.ticketPrefix, 3).toUpperCase() || 'A';
      save(); broadcast();
      return json(res, 200, { settings: s });
    }
    if (req.method === 'POST' && p === '/api/staff/call-next') {
      const next = waitingList()[0];
      if (!next) return json(res, 404, { error: 'empty' });
      setStatus(next, 'called');
      return json(res, 200, { ticket: next });
    }
    if (req.method === 'POST' && p === '/api/staff/add') {
      // Walk-up guest without a phone: staff adds them by hand.
      const b = await readBody(req);
      const ticket = {
        id: crypto.randomUUID(), number: nextNumber(),
        name: clean(b.name, 80) || 'ضيف', phone: odoo.normalisePhone(clean(b.phone, 20)) || null,
        email: null, party: Math.min(10, Math.max(1, parseInt(b.party, 10) || 1)),
        lang: b.lang === 'en' ? 'en' : 'ar', kids: false, consent: false, source: 'staff',
        priority: b.priority ? 1 : 0, status: 'waiting', joinedAt: new Date().toISOString(),
        calledAt: null, startedAt: null, finishedAt: null,
        odooSync: 'off', odooPartnerId: null, odooRegistrationId: null, odooError: null,
      };
      state.tickets.push(ticket); save(); broadcast();
      if (ticket.phone) syncJoinLater(ticket);
      return json(res, 201, { ticket });
    }
    if ((m = p.match(/^\/api\/staff\/ticket\/([\w-]+)\/(call|start|done|no-show|requeue|cancel|priority|sync)$/)) && req.method === 'POST') {
      const t = findTicket(m[1]);
      if (!t) return json(res, 404, { error: 'not found' });
      const action = m[2];
      if (action === 'call') setStatus(t, 'called');
      else if (action === 'start') setStatus(t, 'in_session');
      else if (action === 'done') setStatus(t, 'done');
      else if (action === 'no-show') setStatus(t, 'no_show');
      else if (action === 'cancel') setStatus(t, 'cancelled');
      else if (action === 'requeue') { setStatus(t, 'waiting'); }
      else if (action === 'priority') { t.priority = t.priority ? 0 : 1; save(); broadcast(); }
      else if (action === 'sync') { if (t.odooRegistrationId) syncStatusLater(t); else syncJoinLater(t); }
      return json(res, 200, { ticket: { ...t, position: positionOf(t) } });
    }
    if (req.method === 'POST' && p === '/api/staff/reset') {
      // New screening day: archive today's file and start numbering again.
      const archive = DATA_FILE.replace(/\.json$/, '') + `-${state.day}-${Date.now()}.json`;
      try { fs.copyFileSync(DATA_FILE, archive); } catch (_) { /* nothing to archive */ }
      state = { settings: state.settings, seq: 0, tickets: [], day: today() };
      save(); broadcast();
      return json(res, 200, { ok: true, archive });
    }
    if (req.method === 'GET' && p === '/api/staff/export.csv') {
      const head = ['number', 'name', 'phone', 'email', 'party', 'lang', 'kids_under_10', 'consent', 'source', 'status', 'joined_at', 'called_at', 'started_at', 'finished_at', 'wait_minutes', 'odoo_partner_id', 'odoo_registration_id'];
      const rows = state.tickets.map(t => [
        t.number, t.name, t.phone, t.email, t.party, t.lang, t.kids ? 1 : 0, t.consent ? 1 : 0, t.source, t.status,
        t.joinedAt, t.calledAt, t.startedAt, t.finishedAt,
        t.calledAt ? Math.round((new Date(t.calledAt) - new Date(t.joinedAt)) / 60000) : '',
        t.odooPartnerId, t.odooRegistrationId,
      ].map(csvEscape).join(','));
      res.writeHead(200, { 'content-type': 'text/csv; charset=utf-8', 'content-disposition': `attachment; filename="queue-${state.day}.csv"` });
      return res.end('﻿' + [head.join(','), ...rows].join('\n'));
    }
    if (req.method === 'GET' && p === '/api/staff/odoo-check') {
      try { return json(res, 200, await odoo.check()); }
      catch (err) { return json(res, 502, { enabled: true, error: err.message }); }
    }
    return json(res, 404, { error: 'unknown staff route' });
  }

  res.writeHead(404, { 'content-type': 'text/plain' });
  res.end('Not found');
}

// ----------------------------------------------------------------- boot ----

load();
const server = http.createServer((req, res) => {
  handle(req, res).catch(err => {
    console.error(err);
    if (!res.headersSent) json(res, 500, { error: err.message });
  });
});

server.listen(PORT, () => {
  console.log(`Queue server on http://localhost:${PORT}`);
  console.log(`  guests  → /        staff → /staff (PIN ${STAFF_PIN})   display → /display   QR poster → /qr`);
  console.log(`  data    → ${DATA_FILE}`);
  console.log(`  odoo    → ${odoo.enabled() ? 'sync ON (event ' + (odoo.ODOO_EVENT_ID || 'not set') + ')' : 'sync OFF (set ODOO_URL, ODOO_DB, ODOO_USER, ODOO_API_KEY, ODOO_EVENT_ID)'}`);
});
