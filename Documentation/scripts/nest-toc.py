#!/usr/bin/env python3
"""
Post-processes the DocFX-generated toc.yml to nest child types under their parents.

For example, transforms:
  - WorldRunner
  - WorldRunner.DebugDataType
  - WorldRunner.NetFunctionCtx

Into:
  - WorldRunner
    - DebugDataType
    - NetFunctionCtx
"""

import sys
import re
from pathlib import Path


def parse_toc(content: str) -> list:
    """Simple YAML-like parser for DocFX toc.yml format."""
    lines = content.split('\n')
    result = []
    current_namespace = None
    current_item = None
    i = 0
    
    while i < len(lines):
        line = lines[i]
        stripped = line.strip()
        
        # Skip empty lines and header
        if not stripped or stripped.startswith('###'):
            i += 1
            continue
        
        # Check indentation level
        indent = len(line) - len(line.lstrip())
        
        # Top-level namespace (items:)
        if stripped == 'items:' and indent == 0:
            i += 1
            continue
        
        # Namespace entry (- uid: Nebula.Something with type: Namespace)
        if stripped.startswith('- uid:') and indent == 0:
            if current_namespace:
                result.append(current_namespace)
            current_namespace = {
                'uid': stripped.replace('- uid:', '').strip(),
                'name': None,
                'type': None,
                'items': []
            }
            current_item = None
        elif stripped.startswith('name:') and indent == 2 and current_namespace and current_namespace['name'] is None:
            current_namespace['name'] = stripped.replace('name:', '').strip()
        elif stripped.startswith('type:') and indent == 2 and current_namespace and current_namespace['type'] is None:
            current_namespace['type'] = stripped.replace('type:', '').strip()
        elif stripped == 'items:' and indent == 2:
            pass  # Start of namespace items
        elif stripped.startswith('- uid:') and indent == 2:
            # Item within namespace
            current_item = {
                'uid': stripped.replace('- uid:', '').strip(),
                'name': None,
                'type': None
            }
            current_namespace['items'].append(current_item)
        elif stripped.startswith('name:') and indent == 4 and current_item:
            current_item['name'] = stripped.replace('name:', '').strip()
        elif stripped.startswith('type:') and indent == 4 and current_item:
            current_item['type'] = stripped.replace('type:', '').strip()
        
        i += 1
    
    if current_namespace:
        result.append(current_namespace)
    
    return result


def nest_items(namespaces: list) -> list:
    """Nest child types under their parent types."""
    for ns in namespaces:
        items = ns.get('items', [])
        if not items:
            continue
        
        # Build a map of parent types
        parent_map = {}  # name -> item with children
        nested_names = set()  # track which items are nested
        
        # First pass: identify parents and children
        for item in items:
            name = item.get('name', '')
            if '.' in name:
                parent_name = name.rsplit('.', 1)[0]
                child_name = name.rsplit('.', 1)[1]
                
                # Find parent
                for potential_parent in items:
                    if potential_parent.get('name') == parent_name:
                        if 'children' not in potential_parent:
                            potential_parent['children'] = []
                        potential_parent['children'].append({
                            'uid': item['uid'],
                            'name': child_name,  # Use short name
                            'type': item['type']
                        })
                        nested_names.add(name)
                        break
        
        # Second pass: filter out nested items from top level
        ns['items'] = [item for item in items if item.get('name') not in nested_names]
    
    return namespaces


def write_toc(namespaces: list) -> str:
    """Write the nested structure back to YAML format."""
    lines = ['### YamlMime:TableOfContent', 'items:']
    
    def write_item(item, indent=0):
        prefix = '  ' * indent
        lines.append(f'{prefix}- uid: {item["uid"]}')
        lines.append(f'{prefix}  name: {item["name"]}')
        lines.append(f'{prefix}  type: {item["type"]}')
        if 'children' in item and item['children']:
            lines.append(f'{prefix}  items:')
            for child in item['children']:
                write_item(child, indent + 1)
    
    for ns in namespaces:
        lines.append(f'- uid: {ns["uid"]}')
        lines.append(f'  name: {ns["name"]}')
        lines.append(f'  type: {ns["type"]}')
        if ns.get('items'):
            lines.append('  items:')
            for item in ns['items']:
                write_item(item, indent=1)
    
    return '\n'.join(lines) + '\n'


def main():
    toc_path = Path(__file__).parent.parent / 'api' / 'toc.yml'
    
    if not toc_path.exists():
        print(f"Error: {toc_path} not found")
        sys.exit(1)
    
    print(f"Processing {toc_path}...")
    
    content = toc_path.read_text()
    namespaces = parse_toc(content)
    nested = nest_items(namespaces)
    output = write_toc(nested)
    
    toc_path.write_text(output)
    print(f"TOC restructured successfully!")


if __name__ == '__main__':
    main()
