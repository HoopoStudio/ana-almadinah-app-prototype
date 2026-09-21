# Ana Al-Madinah – VR screening queue

A walk-up queue for the VR screening. Guests scan a QR code at the entrance, type
their name and mobile number, and get a queue number on their phone. Staff see
everyone in order on a tablet, call the next group, and the guest's phone lights up
"It's your turn". Every guest is (optionally) written to Odoo as a contact and an
event registration, so the marketing data is captured automatically.

No database server, no npm install: it is one Node.js process with a JSON file.

## Pages

| URL        | Who        | What |
|------------|------------|------|
| `/`        | Guests     | Join form (Arabic / English), then a live ticket with position and estimated wait. Shows a big yellow **حان دورك** banner, vibrates and beeps when called. |
| `/staff`   | Staff      | PIN-protected board: stats, **Call next**, start / finish / no-show / requeue, priority flag, manual walk-up entry, settings, CSV export, "new day" reset. |
| `/display` | Big screen | "Now serving" and "Up next" board for a TV at the entrance. Chimes when a new number is called. |
| `/qr`      | Staff      | Printable A4 poster with the QR code (works offline, the QR library is bundled). |

Everything updates live over Server-Sent Events, so a phone, three tablets and a TV
all show the same queue at the same moment.

## Run it

```bash
git clone https://github.com/HoopoStudio/ana-almadinah-queue.git
cd ana-almadinah-queue
STAFF_PIN=2468 node server.js          # http://localhost:3000
```

The queue is stored in `data/queue.json` (git-ignored). "New day" in the
settings archives that file and restarts numbering from A-001.

### Make it reachable by guests' phones

The QR code must point to an address a phone on mobile data can open. Any of these works:

1. **A small cloud host** (Render, Railway, Fly.io, a VPS): deploy this repository,
   set `PORT` from the host and the `STAFF_PIN` / `ODOO_*` variables. Recommended: it
   survives venue Wi-Fi problems because guests use their own data.
2. **Same server as the website**: run it behind Nginx on a subdomain such as
   `queue.ana-almadinahart.com`. Nginx needs `proxy_buffering off;` for `/api/events`.
3. **Laptop at the venue + a tunnel** (`cloudflared tunnel --url http://localhost:3000`
   or ngrok) for a one-day event. Paste the tunnel URL into the box at the top of `/qr`
   before printing.

On `/qr` the poster's URL defaults to the address you opened it from; override it
with the input box or `?url=https://…`.

## Queue logic

- One ticket per group; the group size is used for the wait estimate.
- Order is first-come, first-served, with a **priority** flag staff can set
  (elderly, accessibility, VIP) that moves a group to the front.
- Estimated wait = groups ahead ÷ headsets × session length (both configurable in settings).
- Ticket states: `waiting → called → in_session → done`, plus `no_show` and `cancelled`.
  Any ticket can be put back in the queue.
- A phone number that already has an active ticket gets the same ticket back instead of a duplicate.
- Guests can leave the queue from their phone.

## Odoo integration

Your Odoo (`ana-almadinahart.com`) already has the **Events**, **CRM**, **SMS** and
**Survey** apps, so nothing needs to be installed. The server talks to Odoo over
JSON-RPC using an API key and does this:

| When | Odoo action |
|------|-------------|
| Guest joins | Find `res.partner` by phone (creates it if new, with name, mobile, language). Create an `event.registration` on the screening's `event.event`, linked to that partner. Post a note in the registration's chatter with queue number, group size, language, children flag, marketing consent and source. |
| Guest finishes | Registration state → **Attended** (`done`), note with the actual wait time. |
| No-show / left | Registration state → **Cancelled**. |

Sync is asynchronous: the queue never waits for Odoo, and a failed sync shows a red
"retry" button on the staff board.

### Setup (5 minutes)

1. In Odoo, **Events → New**: e.g. "VR screening – Riyadh – 25 Sep 2026". Note the id
   in the URL (`…/event.event/7` → `7`).
2. **My profile → Account Security → New API key**. Use a dedicated user
   ("Queue app") with access to Contacts and Events only.
3. Set the variables (see `.env.example`): `ODOO_URL`, `ODOO_DB`, `ODOO_USER`,
   `ODOO_API_KEY`, `ODOO_EVENT_ID`. For Odoo Online the DB name is normally the
   subdomain (`ana-almadinahart`); it is shown at `/web/database/manager` on self-hosted.
4. Start the server and click **فحص اتصال Odoo** in the staff settings. It reports
   the Odoo version and the event name if everything is correct.

After the event, every guest is in **Events → Attendees** with attended / no-show
status, and in **Contacts** with the phone number normalised to `+9665XXXXXXXX`
(so the same person scanning at the next event is matched, not duplicated). From
there you can build a CRM pipeline, an SMS follow-up, or a mailing list on the
"marketing consent" note.

## API (for other tools)

```
GET  /api/summary                     public counters, now serving, up next
GET  /api/events                      Server-Sent Events stream of the summary
POST /api/join                        {name, phone, party, lang, kids, consent, source}
GET  /api/ticket/:id                  ticket + position + estimate
POST /api/ticket/:id/leave

Staff (header x-staff-pin or ?pin=):
GET  /api/staff/queue
POST /api/staff/call-next
POST /api/staff/add                   walk-up guest
POST /api/staff/ticket/:id/{call|start|done|no-show|requeue|cancel|priority|sync}
POST /api/staff/settings              {eventName, eventNameEn, headsets, sessionMinutes, open, ticketPrefix}
POST /api/staff/reset                 archive today, start numbering again
GET  /api/staff/export.csv
GET  /api/staff/odoo-check
```

This app also lives in the `queue/` folder of the app prototype repository (`ana-almadinah-app-prototype`); this repository is the standalone copy for deployment.

## Files

```
  server.js          HTTP server, queue logic, SSE, staff API
  odoo.js            JSON-RPC client + partner/registration sync
  public/
    join.html        guest page
    staff.html       staff board
    display.html     TV board
    qr.html          printable poster
    queue.css        shared styles (brand colours from the app prototype)
    assets/          brand logos
    vendor/qrcode.js qrcode-generator 1.4.4 (MIT), bundled so the poster works offline
  data/              queue.json + daily archives (git-ignored)
```
