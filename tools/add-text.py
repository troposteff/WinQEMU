"""Adds or replaces entries in an English .resx and its .fr.resx.
Usage: python addtext.py <path/to/X.resx> <entries.json>
entries.json: [{"key": "...", "en": "...", "fr": "...", "comment": "optional"}]
Keys must be C# identifiers. Values are written XML-escaped; keep {0} placeholders identical in both languages."""
import json, re, sys
from xml.sax.saxutils import escape

def upsert(path, entries, lang):
    text = open(path, encoding='utf-8', newline='').read()
    for e in entries:
        key = e['key']
        if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', key):
            sys.exit(f'bad key: {key}')
        comment = f'<comment>{escape(e["comment"])}</comment>' if e.get('comment') and lang == 'en' else ''
        node = f'  <data name="{key}" xml:space="preserve"><value>{escape(e[lang])}</value>{comment}</data>\r\n'
        pattern = re.compile(r'  <data name="' + re.escape(key) + r'"[^>]*>.*?</data>\r?\n', re.S)
        if pattern.search(text):
            text = pattern.sub(lambda _: node, text, count=1)
        else:
            text = text.replace('</root>', node + '</root>', 1)
    open(path, 'w', encoding='utf-8', newline='').write(text)

path, data = sys.argv[1], json.load(open(sys.argv[2], encoding='utf-8'))
for e in data:
    for lang in ('en', 'fr'):
        if lang not in e: sys.exit(f'{e["key"]}: missing {lang}')
    if sorted(re.findall(r'\{\d+[^}]*\}', e['en'])) != sorted(re.findall(r'\{\d+[^}]*\}', e['fr'])):
        sys.exit(f'{e["key"]}: placeholders differ')
upsert(path, data, 'en')
upsert(path.replace('.resx', '.fr.resx'), data, 'fr')
print(f'{len(data)} entries in {path}')
