#!/usr/bin/env python3
"""Publish or update a news item on ana-almadinahvr.com.

Does exactly what the site's admin dashboard (pages/dashboard.html + js/dashboard.js)
does when you add or edit a news item:
  1. signs in with the dashboard's Firebase Auth email/password,
  2. uploads the image (if any) to Storage at news_images/<ms>_<filename>,
  3. adds a document to the Firestore "news" collection with the fields
     title, title_en, date, imageUrl, content, content_en, createdAt (all strings),
     or, with --update, changes only the fields given in the JSON file.
The public news pages read Firestore live, so changes appear immediately.

Credentials are never stored in this repo. They are read from ANA_ADMIN_EMAIL and
ANA_ADMIN_PASSWORD if set, otherwise the script asks for them in the terminal.

Usage:
  python3 website-news/publish_news.py website-news/<item>.json --dry-run    # check only
  python3 website-news/publish_news.py website-news/<item>.json              # publish new item
  python3 website-news/publish_news.py website-news/fixes/<fix>.json --update <doc id>
"""
import argparse
import getpass
import json
import mimetypes
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

# Public client config from the site's js/firebase-config.js.
API_KEY = "AIzaSyBrT7Sl_hTwHXQqM69x1jjKKJSSP0NHuM0"
PROJECT_ID = "ana-almadinah-web"
BUCKET = "ana-almadinah-web.firebasestorage.app"

FIRESTORE = f"https://firestore.googleapis.com/v1/projects/{PROJECT_ID}/databases/(default)/documents"
STORAGE = f"https://firebasestorage.googleapis.com/v0/b/{BUCKET}/o"
SITE = "https://ana-almadinahvr.com"
TEXT_FIELDS = ("title", "title_en", "content", "content_en")
EDITABLE = ("title", "title_en", "date", "content", "content_en")


def request(method, url, body=None, headers=None):
    req = urllib.request.Request(url, data=body, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = resp.read()
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        sys.exit(f"{method} {url.split('?')[0]} failed: HTTP {e.code}\n{detail}")
    return json.loads(raw) if raw else {}


def load_item(path, updating):
    item = json.loads(path.read_text(encoding="utf-8"))
    if updating:
        if not any(item.get(k) for k in EDITABLE + ("image",)):
            sys.exit(f"{path}: nothing to update; give at least one of {', '.join(EDITABLE)}, image")
    else:
        missing = [k for k in ("title", "date", "content", "image") if not item.get(k)]
        if missing:
            sys.exit(f"{path}: missing required field(s): {', '.join(missing)}")
    if "date" in item and not re.fullmatch(r"\d{4}-\d{2}-\d{2}", item["date"]):
        sys.exit(f"{path}: date must be YYYY-MM-DD, got {item['date']!r}")
    # The site inserts titles and content as raw HTML, so keep them plain text.
    for k in TEXT_FIELDS:
        if re.search(r"[<>]", item.get(k, "")):
            sys.exit(f"{path}: {k} must be plain text (no < or >)")
    image = None
    if item.get("image"):
        image = (path.parent / item["image"]).resolve()
        if not image.is_file():
            sys.exit(f"{path}: image not found: {image}")
    return item, image


def existing_duplicate(item):
    """Return the id of a live news doc with the same title, if any (public read)."""
    docs = request("GET", f"{FIRESTORE}/news?pageSize=300").get("documents", [])
    for doc in docs:
        fields = doc.get("fields", {})
        for k in ("title", "title_en"):
            if item.get(k) and fields.get(k, {}).get("stringValue") == item[k]:
                return doc["name"].rsplit("/", 1)[-1]
    return None


def sign_in():
    email, password = os.environ.get("ANA_ADMIN_EMAIL"), os.environ.get("ANA_ADMIN_PASSWORD")
    if not (email and password):
        if not sys.stdin.isatty():
            sys.exit("Set ANA_ADMIN_EMAIL and ANA_ADMIN_PASSWORD, or run this in a terminal to type them.")
        print("Log in with your website dashboard account.")
        email = email or input("Dashboard email: ").strip()
        password = password or getpass.getpass("Dashboard password (hidden as you type): ")
    body = json.dumps({"email": email, "password": password, "returnSecureToken": True}).encode()
    req = urllib.request.Request(
        f"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={API_KEY}",
        data=body,
        method="POST",
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            return json.loads(resp.read())["idToken"]
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        if "INVALID_LOGIN_CREDENTIALS" in detail or "INVALID_PASSWORD" in detail or "EMAIL_NOT_FOUND" in detail:
            sys.exit("Login failed: wrong email or password. Nothing was changed.")
        sys.exit(f"Login failed: HTTP {e.code}\n{detail}")


def upload_image(image, token):
    name = f"news_images/{int(time.time() * 1000)}_{image.name}"
    content_type = mimetypes.guess_type(image.name)[0] or "application/octet-stream"
    res = request(
        "POST",
        f"{STORAGE}?name={urllib.parse.quote(name, safe='')}",
        image.read_bytes(),
        {"Authorization": f"Firebase {token}", "Content-Type": content_type},
    )
    download_token = res["downloadTokens"].split(",")[0]
    url = f"{STORAGE}/{urllib.parse.quote(name, safe='')}?alt=media&token={download_token}"
    return name, url


def delete_image(name, token):
    req = urllib.request.Request(
        f"{STORAGE}/{urllib.parse.quote(name, safe='')}",
        method="DELETE",
        headers={"Authorization": f"Firebase {token}"},
    )
    try:
        urllib.request.urlopen(req, timeout=60)
    except urllib.error.URLError as e:
        print(f"warning: could not remove uploaded image {name}: {e}", file=sys.stderr)


def string_fields(values):
    return json.dumps({"fields": {k: {"stringValue": v} for k, v in values.items()}}).encode()


def create_doc(item, image_url, token):
    created_at = datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
    values = {
        "title": item["title"],
        "title_en": item.get("title_en", ""),
        "date": item["date"],
        "imageUrl": image_url,
        "content": item["content"],
        "content_en": item.get("content_en", ""),
        "createdAt": created_at,
    }
    req = urllib.request.Request(
        f"{FIRESTORE}/news",
        data=string_fields(values),
        method="POST",
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        return json.loads(resp.read())["name"].rsplit("/", 1)[-1]


def update_doc(doc_id, values, token):
    # Like the dashboard's edit: only the given fields change, createdAt is left alone.
    mask = "&".join(f"updateMask.fieldPaths={k}" for k in values)
    req = urllib.request.Request(
        f"{FIRESTORE}/news/{doc_id}?{mask}&currentDocument.exists=true",
        data=string_fields(values),
        method="PATCH",
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
    )
    urllib.request.urlopen(req, timeout=60).close()


def print_links(doc_id):
    print(f"  Arabic:  {SITE}/pages/news-details.html?id={doc_id}")
    print(f"  English: {SITE}/pages/news-details.html?id={doc_id}&lang=en")


def short(text):
    text = text.replace("\n", " ")
    return text if len(text) <= 70 else text[:67] + "..."


def publish(item, image, dry_run):
    duplicate = existing_duplicate(item)
    if duplicate:
        sys.exit(f"Already published: {SITE}/pages/news-details.html?id={duplicate}")

    print(f"date:     {item['date']}")
    print(f"title:    {item['title']}")
    print(f"title_en: {item.get('title_en', '')}")
    print(f"image:    {image.name} ({image.stat().st_size // 1024} KB)")
    if dry_run:
        print("Dry run OK: valid, not yet published. Nothing was uploaded.")
        return

    token = sign_in()
    name, image_url = upload_image(image, token)
    try:
        doc_id = create_doc(item, image_url, token)
    except urllib.error.HTTPError as e:
        delete_image(name, token)
        sys.exit(f"Creating the news document failed: HTTP {e.code}\n{e.read().decode('utf-8', 'replace')}")

    print(f"Published news/{doc_id}")
    print_links(doc_id)


def update(doc_id, item, image, dry_run):
    current = request("GET", f"{FIRESTORE}/news/{doc_id}").get("fields", {})
    print(f"Updating: {current.get('title', {}).get('stringValue', doc_id)}")
    changes = {
        k: item[k]
        for k in EDITABLE
        if k in item and item[k] != current.get(k, {}).get("stringValue")
    }
    for k, v in changes.items():
        print(f"  {k}:\n    was: {short(current.get(k, {}).get('stringValue', ''))}\n    now: {short(v)}")
    if image:
        print(f"  imageUrl: replaced with {image.name}")
    if not changes and not image:
        print("Nothing to change: the live item already matches.")
        return
    if dry_run:
        print("Dry run OK: nothing was changed.")
        return

    token = sign_in()
    uploaded = None
    if image:
        uploaded, changes["imageUrl"] = upload_image(image, token)
    try:
        update_doc(doc_id, changes, token)
    except urllib.error.HTTPError as e:
        if uploaded:
            delete_image(uploaded, token)
        sys.exit(f"Updating the news document failed: HTTP {e.code}\n{e.read().decode('utf-8', 'replace')}")

    print(f"Updated news/{doc_id}")
    print_links(doc_id)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("item", type=Path, help="news item JSON file")
    parser.add_argument("--update", metavar="DOC_ID", help="change an existing news item instead of adding one")
    parser.add_argument("--dry-run", action="store_true", help="check and show what would happen, change nothing")
    args = parser.parse_args()

    item, image = load_item(args.item, updating=bool(args.update))
    if args.update:
        update(args.update, item, image, args.dry_run)
    else:
        publish(item, image, args.dry_run)


if __name__ == "__main__":
    main()
