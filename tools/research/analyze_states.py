"""Local-only FH2 capture analysis. Standard library; never emits full dump JSON.

Usage: python tools/research/analyze_states.py --input /path/to/repository --output research/generated
Historical 18-capture ranking only; production state rules live in Core.
Generated reports describe that historical experiment, not current live validation.
Labels are used only for known captures; UNLABELED_* never enter supervised scoring.
"""
import argparse
import collections
import csv
import datetime as dt
import hashlib
import itertools
import json
import math
import pathlib
import re
import statistics
import struct

REGISTRY_SOURCE = 'AudioEngine+0x630'
FORMATS = {'byte': ('B', 1), 'int16': ('h', 2), 'int32': ('i', 4), 'uint32': ('I', 4),
           'int64': ('q', 8), 'uint64': ('Q', 8), 'float': ('f', 4), 'double': ('d', 8)}
FORMATS.update({f'uint32.bit{bit}': ('I', 4) for bit in range(32)})

def address(s):
    return int(s, 0) if isinstance(s, str) else s

def read_value(raw, offset, kind):
    fmt, size = FORMATS[kind]
    if raw is None or offset + size > len(raw):
        return None
    value = struct.unpack_from('<' + fmt, raw, offset)[0]
    return (value >> int(kind.split('.bit')[1])) & 1 if '.bit' in kind else value

def safe_value(v):
    return v if not isinstance(v, float) or math.isfinite(v) else str(v)

def ui_group(label):
    if label.startswith('UNLABELED_'):
        return None
    if label.startswith('photomode_'):
        return 'photo'
    if label.startswith('pause_'):
        return 'pause'
    if label.startswith('worldmap_'):
        return 'map'
    if label.startswith(('event_', 'roadtrip_', 'gameplay_')):
        return 'gameplay'
    raise ValueError('Unsupported known label: ' + label)

def race_group(label):
    if label.startswith('event_'):
        return 'event'
    if label.startswith('roadtrip_'):
        return 'roadtrip'
    return None  # Underlying race state of pause/photo is not supplied.

def map_group(label):
    group = ui_group(label)
    return None if group is None else 'map' if group == 'map' else 'non_map'

def pearson(a, b):
    if len(a) < 3:
        return None
    ma, mb = statistics.mean(a), statistics.mean(b)
    da, db = [x - ma for x in a], [x - mb for x in b]
    den = math.sqrt(sum(x*x for x in da) * sum(y*y for y in db))
    return sum(x*y for x, y in zip(da, db)) / den if den else None

def repetition(values, groups):
    grouped = {g: [v for v, group in zip(values, groups) if group == g] for g in sorted(set(groups))}
    pairs = [(a, b) for vs in grouped.values() for a, b in itertools.combinations(vs, 2)]
    repeat = sum(a == b for a, b in pairs) / len(pairs) if pairs else 0.0
    modes = {g: collections.Counter(vs).most_common(1)[0][0] for g, vs in grouped.items()}
    between = list(itertools.combinations(modes.values(), 2))
    separation = sum(a != b for a, b in between) / len(between) if between else 0.0
    return repeat, separation, grouped, modes

def score_feature(values, groups, nuisances, coverage, kind):
    repeat, separation, grouped, modes = repetition(values, groups)
    if separation == 0 or repeat < 0.75:
        return None
    correlations = {key: pearson([float(v) for v in values], val) for key, val in nuisances.items()}
    max_corr = max((abs(c) for c in correlations.values() if c is not None), default=0)
    # Correlation is a penalty/control, never evidence that causal independence is proved.
    nuisance_score = 10 * (1 - max_corr)
    small = all(float(v).is_integer() and 0 <= v <= 15 for v in values)
    simple = 10 if small else 7 if all(float(v).is_integer() for v in values) else 4
    base = 40*repeat + 30*separation + simple + 10*coverage + nuisance_score
    singleton = [g for g, vs in grouped.items() if len(vs) < 2]
    factor = 0.8 if singleton else 1.0
    result = dict(score=round(base*factor, 3), repeatability=repeat, separation=separation,
                  group_values={g: list(map(safe_value, vs)) for g, vs in grouped.items()},
                  group_representatives=modes, group_counts={g: len(vs) for g, vs in grouped.items()},
                  singleton_groups=singleton, correlations={k: round(c, 5) if c is not None else None for k, c in correlations.items()},
                  causal_independence_proven=False, production_accepted=False)
    return result

def changed_ranges(a, b):
    ranges, start, changed = [], None, 0
    for i in range(min(len(a), len(b)) + 1):
        diff = i < min(len(a), len(b)) and a[i] != b[i]
        if diff:
            changed += 1
            if start is None:
                start = i
        elif start is not None:
            ranges.append({'offset': hex(start), 'length': i - start})
            start = None
    return changed, ranges

def load_inputs(root):
    labels = json.loads((root/'labels.json').read_text(encoding='utf-8-sig'))
    with (root/'state_signatures.csv').open(encoding='utf-8-sig', newline='') as csv_stream:
        rows = list(csv.DictReader(csv_stream))
    csv_by_file = {row['file']: row for row in rows}
    if set(labels) != set(csv_by_file):
        raise ValueError('CSV and labels.json must cover identical files')
    captures = []
    for file, label in labels.items():
        path = root/'dumps'/file
        data = json.loads(path.read_bytes())  # Parsed locally; never printed.
        blocks = {}
        for block in data['Blocks']:
            key = (address(block['Address']), block['Source'])
            raw = bytes.fromhex(block['Hex'])
            if key in blocks or len(raw) != block['Size']:
                raise ValueError(f'Duplicate block or inconsistent Size: {file}, {key}')
            blocks[key] = raw
        audio_blocks = [(key, raw) for key, raw in blocks.items() if key[1] == 'AudioEngine']
        if len(audio_blocks) != 1:
            raise ValueError('Expected one exact AudioEngine root block')
        audio_key, audio = audio_blocks[0]
        roots = {r['Name']: address(r['Address']) for r in data['Roots']}
        target = read_value(audio, 0x630, 'uint64')
        registry = blocks.get((target, REGISTRY_SOURCE))
        if registry is None:
            raise ValueError(f'{file}: raw AudioEngine+0x630 pointer does not resolve to the captured registry block')
        cap = dict(file=file, label=label, known=ui_group(label) is not None,
                   captured_utc=data['CapturedUtc'], seconds=dt.datetime.fromisoformat(re.sub(r'(\.\d{6})\d+', r'\1', data['CapturedUtc']).replace('Z', '+00:00')).timestamp(),
                   sha256=hashlib.sha256(path.read_bytes()).hexdigest(), blocks=blocks,
                   audio_address=audio_key[0], registry_address=target,
                   raw_pointer_path_valid=roots.get('AudioEngine') == audio_key[0],
                   speed_mph=read_value(audio, 0x90, 'float'),
                   position=struct.unpack_from('<fff', audio, 0xA0),
                   velocity=struct.unpack_from('<fff', audio, 0x170),
                   game_base=data['GameBase'], raw_car_id=data.get('RawCarId'), roots=roots)
        mismatches = []
        for column, text in csv_by_file[file].items():
            if column.startswith(('i32_', 'f32_')):
                off = int(column.split('_')[1], 16)
                actual = read_value(registry, off, 'int32' if column.startswith('i32') else 'float')
                expected = float(text)
                if not math.isclose(actual, expected, rel_tol=1e-7, abs_tol=1e-40):
                    mismatches.append(column)
        if csv_by_file[file]['label'] != label:
            mismatches.append('label')
        compact = csv_by_file[file]
        if not math.isclose(cap['speed_mph'], float(compact['speed_mph']), rel_tol=1e-7, abs_tol=1e-7):
            mismatches.append('speed_mph')
        if not math.isclose(cap['speed_mph']*1.609344, float(compact['speed_kmh']), rel_tol=1e-6, abs_tol=1e-6):
            mismatches.append('speed_kmh')
        for field, actual in [('captured_utc', cap['captured_utc']), ('game_base', cap['game_base']), ('raw_car_id', cap['raw_car_id'])]:
            if compact[field] != actual:
                mismatches.append(field)
        raw_car = audio[0x278:0x278+64].split(b'\0', 1)[0].decode('ascii', errors='replace')
        cap['raw_car_matches_metadata'] = raw_car == cap['raw_car_id']
        cap['csv_mismatches'] = mismatches
        captures.append(cap)
    return captures, rows

def verify_pointer_path(cap, key):
    parts = key[1].split('+')
    current = cap['roots'].get(parts[0])
    if current is None:
        return False
    prefix = parts[0]
    for offset in parts[1:]:
        if not re.fullmatch(r'0x[0-9a-fA-F]+', offset):
            return False
        raw = cap['blocks'].get((current, prefix))
        if raw is None:
            alternatives = [raw for (addr, _), raw in cap['blocks'].items() if addr == current]
            if not alternatives or any(raw != alternatives[0] for raw in alternatives):
                return False
            raw = alternatives[0]
        current = read_value(raw, int(offset, 16), 'uint64')
        if current is None:
            return False
        prefix += '+' + offset
    return current == key[0]

def field_evidence(captures, key, offset, kind, target):
    group_fn = {'ui': ui_group, 'race': race_group, 'map': map_group}[target]
    indices = [i for i, cap in enumerate(captures) if group_fn(cap['label']) is not None]
    values = [read_value(captures[i]['blocks'].get(key), offset, kind) for i in indices]
    if any(v is None or not math.isfinite(v) for v in values) or len(set(values)) < 2:
        return None
    if any(abs(v) > 65535 or (v != 0 and abs(v) < 1e-6) for v in values):
        return None  # This scan deliberately targets simple bounded flags/enums, not arbitrary telemetry.
    groups = [group_fn(captures[i]['label']) for i in indices]
    nuisances = {'speed_mph': [captures[i]['speed_mph'] for i in indices],
                 'time': [captures[i]['seconds'] - captures[indices[0]]['seconds'] for i in indices]}
    for axis in range(3):
        nuisances['position_' + 'xyz'[axis]] = [captures[i]['position'][axis] for i in indices]
    nuisance_vel = [math.sqrt(sum(v*v for v in captures[i]['velocity'])) for i in indices]
    nuisances['velocity_magnitude'] = nuisance_vel
    coverage = sum(key in cap['blocks'] for cap in captures) / len(captures)
    path_results = [verify_pointer_path(cap, key) if key in cap['blocks'] else False for cap in captures]
    path_stability = sum(path_results)/len(captures)
    scores = score_feature(values, groups, nuisances, path_stability, kind)
    if scores is None:
        return None
    float_fragments = []
    if kind in ('byte', 'int16') or '.bit' in kind:
        for container_kind in ('float', 'double'):
            container_size = FORMATS[container_kind][1]
            container_offset = offset // container_size * container_size
            container = [read_value(captures[i]['blocks'].get(key), container_offset, container_kind) for i in indices]
            if all(v is not None and math.isfinite(v) and abs(v) <= 1e9 and (v == 0 or abs(v) >= 1e-6) for v in container) and len(set(container)) > 1:
                float_fragments.append({'container_type': container_kind, 'container_offset': hex(container_offset), 'known_values': container})
        if float_fragments:
            scores['score'] = round(max(0, scores['score']-12), 3)
    pointer_fragments = []
    if kind not in ('float', 'double', 'int64', 'uint64'):
        ptr_offset = offset // 8 * 8
        pointer_values = [read_value(captures[i]['blocks'].get(key), ptr_offset, 'uint64') for i in indices]
        if all(v is not None and (v == 0 or 0x100000000 <= v <= 0x7fffffffffff and v % 8 == 0) for v in pointer_values) and len(set(pointer_values)) > 1:
            pointer_fragments.append({'container_type': 'pointer-shaped uint64', 'container_offset': hex(ptr_offset), 'known_values': list(map(hex, pointer_values))})
            scores['score'] = round(max(0, scores['score']-18), 3)
    if target == 'race':
        scores['score'] = round(min(scores['score']*0.75, 60.0), 3)
    scores.update(target=target, source=key[1], block_address=hex(key[0]), absolute_address=hex(key[0]+offset),
                  relative_offset=hex(offset), type=kind, width=FORMATS[kind][1], capture_coverage=coverage,
                  pointer_path=key[1], pointer_path_raw_valid_fraction=path_stability,
                  pointer_path_valid_by_capture=path_results,
                  bit_mask=hex(1 << int(kind.split('.bit')[1])) if '.bit' in kind else None,
                  possible_float_fragments=float_fragments,
                  possible_pointer_fragments=pointer_fragments,
                  values=[{'file': c['file'], 'label': c['label'], 'known': c['known'],
                           'value': safe_value(read_value(c['blocks'].get(key), offset, kind))} for c in captures],
                  reasoning='Ranked solely on known-label repeatability, between-group separation, bounded enum/flag shape, '
                            'capture coverage and numeric motion/time correlation penalties. No supplied-clue bonus or ASCII-name activation inference. '
                            'Ranking alone does not grant production acceptance; structural guards and independent controls are reviewed separately.'
                            + (' A byte/word/bit fragment also belongs to a plausible changing float/double interpretation; score penalized by 12.' if float_fragments else '')
                            + (' A scalar/bit fragment also belongs to a changing canonical aligned pointer-shaped uint64; score penalized by 18.' if pointer_fragments else ''))
    return scores

def scan(captures, top):
    keys = sorted(set().union(*(c['blocks'].keys() for c in captures)), key=lambda k: (k[1], k[0]))
    all_candidates = {'ui': [], 'race': [], 'map': []}
    examined = 0
    known = [i for i, c in enumerate(captures) if c['known']]
    for key in keys:
        raws = [c['blocks'].get(key) for c in captures]
        if any(raws[i] is None for i in known):
            continue
        length = min(len(raws[i]) for i in known)
        baseline = raws[known[0]]
        # Raw text filtering reduces numeric interpretations of cached string storage.
        text_offsets = set()
        for match in re.finditer(rb'[\x20-\x7e]{4,}', baseline):
            text_offsets.update(range(match.start(), match.end()))
        for kind, (_, size) in FORMATS.items():
            start = (-key[0]) % size
            for off in range(start, length-size+1, size):
                examined += 1
                if any(off+j in text_offsets for j in range(size)):
                    continue
                initial = baseline[off:off+size]
                if all(raws[i][off:off+size] == initial for i in known[1:]):
                    continue
                for target in all_candidates:
                    evidence = field_evidence(captures, key, off, kind, target)
                    if evidence is not None:
                        all_candidates[target].append(evidence)
    counts = {k: len(v) for k, v in all_candidates.items()}
    def rank(c):
        return (-c['score'], c['source'], int(c['absolute_address'], 16),
                0 if c['type'] == 'int32' else 1, c['width'], c['type'])
    # Collapse equal-value alternate widths at one address, not distinct addresses or signatures.
    retained = []
    hypothesis_ranks = []
    for target in ('ui', 'race', 'map'):
        unique = {}
        for c in sorted(all_candidates[target], key=rank):
            ident = (c['source'], c['absolute_address'], tuple(v['value'] for v in c['values']))
            if ident in unique:
                unique[ident]['alternate_types'].append(c['type'])
            else:
                c['alternate_types'] = []
                unique[ident] = c
        collapsed = list(unique.values())
        for rank_index, c in enumerate(collapsed, 1):
            if target == 'ui' and c['source'] == REGISTRY_SOURCE and c['relative_offset'] in ('0xc04', '0xc08') and c['type'] in ('float', 'int32'):
                hypothesis_ranks.append({'type': c['type'], 'relative_offset': c['relative_offset'], 'score': c['score'], 'rank_in_target_before_truncation': rank_index})
        retained.extend(collapsed[:top])
    for target in all_candidates:
        for n, c in enumerate([c for c in retained if c['target'] == target], 1):
            c['rank_in_target'] = n
    return keys, retained, examined, counts, hypothesis_ranks

def registry_diagnostics(captures):
    offsets = [0x984, 0x988, 0xBE8, 0xC00, 0xC04, 0xC08, 0xC0C, 0xC88, 0xCA0, 0xCA4, 0xCA8, 0xCAC,
               0xD28, 0xD40, 0xD44, 0xD48, 0xD4C, 0xDE8, 0xE00, 0xE04, 0xE08, 0xE0C,
               0xE80, 0xE84, 0xE88, 0xE8C, 0xEA0, 0xEA4, 0xEA8, 0xEAC]
    values = []
    for offset in offsets:
        values.append({'relative_offset': hex(offset), 'source': REGISTRY_SOURCE,
                       'values': [{'file': c['file'], 'label': c['label'],
                                   'int32': read_value(c['blocks'][(c['registry_address'], REGISTRY_SOURCE)], offset, 'int32'),
                                   'float': read_value(c['blocks'][(c['registry_address'], REGISTRY_SOURCE)], offset, 'float')} for c in captures]})
    pair_ok = []
    for cap in captures:
        raw = cap['blocks'][(cap['registry_address'], REGISTRY_SOURCE)]
        a, b = read_value(raw, 0xC04, 'float'), read_value(raw, 0xC08, 'int32')
        expected = {'gameplay': (0.0, 0), 'pause': (1.0, 1), 'photo': (1.0, 2), 'map': (0.0, 0)}.get(ui_group(cap['label']))
        pair_ok.append({'file': cap['file'], 'label': cap['label'], 'known': cap['known'], 'observed': [a, b],
                        'expected_from_hypothesis': list(expected) if expected else None,
                        'matches_known_label': (a, b) == expected if expected else None,
                        'invalidates_zero_equals_gameplay': ui_group(cap['label']) == 'map' and (a, b) == (0.0, 0)})
    return values, pair_ok

def cluster_registry(captures):
    # Use all captured registry words that change, not a handpicked subset near the clue.
    raws = [c['blocks'][(c['registry_address'], REGISTRY_SOURCE)] for c in captures]
    length = min(map(len, raws))
    offsets = [i for i in range(0, length-3, 4) if len({raw[i:i+4] for raw in raws}) > 1]
    signatures = [[struct.unpack_from('<I', raw, off)[0] for off in offsets] for raw in raws]
    groups = collections.defaultdict(list)
    for i, sig in enumerate(signatures):
        groups[tuple(sig)].append(i)
    clusters = [{'cluster': f'R{n}', 'members': [captures[i]['label'] for i in indices],
                 'known_members': [captures[i]['label'] for i in indices if captures[i]['known']],
                 'signature_sha256': hashlib.sha256(struct.pack('<'+'I'*len(sig), *sig)).hexdigest()}
                for n, (sig, indices) in enumerate(groups.items(), 1)]
    matches = []
    known = [i for i, c in enumerate(captures) if c['known']]
    for i, cap in enumerate(captures):
        if cap['known']:
            continue
        distances = [(sum(a != b for a, b in zip(signatures[i], signatures[j])), j) for j in known]
        best = min(d for d, _ in distances)
        nearest = [j for d, j in distances if d == best]
        cluster = next(g['cluster'] for g in clusters if cap['label'] in g['members'])
        matches.append({'file': cap['file'], 'label': cap['label'], 'cluster': cluster,
                        'semantic_label': None, 'hamming_distance': best, 'signature_fields': len(offsets),
                        'nearest_known_labels': [captures[j]['label'] for j in nearest],
                        'differing_offsets_from_first_nearest': [hex(off) for off, a, b in zip(offsets, signatures[i], signatures[nearest[0]]) if a != b],
                        'reasoning': 'Signature resemblance only. Missing state labels, cached fields and unobserved modes preclude a semantic assignment.'})
    return {'method': 'Exact equality clusters; nearest known captures by equal-weight Hamming distance over every changing aligned uint32 registry word.',
            'changing_offsets': list(map(hex, offsets)), 'clusters': clusters, 'unlabeled_matches': matches}

def event_strings(captures):
    # Context-only inventory: byte strings never contribute to activation scores.
    terms = re.compile(r'race|event|route|roadtrip|freeroam|paused|photo|\bmap\b', re.I)
    entries = {}
    for cap in captures:
        for key, raw in cap['blocks'].items():
            for match in re.finditer(rb'[\x20-\x7e]{5,}', raw):
                text = match.group().decode('ascii')
                if not terms.search(text):
                    continue
                ident = (key, match.start(), text)
                entries.setdefault(ident, []).append(cap['label'])
    return [{'source': key[1], 'block_address': hex(key[0]), 'relative_offset': hex(off), 'text': text[:160],
             'present_in': labels, 'state_evidence': False}
            for (key, off, text), labels in sorted(entries.items(), key=lambda kv: (-len(kv[1]), kv[0][0], kv[0][1]))][:80]

def byte_diff_summary(captures, keys):
    result = []
    for key in keys:
        available = [(c, c['blocks'][key]) for c in captures if key in c['blocks']]
        pairs = []
        for (a, ar), (b, br) in itertools.combinations(available, 2):
            changed, ranges = changed_ranges(ar, br)
            pairs.append({'first': a['file'], 'second': b['file'], 'compared_bytes': min(len(ar), len(br)),
                          'changed_bytes': changed, 'first_only_tail': max(0, len(ar)-len(br)), 'second_only_tail': max(0, len(br)-len(ar)),
                          'changed_ranges': ranges})
        result.append({'source': key[1], 'address': hex(key[0]), 'present_in': [c['file'] for c, _ in available],
                       'missing_from': [c['file'] for c in captures if key not in c['blocks']], 'pairs': pairs})
    return result

def production_evidence(captures, hypothesis):
    counts = collections.Counter(ui_group(c['label']) for c in captures if c['known'])
    matching = all(h['matches_known_label'] is True for h in hypothesis if h['known'])
    guards = []
    for c in captures:
        raw = c['blocks'][(c['registry_address'], REGISTRY_SOURCE)]
        guards.append(read_value(raw, 0xC00, 'int32') == 0x3f800000 and read_value(raw, 0xC0C, 'int32') == 2 and raw[0xC10:0xC1C] == b'BASE_Paused\0')
    supported = matching and all(guards) and counts['pause'] >= 2 and counts['photo'] >= 2 and counts['map'] >= 1 and counts['gameplay'] >= 3
    map_checks = []
    for c in captures:
        raw = c['blocks'][(c['registry_address'], REGISTRY_SOURCE)]
        observed = [read_value(raw, 0xE84, 'float'), read_value(raw, 0xE88, 'int32')]
        group = ui_group(c['label'])
        expected = [1.0, 1] if group == 'map' else [0.0, 0] if group is not None else None
        layout = read_value(raw, 0xE80, 'int32') == 0x3f800000 and read_value(raw, 0xE8C, 'int32') == 2 and raw[0xE90:0xEA0] == b'BASE_Map_Paused\0'
        map_checks.append({'label':c['label'], 'file':c['file'], 'known':c['known'], 'observed':observed,
                           'expected_from_label':expected, 'matches_known_label':observed == expected if expected else None,
                           'layout_guard_valid':layout, 'semantic_label_for_unlabeled':None})
    map_supported = supported and counts['map'] >= 2 and all(m['layout_guard_valid'] for m in map_checks) and all(m['matches_known_label'] for m in map_checks if m['known'])
    return {'implement_v57': supported, 'implement_v58':map_supported, 'version': '5.8' if map_supported else '5.7' if supported else '5.6',
            'supported_positive_ui_modes': (['PauseMenu', 'PhotoMode']+(['WorldMap'] if map_supported else [])) if supported else [],
            'known_group_counts': dict(counts), 'record_guards_match_all_captures': all(guards),
            'zero_signature': 'Pause pair (0,0) aliases Map and driving; map pair disambiguates positive Map. Both pairs inactive remains Unknown.',
            'map_checks':map_checks,
            'map_field_score':field_evidence(captures,(captures[0]['registry_address'],REGISTRY_SOURCE),0xE84,'float','map')['score'] if map_supported else None,
            'unknown_game_mode': True, 'accepted_new_offsets': ([
                {'source':'AudioEngine', 'offset':'0x630', 'type':'pointer64', 'purpose':'Validated registry traversal; no absolute heap address'},
                {'source':REGISTRY_SOURCE, 'offset':'0xC04', 'type':'float32', 'purpose':'Require exact 1.0 before positive Pause/Photo decode'},
                {'source':REGISTRY_SOURCE, 'offset':'0xC08', 'type':'int32', 'purpose':'1=observed PauseMenu, 2=observed PhotoMode; all other values Unknown'},
                {'source':REGISTRY_SOURCE, 'offset':'0xC00', 'type':'float32', 'purpose':'Layout guard exact 1.0'},
                {'source':REGISTRY_SOURCE, 'offset':'0xC0C', 'type':'int32', 'purpose':'Layout guard exact 2'},
                {'source':REGISTRY_SOURCE, 'offset':'0xC10', 'type':'12 bytes', 'purpose':'Layout guard BASE_Paused followed by NUL; NEVER activation evidence'}] if supported else []) + ([
                {'source':REGISTRY_SOURCE, 'offset':'0xE80', 'type':'float32', 'purpose':'Map record layout guard exact 1.0'},
                {'source':REGISTRY_SOURCE, 'offset':'0xE84', 'type':'float32', 'purpose':'Map activity signal exact 1.0'},
                {'source':REGISTRY_SOURCE, 'offset':'0xE88', 'type':'int32', 'purpose':'Map activity subtype exact 1'},
                {'source':REGISTRY_SOURCE, 'offset':'0xE8C', 'type':'int32', 'purpose':'Map record layout guard exact 2'},
                {'source':REGISTRY_SOURCE, 'offset':'0xE90', 'type':'16 bytes', 'purpose':'Layout guard BASE_Map_Paused followed by NUL; NEVER activation evidence'}] if map_supported else []),
            'limitations':['Only observed positive signatures accepted, not a complete UI state machine.',
                           'Map has two labeled captures, but no separately labeled Map_Paused versus Map transitions.',
                           'No loading/video/menu coverage and no restarted/relocated live capture; guarded failure returns Unknown.',
                           'Pause/Photo naming is empirical from human labels, not causally proven engine field semantics.']}

def write_report(report, output):
    output.mkdir(parents=True, exist_ok=True)
    (output/'state-report.json').write_text(json.dumps(report, indent=2, allow_nan=False), encoding='utf-8')
    fields = ['target','rank_in_target','source','block_address','absolute_address','relative_offset','type','score','repeatability','separation','production_accepted','reasoning']
    with (output/'top-candidates.csv').open('w',newline='',encoding='utf-8') as stream:
        labels=[c['label'] for c in report['captures']]
        writer=csv.DictWriter(stream,fieldnames=fields+labels); writer.writeheader()
        for c in report['candidates']:
            row={f:c[f] for f in fields}; row.update({v['label']:v['value'] for v in c['values']}); writer.writerow(row)
    known=sum(c['known'] for c in report['captures'])
    prod=report['production']
    lines=['# FH2 state research — repeated World Map controls','',
      f"Analyzed **{len(report['captures'])}** captures locally: **{known} labeled**, five intentionally unlabeled. Corrected human mapping: **155811-488 = World Map; 155825-653 = driving**. The initial attachment-order interpretation was reversed by the user, not by automatic inference.",'',
      '**v5.8 adds WorldMap to the existing PauseMenu / PhotoMode reader. Ordinary gameplay still returns Unknown.**','',
      f"There are {report['aligned_keys']} exact Address+Source keys, {report['fully_matched_keys']} present in every capture; {report['interpretations_examined']:,} aligned numeric/bit interpretations were screened. Full dumps were parsed only by local code.",'',
      '## Repeated numeric evidence','',
      '| Label | Pause float C04 / enum C08 | Map float E84 / enum E88 | Human-labeled group |',
      '|---|---|---|---|']
    for h,m in zip(report['hypothesis_checks'],prod['map_checks']):
        group=ui_group(h['label']) or 'UNLABELED — no semantic assignment'
        lines.append(f"| {h['label']} | {h['observed']} | {m['observed']} | {group} |")
    lines += ['',
      'Both known World Map samples have float +0xE84=1.0 and int32 +0xE88=1 in the registry reached through AudioEngine+0x630. Every other known sample (11 controls: driving/event, stopped roadtrip, Pause Menu and Photo Mode) has (0,0) there. The pause pair remains (0,0) in Map and driving, so it cannot identify gameplay by itself.','',
      'The new Map/drive pair changes eight aligned registry words. The Map record signal repeats across two capture periods and differs from stationary gameplay as well as pause/photo. Map samples have approximately 54.10 and 82.61 MPH; other labeled UI/gameplay captures cover the same and wider speed range. The result is not a simple speed threshold. Discovery and checking use the same small dataset; these are empirical correlations, not causal proof or a blind held-out evaluation.','',
      'Nearby UI_AMB +0x984/+0x988 has the same observed partition, but remains exploratory; it is not an additional production offset. BASE_Map_Paused exists in every capture, including gameplay, so the string alone never establishes an active state.','',
      '## Minimal runtime rule','',
      '- Traverse dynamic FH2 image base + existing RVA 0x0365D3D8 → AudioEngine → raw pointer +0x630. No heap address is hard-coded.',
      '- Validate Pause record C00..C1B and Map record E80..E9F: exact header float 1.0, int32 guard 2, and the respective NUL-terminated name as a layout guard only.',
      '- Pause (1,1) or Photo (1,2) requires Map inactive (0,0). WorldMap requires Pause inactive (0,0) and Map positive (1,1).',
      '- Both inactive, unexpected values, conflicting positive records, partial reads, unsupported PE layout, changed pointers or two differing record reads return Unknown.',
      '- Neither zero nor a present state name proves normal gameplay. No race, event/route ID or Road Trip mode is claimed.','',
      'Every relevant record is read twice and both pointers are rechecked. This reduces inconsistent reads but does not make the remote snapshot atomic. The existing PE-layout gate remains. ASCII is a structural check and context only; numeric activity/subtype fields drive the result.','',
      '## Ranked candidates','',
      'Rankings have no supplied-clue bonus. Known-label repeatability (40), separation (30), bounded enum shape (10), raw pointer-path checks (10), numeric motion/time control penalties (10) are used. Singleton groups multiply scores by 0.8; plausible float fragments lose 12 and pointer fragments 18. Event scores receive a further 0.75 factor capped at 60. Scores are not probabilities. Equal-value alternate types at one address are collapsed.','',
      '| Target | Source | Offset | Type | Score | Repeatability | Separation |',
      '|---|---|---|---|---:|---:|---:|']
    for target in ('map','ui','race'):
        for c in [c for c in report['candidates'] if c['target']==target][:8]:
            lines.append(f"| {target} | {c['source']} | {c['relative_offset']} | {c['type']} | {c['score']} | {c['repeatability']:.3f} | {c['separation']:.3f} |")
    lines += ['',f"Map +0xE84 float receives the same unboosted score rule: {prod['map_field_score']}.",'',
      'All top candidate values/addresses/types and reasoning are in JSON and CSV. Complete matching block-pair differences are in byte-diffs.json. Per-field guards and accepted production offsets are listed separately; general ranking does not grant production acceptance.','',
      '## Unlabeled captures','',
      'All five labels remain UNLABELED. Runtime signature predictions, if any, are not ground truth; nearest-signature comparisons do not silently rename them.','',
      '| Unlabeled | Cluster | Nearest known labels | Differing words | Assigned semantic label |',
      '|---|---|---|---:|---|']
    for m in report['clustering']['unlabeled_matches']:
        lines.append(f"| {m['label']} | {m['cluster']} | {', '.join(m['nearest_known_labels'])} | {m['hamming_distance']} | none |")
    lines += ['',
      '## Validation and remaining limits','',
      'The user confirmed the earlier PauseMenu/PhotoMode reader works live, and that Map/driving were Unknown. The new WorldMap branch has offline fixture and failure-path tests but awaits live confirmation. No live app is launched by analysis/build validation. Existing always-on-top, speed units, dynamic PE mapping, Discord IPC/formatting, tray/startup and F8 capture are preserved.','',
      'All captures still use one observed heap layout. Root-image-slot bytes are absent from dump graphs and remain source/user evidence from v5.6. Relocation and root-slot tests are explicitly synthetic. Loading/video/other menus and Map transition variants are not covered. More separately labeled controls are needed before normal gameplay, event or any other mode is promoted.','',
      'See validation.md/json, test-results.txt, ui-reader-test-results.txt and build-results.txt. Input SHA-256 values and raw pointer-path checks are in JSON. Corrected labels and live confirmation provenance are in map-pair-label-provenance.json.','']
    (output/'state-report.md').write_text('\n'.join(lines),encoding='utf-8')

def analyze(root, output, top=60, diffs=True):
    captures, rows = load_inputs(root)
    if any(c['csv_mismatches'] for c in captures):
        raise ValueError('Compact CSV disagrees with raw dump values')
    keys, candidates, examined, counts, hypothesis_ranks = scan(captures, top)
    registry, hypothesis = registry_diagnostics(captures)
    report = {'schema_version': 1, 'analysis': 'local programmatic raw-byte analysis; no semantic assignment to unlabeled captures',
              'captures': [{k: v for k, v in c.items() if k not in ['blocks', 'seconds']} for c in captures],
              'aligned_keys': len(keys), 'fully_matched_keys': sum(all(k in c['blocks'] for c in captures) for k in keys),
              'interpretations_examined': examined, 'candidate_counts_before_truncation': counts,
              'top_per_target': top, 'candidates': candidates, 'registry_diagnostics': registry,
              'hypothesis_discovery_ranks': hypothesis_ranks,
              'hypothesis_checks': hypothesis, 'clustering': cluster_registry(captures),
              'context_only_string_inventory': event_strings(captures),
              'production': production_evidence(captures, hypothesis),
              'scope_limits': ['Natural alignment, numeric bounded enum/flag candidates only; absence from ranking does not prove absence of state.',
                               'Source+absolute block address exact alignment; relocated blocks are not guessed.',
                               'Only blocks complete in all known captures enter supervised ranking; missing coverage is reported in byte diffs.',
                               'Unlabeled signatures never supply semantic labels or improve repeatability counts.',
                               'No live process, Discord IPC, startup registry, UI, or game interaction was performed.']}
    write_report(report, output)
    if diffs:
        (output/'byte-diffs.json').write_text(json.dumps(byte_diff_summary(captures, keys), indent=2), encoding='utf-8')
    return report

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=pathlib.Path, required=True)
    parser.add_argument('--output', type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[2]/'research'/'generated')
    parser.add_argument('--top', type=int, default=60)
    parser.add_argument('--no-diffs', action='store_true')
    args = parser.parse_args()
    if args.top < 1:
        parser.error('--top must be positive')
    report = analyze(args.input, args.output, args.top, not args.no_diffs)
    print(json.dumps({'captures': len(report['captures']), 'aligned_keys': report['aligned_keys'],
                      'examined': report['interpretations_examined'], 'candidate_counts': report['candidate_counts_before_truncation'],
                      'known_hypothesis_matches': sum(c['matches_known_label'] is True for c in report['hypothesis_checks']),
                      'clusters': report['clustering']['clusters'],
                      'nearest': report['clustering']['unlabeled_matches'], 'production': report['production']}, indent=2))
