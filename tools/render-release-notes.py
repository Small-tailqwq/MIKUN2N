#!/usr/bin/env python3
"""Render bilingual Release notes without building or changing package assets."""

import argparse
import json
from pathlib import Path
import re
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', required=True)
    parser.add_argument('--repository', required=True, help='GitHub owner/repository')
    parser.add_argument('--source-ref', help='Exact packaged source commit; defaults to the current checkout')
    parser.add_argument('--run-url', required=True)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if not re.fullmatch(r'\d+\.\d+\.\d+-\d+', args.version):
        parser.error('version must be the canonical release version')
    if not re.fullmatch(r'[\w.-]+/[\w.-]+', args.repository):
        parser.error('repository must be owner/name')
    source = args.source_ref or subprocess.check_output(
        ['git', 'rev-parse', 'HEAD'], text=True).strip()
    root = Path(__file__).resolve().parents[1]
    change_path = root / '.github' / 'release-notes' / f'{args.version}.json'
    changes = json.loads(change_path.read_text(encoding='utf-8')) if change_path.exists() else {}
    values = {
        'VERSION': args.version,
        'SOURCE_URL': f'https://github.com/{args.repository}/tree/{source}',
        'DOC_URL': f'https://github.com/{args.repository}/blob/{source}',
        'BUILD_URL': args.run_url,
        'CHANGES_ZH': '\n'.join('- ' + line for line in changes.get('changes_zh',
                                  ['本版本的改动见对应源码。'])),
        'CHANGES_EN': '\n'.join('- ' + line for line in changes.get('changes_en',
                                  ['See the corresponding source for this version.'])),
        'VERSION_NOTE_ZH': changes.get('version_note_zh', ''),
        'VERSION_NOTE_EN': changes.get('version_note_en', ''),
    }
    template = (root / '.github' / 'release-notes-template.md').read_text(encoding='utf-8')
    for key, value in values.items():
        template = template.replace('{{' + key + '}}', value)
    if re.search(r'\{\{[A-Z_]+\}\}', template):
        raise ValueError('unresolved release-note placeholder')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(template.encode('utf-8'))
    print(f'Wrote bilingual release notes: {args.output}')


if __name__ == '__main__':
    main()
