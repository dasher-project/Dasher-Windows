#!/usr/bin/env python3
"""Generate .resx resource files from the shared UI string catalogue.

Consumes shared-resources/ui-strings.json (the dasher-shared-resources git
submodule) — the canonical English source plus all merged frontend
translations — and emits:

    src/Dasher.Windows/Resources/Strings.resx          (neutral = English)
    src/Dasher.Windows/Resources/Strings.<locale>.resx (one per locale)

The .NET SDK builds culture-suffixed .resx into satellite assemblies
automatically; the Loc service resolves via ResourceManager with
CurrentUICulture fallback.

Usage:
    python Scripts/gen-translations.py
"""

import json
import os
import sys
from xml.sax.saxutils import escape

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
SHARED = os.path.join(REPO, "shared-resources", "ui-strings.json")
OUT_DIR = os.path.join(REPO, "src", "Dasher.Windows", "Resources")

RESX_HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""


def write_resx(path, entries, locale):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(RESX_HEADER)
        f.write(f"  <!-- Generated from shared-resources/ui-strings.json ({locale}). Do not edit by hand. -->\n")
        for key in sorted(entries):
            value = entries[key]
            f.write(f'  <data name="{escape(key)}" xml:space="preserve">\n')
            f.write(f"    <value>{escape(value)}</value>\n")
            f.write("  </data>\n")
        f.write("</root>\n")


def main():
    if not os.path.exists(SHARED):
        print(f"error: {SHARED} not found — run 'git submodule update --init'", file=sys.stderr)
        return 1

    with open(SHARED, encoding="utf-8") as f:
        data = json.load(f)

    shared = data["shared"]
    locales = data["meta"]["locales"]

    os.makedirs(OUT_DIR, exist_ok=True)

    # Neutral (English) resource — every key must have one for fallback.
    neutral = {}
    for key, translations in shared.items():
        if not isinstance(translations, dict):
            continue
        neutral[key] = translations.get("en")
        if neutral[key] is None:
            # No English entry: skip the key entirely rather than ship gaps.
            print(f"warning: key '{key}' has no 'en' entry — skipped", file=sys.stderr)
            del neutral[key]
    write_resx(os.path.join(OUT_DIR, "Strings.resx"), neutral, "en")
    print(f"Strings.resx: {len(neutral)} keys (neutral/English)")

    # Per-locale resources — only non-empty translations for that locale.
    for locale in locales:
        if locale == "en":
            continue
        entries = {}
        for key, translations in shared.items():
            if not isinstance(translations, dict):
                continue
            value = translations.get(locale)
            if value and value.strip():
                entries[key] = value
        if not entries:
            continue
        write_resx(os.path.join(OUT_DIR, f"Strings.{locale}.resx"), entries, locale)
        print(f"Strings.{locale}.resx: {len(entries)} keys")

    return 0


if __name__ == "__main__":
    sys.exit(main())
