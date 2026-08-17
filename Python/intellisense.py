import ast as _ast
import re as _re


def _clean_attribute_description(_lines):
    _paragraphs = []
    _paragraph = []
    for _line in _lines:
        _text = _line.strip()
        if _text:
            _paragraph.append(_text)
        elif _paragraph:
            _paragraphs.append(' '.join(_paragraph))
            _paragraph = []
    if _paragraph:
        _paragraphs.append(' '.join(_paragraph))
    return '\n\n'.join(_paragraphs)


def _attribute_docs(_doc):
    _result = {}
    _lines = (_doc or '').splitlines()
    _start = -1
    for _index in range(len(_lines) - 1):
        if (_lines[_index].strip() == 'Attributes'
                and _lines[_index + 1].strip()
                and set(_lines[_index + 1].strip()) == {'='}):
            _start = _index + 2
            break
    if _start < 0:
        return _result

    _name = ''
    _type_name = ''
    _description = []

    def _store():
        if not _name:
            return
        _result[_name] = _clean_attribute_description(_description)

    _index = _start
    while _index < len(_lines):
        _line = _lines[_index]
        if (_line and not _line[0].isspace() and _index + 1 < len(_lines)
                and _lines[_index + 1].strip()
                and set(_lines[_index + 1].strip()) == {'='}):
            break
        _match = _re.match(r'^([A-Za-z_]\w*)\s*:\s*(.+?)\s*$', _line)
        if _match:
            _store()
            _name = _match.group(1)
            _type_name = _match.group(2)
            _description = []
        elif _name:
            _description.append(_line)
        _index += 1
    _store()
    return _result


_stub_attribute_docs = {}
_stub_attribute_signatures = {}
_stub_method_docs = {}
_stub_class_names = set()
_elementary_class_names = {
    'NoneType', 'bool', 'int', 'float', 'complex', 'str', 'bytes', 'bytearray',
    'list', 'tuple', 'dict', 'set', 'frozenset', 'range', 'memoryview'
}
try:
    _stub_tree = _ast.parse(__stub_code)
    for _node in _stub_tree.body:
        if not isinstance(_node, _ast.ClassDef):
            continue
        _stub_class_names.add(_node.name)
        for _attribute_name, _doc in _attribute_docs(_ast.get_docstring(_node, clean=True)).items():
            _stub_attribute_docs[(_node.name, _attribute_name)] = _doc
        for _member in _node.body:
            if isinstance(_member, _ast.AnnAssign) and isinstance(_member.target, _ast.Name):
                try:
                    _annotation = _ast.unparse(_member.annotation)
                except Exception:
                    _annotation = ''
                if _annotation:
                    _stub_attribute_signatures[(_node.name, _member.target.id)] = f'{_member.target.id}: {_annotation}'
            if isinstance(_member, (_ast.FunctionDef, _ast.AsyncFunctionDef)):
                _doc = _ast.get_docstring(_member, clean=True) or ''
                if _doc:
                    _stub_method_docs[(_node.name, _member.name)] = _doc
except Exception:
    pass


def _definition_class(_item):
    try:
        _position = _item.get_definition_start_position()
    except Exception:
        return ''
    if not _position:
        return ''
    _line = _position[0]
    _lines = __code.splitlines()
    _index = min(max(_line - 1, 0), len(_lines) - 1)
    while _index >= 0:
        _text = _lines[_index]
        if _text.startswith('class '):
            return _text.split('class ', 1)[1].split(':', 1)[0].split('(', 1)[0].strip()
        _index -= 1
    return ''


def _stub_attribute_doc(_item):
    _name = getattr(_item, 'name', '') or ''
    _class_name = _definition_class(_item) if _name else ''
    return _stub_attribute_docs.get((_class_name, _name), '')


def _stub_attribute_signature(_item):
    _name = getattr(_item, 'name', '') or ''
    _class_name = _definition_class(_item) if _name else ''
    return _stub_attribute_signatures.get((_class_name, _name), '')


def _stub_method_doc(_item):
    _name = getattr(_item, 'name', '') or ''
    _class_name = _definition_class(_item) if _name else ''
    return _stub_method_docs.get((_class_name, _name), '')


def _variable_signature(_item):
    if getattr(_item, 'type', '') not in ('statement', 'param'):
        return ''
    _name = getattr(_item, 'name', '') or ''
    _description = getattr(_item, 'description', '') or ''
    if _name and (_description == _name
            or _description.startswith(_name + ':')
            or _description.startswith(_name + ' =')):
        return _description
    return _name


def _inferred_class_doc(_item):
    try:
        _inferred = _item.infer() if hasattr(_item, 'infer') else []
    except Exception:
        _inferred = []
    for _inferred_item in _inferred:
        _name = getattr(_inferred_item, 'name', '') or ''
        _full_name = getattr(_inferred_item, 'full_name', '') or ''
        _type = getattr(_inferred_item, 'type', '') or ''
        if _type not in ('instance', 'class') or _name in _elementary_class_names:
            continue
        if _full_name.startswith('builtins.') and _name in _elementary_class_names:
            continue
        try:
            _doc = _inferred_item.docstring(raw=True) if hasattr(_inferred_item, 'docstring') else ''
        except Exception:
            _doc = ''
        if _doc:
            return _doc
    return ''


def _variable_doc(_item):
    try:
        _own_doc = _item.docstring(raw=True) if hasattr(_item, 'docstring') else ''
    except Exception:
        _own_doc = ''
    _class_doc = _inferred_class_doc(_item)
    if _own_doc and _class_doc and _own_doc.strip() != _class_doc.strip():
        return f'{_own_doc.strip()}\n\n{_class_doc.strip()}'
    return _own_doc or _class_doc
