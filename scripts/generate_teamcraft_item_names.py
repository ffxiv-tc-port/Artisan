#!/usr/bin/env python3
"""Regenerates Artisan/RawInformation/TeamcraftItemNames.tsv.

The TSV maps craftable-item names in languages the TC game client does NOT
ship (English from global data, Simplified Chinese from the CN client) to
item IDs, so Teamcraft "Copy as Text" exports made under those display
languages can still be imported. Only items that are some recipe's result
are included to keep the embedded resource small.

Source (public datamining repo, no game install needed):
  https://github.com/xivapi/ffxiv-datamining (csv/<lang>/Item.csv, csv/en/Recipe.csv)
"""
import csv
import io
import sys
import urllib.request

EN_ITEM = "https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en/Item.csv"
EN_RECIPE = "https://raw.githubusercontent.com/xivapi/ffxiv-datamining/master/csv/en/Recipe.csv"
CN_ITEM = "https://raw.githubusercontent.com/thewakingsands/ffxiv-datamining-cn/master/Item.csv"
OUT = "Artisan/RawInformation/TeamcraftItemNames.tsv"


def fetch_csv(url):
    print(f"fetching {url}")
    with urllib.request.urlopen(url) as resp:
        text = resp.read().decode("utf-8-sig")
    rows = list(csv.reader(io.StringIO(text)))
    if rows[0][0] == "key":
        # SaintCoinach style: key row, column-name row, type row, then data
        return rows[1], rows[3:]
    # single header row (first column "#"), data from row1
    return rows[0], rows[1:]


def column(header, name):
    return header.index(name)


def main():
    en_rec_header, en_rec_rows = fetch_csv(EN_RECIPE)
    result_col = column(en_rec_header, "ItemResult")
    craftable = set()
    for row in en_rec_rows:
        try:
            item_id = int(row[result_col])
        except (ValueError, IndexError):
            continue
        if item_id > 0:
            craftable.add(item_id)
    print(f"{len(craftable)} craftable result items")

    names = {}  # id -> [en, cn]
    for idx, url in ((0, EN_ITEM), (1, CN_ITEM)):
        header, rows = fetch_csv(url)
        name_col = column(header, "Name")
        for row in rows:
            try:
                item_id = int(row[0])
            except (ValueError, IndexError):
                continue
            if item_id not in craftable:
                continue
            name = row[name_col].strip() if len(row) > name_col else ""
            if not name:
                continue
            names.setdefault(item_id, ["", ""])[idx] = name

    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        for item_id in sorted(names):
            en, cn = names[item_id]
            f.write(f"{item_id}\t{en}\t{cn}\n")
    print(f"wrote {len(names)} rows to {OUT}")


if __name__ == "__main__":
    sys.exit(main())
