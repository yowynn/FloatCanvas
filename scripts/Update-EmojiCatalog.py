"""Rebuild the embedded picker data using only the Python standard library."""
import argparse
import gzip
from pathlib import Path
import re
from urllib.request import urlopen
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]
EMOJI = "https://www.unicode.org/Public/18.0.0/emoji/emoji-test.txt"
CLDR = "https://raw.githubusercontent.com/unicode-org/cldr/release-49/common"
GROUPS = ["Smileys & Emotion", "People & Body", "Animals & Nature", "Food & Drink",
          "Travel & Places", "Activities", "Objects", "Symbols", "Flags"]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--cache", type=Path, help="Directory for cached upstream data files")
args = parser.parse_args()


def read(name, url):
    file = args.cache / name if args.cache else None
    if file and file.exists():
        return file.read_bytes()
    with urlopen(url, timeout=45) as response:
        content = response.read()
    if file:
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_bytes(content)
    return content


def annotations(language):
    result = {}
    for section in ("annotations", "annotationsDerived"):
        xml = read(f"{section}-{language}.xml", f"{CLDR}/{section}/{language}.xml")
        for item in ElementTree.fromstring(xml).iter("annotation"):
            value = (item.text or "").strip()
            if value and value not in ("↑↑↑", "∅∅∅"):
                result.setdefault(item.attrib["cp"].replace("\ufe0f", ""), {})[item.get("type", "keywords")] = value
    return result


en, zh = annotations("en"), annotations("zh")
rows, aliases = {}, {}
source = read("emoji-test.txt", EMOJI).decode("utf-8")
for line in source.splitlines():
    if line.startswith("# group: "):
        group = line.removeprefix("# group: ")
    if not line or line.startswith("#") or group == "Component":
        continue
    codepoints, status, name = re.fullmatch(r"([0-9A-F ]+)\s*;\s*([\w-]+)\s*#\s*\S+\s+E[\d.]+\s+(.+)", line).groups()
    glyph = "".join(chr(int(cp, 16)) for cp in codepoints.split())
    key = glyph.replace("\ufe0f", "")
    if status == "fully-qualified":
        english, chinese = en[key], zh[key]
        rows[key] = [glyph, str(GROUPS.index(group)), chinese["tts"], english.get("tts", name),
                     chinese.get("keywords", ""), english.get("keywords", "")]
    else:
        aliases.setdefault(key, []).append(glyph)

assert set(aliases) <= rows.keys()
assert len(rows) == int(re.search(r"^# fully-qualified\s*:\s*(\d+)", source, re.M)[1])
lines = ["\t".join(row + [" ".join(aliases.get(key, []))]) for key, row in rows.items()]
assert all(line.count("\t") == 6 for line in lines)
dest = ROOT / "src/FloatCanvas.Core/Data/emoji-18.tsv.gz"
dest.parent.mkdir(parents=True, exist_ok=True)
dest.write_bytes(gzip.compress(("\n".join(lines) + "\n").encode("utf-8"), mtime=0))
print(f"{len(rows)} emoji in {dest.name}")
