// Odoo sync for the queue app.
// Talks to Odoo over JSON-RPC (/jsonrpc) with nothing but Node built-ins.
// Configure with environment variables:
//   ODOO_URL       e.g. https://ana-almadinahart.com
//   ODOO_DB        database name (for Odoo Online it is usually the subdomain, e.g. ana-almadinahart)
//   ODOO_USER      login e-mail of the user whose API key is used
//   ODOO_API_KEY   API key (Odoo → My Profile → Account Security → New API Key)
//   ODOO_EVENT_ID  id of the event.event record for this screening day
// When ODOO_URL is not set, sync is disabled and the queue works standalone.

const ODOO_URL = (process.env.ODOO_URL || '').replace(/\/+$/, '');
const ODOO_DB = process.env.ODOO_DB || '';
const ODOO_USER = process.env.ODOO_USER || '';
const ODOO_API_KEY = process.env.ODOO_API_KEY || '';
const ODOO_EVENT_ID = Number(process.env.ODOO_EVENT_ID || 0);

let uid = null;
let rpcId = 0;

function enabled() {
  return Boolean(ODOO_URL && ODOO_DB && ODOO_USER && ODOO_API_KEY);
}

async function rpc(service, method, args) {
  const body = { jsonrpc: '2.0', method: 'call', id: ++rpcId, params: { service, method, args } };
  const res = await fetch(ODOO_URL + '/jsonrpc', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error('Odoo HTTP ' + res.status);
  const json = await res.json();
  if (json.error) {
    const msg = (json.error.data && json.error.data.message) || json.error.message || 'Odoo error';
    throw new Error(msg);
  }
  return json.result;
}

async function login() {
  if (uid) return uid;
  const result = await rpc('common', 'authenticate', [ODOO_DB, ODOO_USER, ODOO_API_KEY, {}]);
  if (!result) throw new Error('Odoo login failed: check ODOO_DB / ODOO_USER / ODOO_API_KEY');
  uid = result;
  return uid;
}

async function execute(model, method, args, kwargs) {
  await login();
  try {
    return await rpc('object', 'execute_kw', [ODOO_DB, uid, ODOO_API_KEY, model, method, args, kwargs || {}]);
  } catch (err) {
    // A stale session or a changed key: force a re-login once.
    if (/session|access denied|authenticat/i.test(err.message)) uid = null;
    throw err;
  }
}

// Normalise Saudi numbers to +9665XXXXXXXX so the same guest is found next time.
function normalisePhone(phone) {
  let p = String(phone || '').replace(/[^\d+]/g, '');
  if (p.startsWith('00')) p = '+' + p.slice(2);
  if (/^05\d{8}$/.test(p)) p = '+966' + p.slice(1);
  if (/^5\d{8}$/.test(p)) p = '+966' + p;
  if (/^9665\d{8}$/.test(p)) p = '+' + p;
  return p;
}

async function upsertPartner(guest) {
  const phone = normalisePhone(guest.phone);
  const fields = ['id', 'name', 'email'];
  let found = [];
  if (phone) {
    found = await execute('res.partner', 'search_read', [[
      '|', ['phone', '=', phone], ['mobile', '=', phone],
    ]], { fields, limit: 1 });
  }
  const values = {
    name: guest.name,
    phone,
    mobile: phone,
    lang: guest.lang === 'en' ? 'en_US' : 'ar_001',
    comment: 'Ana Al-Madinah VR screening queue guest',
  };
  if (guest.email) values.email = guest.email;
  if (found.length) {
    // Keep what is in Odoo, only fill in blanks.
    const patch = {};
    if (!found[0].email && guest.email) patch.email = guest.email;
    if (Object.keys(patch).length) await execute('res.partner', 'write', [[found[0].id], patch]);
    return found[0].id;
  }
  try {
    return await execute('res.partner', 'create', [values]);
  } catch (err) {
    if (/lang/i.test(err.message)) {
      delete values.lang;
      return await execute('res.partner', 'create', [values]);
    }
    throw err;
  }
}

async function createRegistration(ticket, partnerId) {
  if (!ODOO_EVENT_ID) return null;
  const values = {
    event_id: ODOO_EVENT_ID,
    partner_id: partnerId,
    name: ticket.name,
    phone: normalisePhone(ticket.phone),
  };
  if (ticket.email) values.email = ticket.email;
  const regId = await execute('event.registration', 'create', [values]);
  const note = [
    `Queue ticket ${ticket.number}`,
    `Party size: ${ticket.party}`,
    `Language: ${ticket.lang}`,
    `Children under 10: ${ticket.kids ? 'yes' : 'no'}`,
    `Marketing consent: ${ticket.consent ? 'yes' : 'no'}`,
    `Heard about us: ${ticket.source || '-'}`,
    `Joined: ${ticket.joinedAt}`,
  ].join('<br/>');
  try {
    await execute('event.registration', 'message_post', [[regId]], { body: note, message_type: 'comment' });
  } catch (_) { /* chatter is optional */ }
  return regId;
}

// Called when a guest joins the queue. Returns {partnerId, registrationId}.
async function syncJoin(ticket) {
  const partnerId = await upsertPartner(ticket);
  const registrationId = await createRegistration(ticket, partnerId);
  return { partnerId, registrationId };
}

// Called when a guest finishes the experience or leaves.
async function syncStatus(ticket) {
  if (!ticket.odooRegistrationId) return;
  const state = ticket.status === 'done' ? 'done'
    : (ticket.status === 'no_show' || ticket.status === 'cancelled') ? 'cancel'
    : null;
  if (!state) return;
  await execute('event.registration', 'write', [[ticket.odooRegistrationId], { state }]);
  const waited = ticket.calledAt && ticket.joinedAt
    ? Math.round((new Date(ticket.calledAt) - new Date(ticket.joinedAt)) / 60000) : null;
  try {
    await execute('event.registration', 'message_post', [[ticket.odooRegistrationId]], {
      body: `Queue status: ${ticket.status}${waited !== null ? ` (waited ${waited} min)` : ''}`,
      message_type: 'comment',
    });
  } catch (_) { /* optional */ }
}

async function check() {
  if (!enabled()) return { enabled: false };
  const version = await rpc('common', 'version', []);
  await login();
  let event = null;
  if (ODOO_EVENT_ID) {
    const rows = await execute('event.event', 'read', [[ODOO_EVENT_ID]], { fields: ['name', 'date_begin', 'date_end'] });
    event = rows[0] || null;
  }
  return { enabled: true, serverVersion: version.server_version, uid, event };
}

module.exports = { enabled, syncJoin, syncStatus, check, normalisePhone, ODOO_EVENT_ID };
