
_script = jedi.Script(__code)
_result = None
_items = _script.help(__line, __column)

if not _items:
    _items = _script.infer(__line, __column)

def _best_inferred_doc(_item):
    try:
        _inferred = _item.infer() if hasattr(_item, 'infer') else []
    except Exception:
        _inferred = []
    for _inferred_item in _inferred:
        _name = getattr(_inferred_item, 'name', '') or ''
        try:
            _doc = _inferred_item.docstring(raw=True) if hasattr(_inferred_item, 'docstring') else ''
        except Exception:
            _doc = ''
        if _doc and (_name in _stub_class_names or not _doc.startswith(_name + ':')):
            return _doc
    return ''

def _doc_blocks(_doc):
    if not _doc:
        return []
    try:
        import docstring_parser as _docstring_parser
        _parsed = _docstring_parser.parse(_doc)
        _blocks = []
        if _parsed.short_description:
            _blocks.append({'kind': 'paragraph', 'text': _parsed.short_description})
        if _parsed.long_description:
            _blocks.append({'kind': 'paragraph', 'text': _parsed.long_description})
        _params = []
        for _param in getattr(_parsed, 'params', []) or []:
            _name = getattr(_param, 'arg_name', '') or ''
            _type_name = getattr(_param, 'type_name', '') or ''
            _description = getattr(_param, 'description', '') or ''
            if _name or _description:
                _label = _name if not _type_name else f'{_name} : {_type_name}'
                _params.append({'label': _label, 'text': _description})
        if _params:
            _blocks.append({'kind': 'section', 'title': 'Parameters'})
            _blocks.append({'kind': 'list', 'items': _params})
        _returns = getattr(_parsed, 'returns', None)
        if _returns:
            _return_type = getattr(_returns, 'type_name', '') or ''
            _return_description = getattr(_returns, 'description', '') or ''
            _return_text = _return_description if not _return_type else f'{_return_type}. {_return_description}'.strip()
            if _return_text:
                _blocks.append({'kind': 'section', 'title': 'Returns'})
                _blocks.append({'kind': 'paragraph', 'text': _return_text})
        _raises = []
        for _raise in getattr(_parsed, 'raises', []) or []:
            _type_name = getattr(_raise, 'type_name', '') or ''
            _description = getattr(_raise, 'description', '') or ''
            if _type_name or _description:
                _raises.append({'label': _type_name, 'text': _description})
        if _raises:
            _blocks.append({'kind': 'section', 'title': 'Raises'})
            _blocks.append({'kind': 'list', 'items': _raises})
        for _example in getattr(_parsed, 'examples', []) or []:
            _description = getattr(_example, 'description', '') or ''
            _snippet = getattr(_example, 'snippet', '') or ''
            if _description:
                _blocks.append({'kind': 'paragraph', 'text': _description})
            if _snippet:
                _blocks.append({'kind': 'code', 'text': _snippet})
        return _blocks if _blocks else [{'kind': 'raw', 'text': _doc}]
    except Exception:
        return [{'kind': 'raw', 'text': _doc}]

if _items:
    _item = _items[0]
    _type = getattr(_item, 'type', '')
    _description = getattr(_item, 'description', '')
    _attribute_signature = '' if _type == 'keyword' else _stub_attribute_signature(_item)
    _variable_definition = '' if _attribute_signature or _type == 'keyword' else _variable_signature(_item)
    _doc = '' if _type == 'keyword' else (
        _variable_doc(_item) if _variable_definition else (_item.docstring(raw=True) if hasattr(_item, 'docstring') else ''))
    _stub_doc = '' if _type == 'keyword' else (_stub_attribute_doc(_item) or _stub_method_doc(_item))
    if _stub_doc:
        _doc = _stub_doc
    elif not _variable_definition and _type != 'keyword' and (not _doc or _type in ('statement', 'instance', 'param')):
        _inferred_doc = _best_inferred_doc(_item)
        if _inferred_doc:
            _doc = _inferred_doc
    _signature = _attribute_signature or _variable_definition
    if _type in ('function', 'method', 'class'):
        try:
            _signatures = _item.get_signatures() if hasattr(_item, 'get_signatures') else []
            if _signatures:
                _signature = _signatures[0].to_string()
        except Exception:
            _signature = ''
    _result = {
        'type': _type,
        'description': _description,
        'signature': _signature,
        'docstring': _doc,
        'docblocks': _doc_blocks(_doc)
    }

import json as _json
_result_json = _json.dumps(_result)
