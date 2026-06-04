#!/usr/bin/env python3
"""Extract STS2 card data from slaythespire2.gg page HTML dump."""
import json, re
from pathlib import Path

data_dir = Path("D:/projects/STS2-AGENT/data")
html_path = data_dir / "cards_page.html"

with open(html_path, "r", encoding="utf-8") as f:
    html = f.read()

# Find all self.__next_f.push([1,"..."]) - extract the JSON string content
# The pattern matches: push([1,"CONTENT"])
pattern = re.compile(r'self\.__next_f\.push\(\[1,"((?:[^"\\]|\\.)*)"\]\)')
chunks = pattern.findall(html)
print(f"Found {len(chunks)} RSC chunks, total {sum(len(c) for c in chunks)} chars")

# Concatenate and unescape
big = ""
for c in chunks:
    big += c.replace('\\"', '"').replace('\\n', '\n').replace('\\\\', '\\')

print(f"Concatenated: {len(big)} chars")

# Look for card data pattern
if 'cardType' in big:
    print("cardType found in concatenated data")
if 'CARD' in big:
    print("CARD found in concatenated data")

# Find all card entries by looking for "category":"CARD" patterns
# Each card entry looks like: {"id":"...","name":"...","category":"CARD",...}
card_entries = []
pos = 0
while True:
    idx = big.find('"category":"CARD"', pos)
    if idx == -1:
        break
    # Find the start of this JSON object (look backwards for '{')
    start = big.rfind('{', 0, idx)
    if start == -1:
        pos = idx + 1
        continue
    # Find the end of this JSON object (look forward for matching '}')
    depth = 0
    end = start
    for i in range(start, min(start + 5000, len(big))):
        if big[i] == '{':
            depth += 1
        elif big[i] == '}':
            depth -= 1
            if depth == 0:
                end = i + 1
                break
    if end > start:
        try:
            obj = json.loads(big[start:end])
            if obj.get("category") == "CARD" and "name" in obj:
                card_entries.append(obj)
        except:
            pass
    pos = end

print(f"Parsed {len(card_entries)} card entries")

# Organize by character
by_char = {}
for card in card_entries:
    char = card.get("character", "Unknown")
    if char not in by_char:
        by_char[char] = []
    by_char[char].append(card)

for char, cards in sorted(by_char.items()):
    print(f"  {char}: {len(cards)} cards")

# For any existing markdown files we have, check completeness
for char in ["Ironclad", "Silent", "Defect", "Necrobinder", "The Regent"]:
    have = len(by_char.get(char, []))
    expected = {"Ironclad": 86, "Silent": 86, "Defect": 86, "Necrobinder": 86, "The Regent": 87}
    status = "✅" if have == expected.get(char, 0) else f"❌ ({expected.get(char, '?')} expected)"
    print(f"  {char}: {have} {status}")

# Save all new card files
header = "| # | Card Name | Type | Rarity | Energy | Effect Description |\n"
header += "|---|-----------|------|--------|--------|-------------------|\n"

saved = 0
for char, cards in by_char.items():
    safe_name = char.lower().replace(" ", "_").replace("/", "_")
    out_path = data_dir / f"cards_{safe_name}.md"
    # Skip if already exists and is complete
    if out_path.exists():
        # Don't overwrite existing well-formed files
        if char == "Ironclad" and len(cards) >= 80:
            continue

    with open(out_path, "w", encoding="utf-8") as f:
        f.write(f"# {char} Cards - Slay the Spire 2\n\n")
        f.write(f"> Source: https://slaythespire2.gg/cards\n")
        f.write(f"> Total: {len(cards)} cards\n\n")
        f.write(header)
        for i, card in enumerate(cards, 1):
            name = card.get("name", "?")
            ctype = card.get("cardType", "?")
            rarity = card.get("rarity", "?")
            energy = card.get("energy", "?")
            if energy is None:
                energy = "X"
            effect = str(card.get("description", "")).replace("|", "\\|").replace("\n", " ")
            f.write(f"| {i} | {name} | {ctype} | {rarity} | {energy} | {effect} |\n")
    saved += 1
    print(f"  Saved: {out_path.name} ({len(cards)} cards)")

print(f"\nTotal: {len(card_entries)} cards, {saved} files saved")
