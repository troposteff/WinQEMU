"""Adds or replaces entries in an English .resx and its .fr.resx.
Usage: python addtext.py <path/to/X.resx> <entries.json>
entries.json: [{"key": "...", "en": "...", "fr": "...", "comment": "optional"}]
Keys must be C# identifiers. Values are written XML-escaped; keep {0} placeholders identical in both languages."""
import json, re, sys
from xml.sax.saxutils import escape

def upsert(path, entries, lang):
    """The file's new text; written by the caller once both languages are ready."""
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
    return text

path, data = sys.argv[1], json.load(open(sys.argv[2], encoding='utf-8'))
for e in data:
    for lang in ('en', 'fr'):
        if lang not in e: sys.exit(f'{e["key"]}: missing {lang}')
    if sorted(re.findall(r'\{\d+[^}]*\}', e['en'])) != sorted(re.findall(r'\{\d+[^}]*\}', e['fr'])):
        sys.exit(f'{e["key"]}: placeholders differ')
# The English file is named: given the French one, English values went into it, then the script failed on X.fr.fr.resx.
# Both files exist and both texts are made before either is written, so the two never get out of step.
if path.endswith('.fr.resx'):
    sys.exit('give the English file (X.resx), not X.fr.resx')
french = path[:-len('.resx')] + '.fr.resx'
for p in (path, french):
    try:
        open(p, encoding='utf-8').close()
    except OSError:
        sys.exit(f'missing: {p}')
english_text, french_text = upsert(path, data, 'en'), upsert(french, data, 'fr')
open(path, 'w', encoding='utf-8', newline='').write(english_text)
open(french, 'w', encoding='utf-8', newline='').write(french_text)
print(f'{len(data)} entries in {path}')
