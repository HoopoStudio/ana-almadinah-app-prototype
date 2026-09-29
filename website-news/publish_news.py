#!/usr/bin/env python3
"""Publish a news item to ana-almadinahvr.com.

Does exactly what the site's admin dashboard (pages/dashboard.html + js/dashboard.js)
does when you add a news item:
  1. signs in with the dashboard's Firebase Auth email/password,
  2. uploads the image to Storage at news_images/<ms>_<filename>,
  3. adds a document to the Firestore "news" collection with the fields
     title, title_en, date, imageUrl, content, content_en, createdAt (all strings).
The public news pages read Firestore live, so the item appears immediately.

Credentials are read from the environment, never from this repo:
  ANA_ADMIN_EMAIL, ANA_ADMIN_PASSWORD

Usage:
  python3 website-news/publish_news.py website-news/<item>.json --dry-run   # validate only
  python3 website-news/publish_news.py website-news/<item>.json             # publish
"""
import argparse
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


def request(method, url, body=None, headers=None):
    req = urllib.request.Request(url, data=body, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = resp.read()
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        sys.exit(f"{method} {url.split('?')[0]} failed: HTTP {e.code}\n{detail}")
    return json.loads(raw) if raw else {}


def load_item(path):
    item = json.loads(path.read_text(encoding="utf-8"))
    missing = [k for k in ("title", "date", "content", "image") if not item.get(k)]
    if missing:
        sys.exit(f"{path}: missing required field(s): {', '.join(missing)}")
    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", item["date"]):
        sys.exit(f"{path}: date must be YYYY-MM-DD, got {item['date']!r}")
    # The site inserts titles and content as raw HTML, so keep them plain text.
    for k in TEXT_FIELDS:
        if re.search(r"[<>]", item.get(k, "")):
            sys.exit(f"{path}: {k} must be plain text (no < or >)")
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
    if not email or not password:
        sys.exit("Set ANA_ADMIN_EMAIL and ANA_ADMIN_PASSWORD in the environment first.")
    body = json.dumps({"email": email, "password": password, "returnSecureToken": True}).encode()
    res = request(
        "POST",
        f"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={API_KEY}",
        body,
        {"Content-Type": "application/json"},
    )
    return res["idToken"]


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
    body = json.dumps({"fields": {k: {"stringValue": v} for k, v in values.items()}}).encode()
    req = urllib.request.Request(
        f"{FIRESTORE}/news",
        data=body,
        method="POST",
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        return json.loads(resp.read())["name"].rsplit("/", 1)[-1]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("item", type=Path, help="news item JSON file")
    parser.add_argument("--dry-run", action="store_true", help="validate and check for duplicates, publish nothing")
    args = parser.parse_args()

    item, image = load_item(args.item)
    duplicate = existing_duplicate(item)
    if duplicate:
        sys.exit(f"Already published: {SITE}/pages/news-details.html?id={duplicate}")

    print(f"date:     {item['date']}")
    print(f"title:    {item['title']}")
    print(f"title_en: {item.get('title_en', '')}")
    print(f"image:    {image.name} ({image.stat().st_size // 1024} KB)")
    if args.dry_run:
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
    print(f"  Arabic:  {SITE}/pages/news-details.html?id={doc_id}")
    print(f"  English: {SITE}/pages/news-details.html?id={doc_id}&lang=en")


if __name__ == "__main__":
    main()
