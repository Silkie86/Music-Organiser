"""ChoCo JAMS (Harte chords) and the Jazz Harmony Treebank -> one TSV the theory bench reads (--corpus).

Usage: python3 corpus_to_tsv.py <folder>, where <folder> holds choco/ (a ChoCo release, github.com/smashub/choco,
CC BY 4.0 for the partitions read here) and jht/treebank.json (the Jazz Harmony Treebank, CC BY-NC-SA 4.0: measuring
only, never shipped). Writes <folder>/corpus.tsv.

Columns: source, id, title, genre, key (tonic pc:major|minor or ''), chords, targets.
chords: space separated 'beats|root:maskhex/bass' (PhraseChord encoding; beats = length in beats).
targets: Treebank only: for each chord, the index of the chord it prepares or prolongs in the tree, -1 for the head, '' when no tree.
"""
import csv, glob, json, os, re, sys

ROOT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.getcwd()
NOTES = {'C': 0, 'D': 2, 'E': 4, 'F': 5, 'G': 7, 'A': 9, 'B': 11}
DEG = {1: 0, 2: 2, 3: 4, 4: 5, 5: 7, 6: 9, 7: 11, 9: 14, 11: 17, 13: 21}
SHORT = {
    'maj': '1,3,5', 'min': '1,b3,5', 'dim': '1,b3,b5', 'aug': '1,3,#5', 'maj7': '1,3,5,7', 'min7': '1,b3,5,b7', '7': '1,3,5,b7',
    'dim7': '1,b3,b5,bb7', 'hdim7': '1,b3,b5,b7', 'hdim': '1,b3,b5,b7', 'minmaj7': '1,b3,5,7', 'maj6': '1,3,5,6', 'min6': '1,b3,5,6',
    '9': '1,3,5,b7,9', 'maj9': '1,3,5,7,9', 'min9': '1,b3,5,b7,9', '11': '1,3,5,b7,9,11', 'min11': '1,b3,5,b7,9,11',
    '13': '1,3,5,b7,9,11,13', 'maj13': '1,3,5,7,9,11,13', 'min13': '1,b3,5,b7,9,11,13', 'sus4': '1,4,5', 'sus2': '1,2,5',
    '5': '1,5', '1': '1', '6': '1,3,5,6', '': '1,3,5',
}


def note(s):
    pc = NOTES[s[0].upper()]
    for ch in s[1:]:
        pc += 1 if ch in '#s' else -1 if ch == 'b' else 0
    return pc % 12


def degree(tok):
    m = re.fullmatch(r'([b#s]*)(\d+)', tok)
    if not m or int(m.group(2)) not in DEG:
        return None
    return (DEG[int(m.group(2))] + sum(1 if c in '#s' else -1 for c in m.group(1))) % 12


def harte(label):
    """'G:min7(9)/b3' -> (root, mask, bass) or None."""
    if label in ('N', 'X', 'NC', '') or label.startswith('N'):
        return None
    m = re.fullmatch(r'([A-G][#sb]*)(?::([^(/]*))?(?:\(([^)]*)\))?(?:/([b#s]*\d+))?', label)
    if not m:
        return None
    root = note(m.group(1))
    quality = m.group(2) or ''
    if quality not in SHORT:
        return None
    degs = set(degree(t) for t in SHORT[quality].split(','))
    for t in (m.group(3) or '').split(','):
        t = t.strip()
        if not t:
            continue
        if t.startswith('*'):
            d = degree(t[1:])
            degs.discard(d)
        else:
            d = degree(t)
            if d is not None:
                degs.add(d)
    degs.discard(None)
    # A dominant or major chord with a natural 11 over its major 3rd is a 13 chord as played: the 11 is left out.
    if 4 in degs and 5 in degs and len(degs) >= 5:
        degs.discard(5)
    mask = 0
    for d in degs:
        mask |= 1 << ((root + d) % 12)
    bass = root
    if m.group(4):
        d = degree(m.group(4))
        if d is not None:
            bass = (root + d) % 12
            mask |= 1 << bass
    return root, mask, bass


def enc(c):
    root, mask, bass = c
    return f'{root}:{mask:x}' + (f'/{bass}' if bass != root else '')


def key_of(value):
    m = re.fullmatch(r'([A-G][#b]*)(?::|-)?(maj|major|min|minor)?', value.strip())
    if not m:
        return ''
    return f'{note(m.group(1))}:{"minor" if (m.group(2) or "").startswith("min") else "major"}'


def choco(out):
    parts = [('real-book', 'partitions/real-book/choco'), ('ireal-playlists', 'partitions/ireal-pro/choco/playlists'),
             ('weimar', 'partitions/weimar/choco'), ('jazz-corpus', 'partitions/jazz-corpus/choco'),
             ('ireal-forum-gospel', 'partitions/ireal-pro/choco/forum')]
    for source, folder in parts:
        base = os.path.join(ROOT, 'choco', folder)
        meta = {}
        with open(os.path.join(base, 'meta.csv'), newline='') as f:
            for row in csv.DictReader(f):
                meta[os.path.basename(row['jams_path']).replace('.jams', '')] = row
        skipped = kept = 0
        for path in sorted(glob.glob(os.path.join(base, 'jams-converted', '*.jams'))):
            name = os.path.basename(path)[:-5]
            d = json.load(open(path))
            chords, key = [], ''
            for a in d['annotations']:
                if a['namespace'] == 'chord_harte':
                    for o in a['data']:
                        c = harte(o['value'])
                        if c is None:
                            skipped += 1
                            continue
                        kept += 1
                        chords.append(f'{max(0.25, float(o["duration"])):g}|{enc(c)}')
                elif a['namespace'] == 'key_mode' and a['data'] and not key:
                    key = key_of(str(a['data'][0]['value']))
            if len(chords) < 2:
                continue
            row = meta.get(name, {})
            title = (row.get('title') or row.get('file_title') or row.get('score_title') or d['file_metadata'].get('title') or '').strip()
            out.writerow([source, name, title, row.get('genre', ''), key, ' '.join(chords), ''])
        print(source, 'chords kept', kept, 'skipped', skipped, file=sys.stderr)


def jht_chord(s):
    m = re.fullmatch(r'([A-G][#b]?)(.*)', s)
    root = note(m.group(1))
    q = m.group(2)
    table = {'': '1,3,5', '7': '1,3,5,b7', '^7': '1,3,5,7', '^': '1,3,5', '6': '1,3,5,6', 'm': '1,b3,5', 'm7': '1,b3,5,b7',
             'm6': '1,b3,5,6', 'm^7': '1,b3,5,7', '%7': '1,b3,b5,b7', '%': '1,b3,b5,b7', 'o7': '1,b3,b5,bb7', 'o': '1,b3,b5',
             '+': '1,3,#5', '+7': '1,3,#5,b7', 'sus': '1,4,5,b7', 'sus7': '1,4,5,b7'}
    degs = [degree(t) for t in table[q].split(',')]
    mask = 0
    for d in degs:
        mask |= 1 << ((root + d) % 12)
    return root, mask, root


def jht(out):
    data = json.load(open(os.path.join(ROOT, 'jht', 'treebank.json')))
    for i, t in enumerate(data):
        k = t['key']
        tonic = note(k[0]) + (-1 if '-' in k[1:] else 1 if '#' in k[1:] else 0)
        key = f'{tonic % 12}:{"minor" if k[0].islower() else "major"}'
        num = t['meter']['numerator']
        starts = [(m - 1) * num + (b - 1) for m, b in zip(t['measures'], t['beats'])]
        ends = starts[1:] + [t['measures'][-1] * num]
        chords = [f'{max(1, e - s)}|{enc(jht_chord(c))}' for c, s, e in zip(t['chords'], starts, ends)]
        targets = ''
        if t.get('trees'):
            tree = t['trees'][0]['complete_constituent_tree']
            leaves = []
            deps = {}

            def walk(node):
                # Returns the leaf index heading this node.
                if not node['children']:
                    leaves.append(node['label'])
                    return len(leaves) - 1
                left, right = (walk(c) for c in node['children'])
                deps[left] = right
                return right
            walk(tree)
            n = len(leaves)
            if [c for c in t['chords'][:n]] == leaves:
                targets = ' '.join(str(deps.get(j, -1)) for j in range(n))
        out.writerow(['treebank', str(i), t['title'], '', key, ' '.join(chords), targets])


if __name__ == '__main__':
    w = csv.writer(open(os.path.join(ROOT, 'corpus.tsv'), 'w', newline=''), delimiter='\t')
    choco(w)
    jht(w)
