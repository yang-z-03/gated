
_script = jedi.Script(__code)
_result = []

for _item in _script.complete(__line, __column):
    _name = getattr(_item, 'name', '')
    _type = getattr(_item, 'type', '')
    if not _name or _name.startswith('_') or _type in ('path', 'file'):
        continue
    _attribute_signature = '' if _type == 'keyword' else _stub_attribute_signature(_item)
    _variable_definition = '' if _attribute_signature or _type == 'keyword' else _variable_signature(_item)
    _signature = _attribute_signature or _variable_definition
    if _type in ('function', 'method', 'class'):
        try:
            _signatures = _item.get_signatures() if hasattr(_item, 'get_signatures') else []
            if _signatures:
                _signature = _signatures[0].to_string()
        except Exception:
            _signature = ''
    _doc = '' if _type == 'keyword' else (
        _stub_attribute_doc(_item)
        or _stub_method_doc(_item)
        or (_variable_doc(_item) if _variable_definition
            else (_item.docstring(raw=True) if hasattr(_item, 'docstring') else '')))
    _result.append({
        'name': _name,
        'complete': getattr(_item, 'complete', ''),
        'type': _type,
        'description': getattr(_item, 'description', ''),
        'signature': _signature,
        'docstring': _doc
    })
    
import json as _json
_result_json = _json.dumps(_result)
