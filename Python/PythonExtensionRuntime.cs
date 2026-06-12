using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using gated.Models;
using Python.Runtime;

namespace gated.Python;

public static class PythonExtensionRuntime
{
    private static readonly object gate = new();
    // PythonNet is initialized and used only on caller_thread. The language server
    // owns its request queue, then marshals Python/Jedi work to caller_thread.
    private static readonly PythonInteropThread caller_thread = new("Gated Python caller", owns_python: true);
    private static readonly PythonInteropThread language_thread = new("Gated Python language server", owns_python: false);
    private static bool engine_initialized;
    private static bool initialized;
    private static PyObject? jedi_module;
    private static PyObject? numpy_module;
    private static PyObject? pandas_module;
    private static Task? startup_task;
    public static event Action<string>? LogReceived;

    public static void StartBackground()
    {
        lock (gate)
        {
            startup_task ??= Task.Run(() =>
            {
                try
                {
                    caller_thread.Invoke(() => { });
                    language_thread.Invoke(() => caller_thread.InvokeWithGil(preload_jedi_modules));
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"Python runtime startup failed: {exception.Message}");
                }
            });
        }
    }

    public static void EnsureInitialized()
    {
        if (!caller_thread.IsCurrentThread)
        {
            caller_thread.Invoke(EnsureInitialized);
            return;
        }

        ensure_python_engine_initialized_on_owner_thread();
        using (Py.GIL())
            ensure_python_modules_initialized_on_owner_thread();
    }

    private static void ensure_python_engine_initialized_on_owner_thread()
    {
        lock (gate)
        {
            if (engine_initialized)
                return;

            Runtime.PythonDLL = Path.Combine(AppContext.BaseDirectory, "python", "python313.dll");
            PythonEngine.Initialize();
            engine_initialized = true;
        }
    }

    private static void ensure_python_modules_initialized_on_owner_thread()
    {
        lock (gate)
        {
            if (initialized)
                return;

            jedi_module = Py.Import("jedi");
            numpy_module = Py.Import("numpy");
            pandas_module = Py.Import("pandas");
            preload_jedi_modules();
            initialized = true;
        }
    }

    public static void Shutdown()
    {
        lock (gate)
        {
            if (!engine_initialized)
            {
                startup_task = null;
                language_thread.Dispose();
                caller_thread.Dispose();
                return;
            }
        }

        try
        {
            caller_thread.InvokePythonShutdown(() =>
            {
                lock (gate)
                {
                    if (initialized)
                        dispose_python_modules();
                    initialized = false;
                    startup_task = null;
                }
            });
        }
        finally
        {
            language_thread.Dispose();
            caller_thread.Dispose();
        }
    }

    public static void Execute(string code, Workspace workspace)
    {
        if (string.IsNullOrWhiteSpace(code))
            return;

        caller_thread.InvokeWithGil(() =>
        {
            using var globals = new PyDict();
            globals.SetItem("__builtins__", Py.Import("builtins"));
            globals.SetItem("workspace", workspace.ToPython());
            globals.SetItem("np", (numpy_module ?? Py.Import("numpy")));
            globals.SetItem("pd", (pandas_module ?? Py.Import("pandas")));
            globals.SetItem("msgbox", new Action<string, string>(MsgBox).ToPython());
            globals.SetItem("log", new Action<object?>(Log).ToPython());
            dynamic builtins = Py.Import("builtins");
            builtins.exec(code, globals, globals);
        });
    }

    public static double CalculateStatistic(gated.Models.StatisticDefinition definition, float[,] matrix, string[] channels)
    {
        if (definition.Kind != StatisticKind.Python || string.IsNullOrWhiteSpace(definition.PythonSource))
            return double.NaN;

        return caller_thread.InvokeWithGil(() =>
        {
            using var globals = new PyDict();
            globals.SetItem("__builtins__", Py.Import("builtins"));
            globals.SetItem("np", (numpy_module ?? Py.Import("numpy")));
            globals.SetItem("pd", (pandas_module ?? Py.Import("pandas")));
            dynamic builtins = Py.Import("builtins");
            builtins.exec(definition.PythonSource, globals, globals);

            using PyObject callable = globals.GetItem(definition.PythonCallableName);
            if (callable.IsNone() || !callable.HasAttr("__call__"))
                throw new InvalidOperationException($"Python statistic callable '{definition.PythonCallableName}' was not found.");

            using PyObject py_matrix = PythonArrayConverter.ToNumpy(matrix);
            using PyObject py_channels = string_list(channels);
            using PyObject py_parameters = PythonArrayConverter.ToNumpy(definition.PythonParameters);
            using PyObject result = callable.Invoke(py_matrix, py_channels, py_parameters);
            return scalar_result(result);
        });
    }

    public static void ValidateStatisticSource(string source, string callable_name)
    {
        caller_thread.InvokeWithGil(() =>
        {
            using var globals = new PyDict();
            globals.SetItem("__builtins__", Py.Import("builtins"));
            globals.SetItem("np", (numpy_module ?? Py.Import("numpy")));
            globals.SetItem("pd", (pandas_module ?? Py.Import("pandas")));
            dynamic builtins = Py.Import("builtins");
            builtins.exec(source, globals, globals);
            using PyObject callable = globals.GetItem(callable_name);
            if (callable.IsNone() || !callable.HasAttr("__call__"))
                throw new InvalidOperationException($"Python statistic callable '{callable_name}' was not found.");
        });
    }

    public static IReadOnlyList<PythonCompletionItem> CompletePython(string code, int line, int column)
    {
        try
        {
            return language_thread.Invoke(() => caller_thread.InvokeWithGil(() =>
            {
                using var globals = jedi_globals(code, line, column);
                dynamic builtins = Py.Import("builtins");
                builtins.exec(@"
_script = jedi.Script(__code)
_result = []
_api_attribute_docs = {
    'Workspace.metadata': 'metadata: pd.DataFrame\n\nA typed pandas DataFrame containing one row per sample. Group and Sample are read-only identity columns; all other columns are sample metadata. This is a copy; use apply_metadata(dataframe) to replace workspace metadata with the edited table.',
    'Workspace.groupings': 'groupings: list[Grouping]\n\nThe grouping collection in the current workspace. Use workspace[group_name] to retrieve one grouping by name.',
    'Grouping.name': 'name: str\n\nThe grouping name.',
    'Grouping.samples': 'samples: list[Sample]\n\nSamples contained in this grouping.',
    'Grouping.strategies': 'strategies: Strategy\n\nThe root strategy node for grouping-level gates and statistics.',
    'Grouping.compensations': 'compensations: dict[str, Compensation]\n\nRegistered compensation candidates keyed by compensation name.',
    'Grouping.current_compensation': 'current_compensation: str\n\nName of the applied compensation, or an empty string when no compensation is applied.',
    'Grouping.channels': 'channels: list[str]\n\nOrdered channel names shared by compatible samples in this grouping.',
    'Sample.name': 'name: str\n\nThe sample name.',
    'Sample.channels': 'channels: list[str]\n\nOrdered channel names in this sample.',
    'Sample.embeddings': 'embeddings: list[str]\n\nNames of sample-level embedding arrays available for plotting and downstream analysis.',
    'Sample.matrix': 'matrix: np.ndarray\n\nRaw event matrix as a NumPy copy. Rows are events and columns follow sample.channels.',
    'Sample.embedding_matrix': 'embedding_matrix: np.ndarray\n\nA NumPy copy of all sample embeddings stacked as columns in sample.embeddings order.',
    'Sample.populations': 'populations: dict[str, Population]\n\nPopulation results keyed by population key.',
    'Sample.strategy': 'strategy: Strategy\n\nThe root strategy for this sample grouping.',
    'Sample.population_keys': 'population_keys: list[str]\n\nAvailable population keys for this sample.',
    'Population.mask': 'mask: np.ndarray\n\nA boolean sample-wide mask. True marks events that belong to this population.',
    'Population.populations': 'populations: dict[str, Population]\n\nChild populations keyed by population key.',
    'Population.strategy': 'strategy: Strategy\n\nThe strategy node that produced this population.',
    'Population.population_keys': 'population_keys: list[str]\n\nAvailable child population keys.',
    'Strategy.name': 'name: str\n\nThe grouping or gate strategy name.',
    'Strategy.statistics': 'statistics: list[StatisticDefinition]\n\nStatistics attached to this strategy node.',
    'Strategy.population_keys': 'population_keys: list[str]\n\nPopulation keys generated by child gates.',
    'Strategy.has_multiple_populations': 'has_multiple_populations: bool\n\nTrue when this strategy has more than one population region.',
    'StatisticDefinition.kind': 'kind: str\n\nNative statistic kind name, or Python for source-backed Python statistics.',
    'Compensation.name': 'name: str\n\nThe compensation name.',
    'Compensation.channels': 'channels: list[str]\n\nOrdered channel names covered by the compensation matrix.',
    'Compensation.matrix': 'matrix: np.ndarray\n\nThe compensation matrix as a NumPy copy.'
}
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
def _api_attribute_doc(_item):
    _name = getattr(_item, 'name', '') or ''
    if not _name:
        return ''
    _class_name = _definition_class(_item)
    if not _class_name:
        return ''
    return _api_attribute_docs.get(f'{_class_name}.{_name}', '')
for _item in _script.complete(__line, __column):
    _name = getattr(_item, 'name', '')
    _type = getattr(_item, 'type', '')
    if not _name or _name.startswith('_') or _type in ('path', 'file'):
        continue
    _signature = ''
    if _type in ('function', 'method', 'class'):
        try:
            _signatures = _item.get_signatures() if hasattr(_item, 'get_signatures') else []
            if _signatures:
                _signature = _signatures[0].to_string()
        except Exception:
            _signature = ''
    _result.append({
        'name': _name,
        'complete': getattr(_item, 'complete', ''),
        'type': _type,
        'description': getattr(_item, 'description', ''),
        'signature': _signature,
        'docstring': _api_attribute_doc(_item) or (_item.docstring(raw=True) if hasattr(_item, 'docstring') else '')
    })
import json as _json
_result_json = _json.dumps(_result)
", globals, globals);
                using PyObject result = globals.GetItem("_result_json");
                var items = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(result.As<string>()) ?? [];
                return items
                    .Where(item => item.TryGetValue("name", out string? name) && !string.IsNullOrWhiteSpace(name))
                    .Select(item => new PythonCompletionItem(
                        item.GetValueOrDefault("name", ""),
                        item.GetValueOrDefault("complete", ""),
                        item.GetValueOrDefault("type", ""),
                        item.GetValueOrDefault("description", ""),
                        item.GetValueOrDefault("signature", ""),
                        item.GetValueOrDefault("docstring", "")))
                    .ToArray();
            }));
        }
        catch (Exception exception)
        {
            Log($"Jedi completion failed: {exception.Message}");
            return [];
        }
    }

    public static PythonHoverItem? GetPythonHoverInfo(string code, int line, int column)
    {
        try
        {
            return language_thread.Invoke(() => caller_thread.InvokeWithGil(() =>
            {
                using var globals = jedi_globals(code, line, column);
                dynamic builtins = Py.Import("builtins");
                builtins.exec(@"
_script = jedi.Script(__code)
_result = None
_items = _script.help(__line, __column)
if not _items:
    _items = _script.infer(__line, __column)
_api_attribute_docs = {
    'Workspace.metadata': 'metadata: pd.DataFrame\n\nA typed pandas DataFrame containing one row per sample. Group and Sample are read-only identity columns; all other columns are sample metadata. This is a copy; use apply_metadata(dataframe) to replace workspace metadata with the edited table.',
    'Workspace.groupings': 'groupings: list[Grouping]\n\nThe grouping collection in the current workspace. Use workspace[group_name] to retrieve one grouping by name.',
    'Grouping.name': 'name: str\n\nThe grouping name.',
    'Grouping.samples': 'samples: list[Sample]\n\nSamples contained in this grouping.',
    'Grouping.strategies': 'strategies: Strategy\n\nThe root strategy node for grouping-level gates and statistics.',
    'Grouping.compensations': 'compensations: dict[str, Compensation]\n\nRegistered compensation candidates keyed by compensation name.',
    'Grouping.current_compensation': 'current_compensation: str\n\nName of the applied compensation, or an empty string when no compensation is applied.',
    'Grouping.channels': 'channels: list[str]\n\nOrdered channel names shared by compatible samples in this grouping.',
    'Sample.name': 'name: str\n\nThe sample name.',
    'Sample.channels': 'channels: list[str]\n\nOrdered channel names in this sample.',
    'Sample.embeddings': 'embeddings: list[str]\n\nNames of sample-level embedding arrays available for plotting and downstream analysis.',
    'Sample.matrix': 'matrix: np.ndarray\n\nRaw event matrix as a NumPy copy. Rows are events and columns follow sample.channels.',
    'Sample.embedding_matrix': 'embedding_matrix: np.ndarray\n\nA NumPy copy of all sample embeddings stacked as columns in sample.embeddings order.',
    'Sample.populations': 'populations: dict[str, Population]\n\nPopulation results keyed by population key.',
    'Sample.strategy': 'strategy: Strategy\n\nThe root strategy for this sample grouping.',
    'Sample.population_keys': 'population_keys: list[str]\n\nAvailable population keys for this sample.',
    'Population.mask': 'mask: np.ndarray\n\nA boolean sample-wide mask. True marks events that belong to this population.',
    'Population.populations': 'populations: dict[str, Population]\n\nChild populations keyed by population key.',
    'Population.strategy': 'strategy: Strategy\n\nThe strategy node that produced this population.',
    'Population.population_keys': 'population_keys: list[str]\n\nAvailable child population keys.',
    'Strategy.name': 'name: str\n\nThe grouping or gate strategy name.',
    'Strategy.statistics': 'statistics: list[StatisticDefinition]\n\nStatistics attached to this strategy node.',
    'Strategy.population_keys': 'population_keys: list[str]\n\nPopulation keys generated by child gates.',
    'Strategy.has_multiple_populations': 'has_multiple_populations: bool\n\nTrue when this strategy has more than one population region.',
    'StatisticDefinition.kind': 'kind: str\n\nNative statistic kind name, or Python for source-backed Python statistics.',
    'Compensation.name': 'name: str\n\nThe compensation name.',
    'Compensation.channels': 'channels: list[str]\n\nOrdered channel names covered by the compensation matrix.',
    'Compensation.matrix': 'matrix: np.ndarray\n\nThe compensation matrix as a NumPy copy.'
}
_api_class_names = {'Workspace', 'Grouping', 'Sample', 'Population', 'Strategy', 'StatisticDefinition', 'Compensation'}
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
def _api_attribute_doc(_item):
    _name = getattr(_item, 'name', '') or ''
    if not _name:
        return ''
    _class_name = _definition_class(_item)
    if not _class_name:
        return ''
    return _api_attribute_docs.get(f'{_class_name}.{_name}', '')
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
        if _doc and (_name in _api_class_names or not _doc.startswith(_name + ':')):
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
    _doc = _item.docstring(raw=True) if hasattr(_item, 'docstring') else ''
    _api_doc = _api_attribute_doc(_item)
    if _api_doc:
        _doc = _api_doc
    elif not _doc or _type in ('statement', 'instance', 'param'):
        _inferred_doc = _best_inferred_doc(_item)
        if _inferred_doc:
            _doc = _inferred_doc
    _signature = ''
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
", globals, globals);
                using PyObject result = globals.GetItem("_result_json");
                string json = result.As<string>();
                if (json == "null")
                    return null;
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind is not JsonValueKind.Object)
                    return null;
                var item = document.RootElement;
                return new PythonHoverItem(
                    json_property(item, "type"),
                    json_property(item, "description"),
                    json_property(item, "signature"),
                    json_property(item, "docstring"),
                    json_property(item, "docblocks"));
            }));
        }
        catch (Exception exception)
        {
            Log($"Jedi hover failed: {exception.Message}");
            return null;
        }
    }

    public static string GetPythonHover(string code, int line, int column)
    {
        var item = GetPythonHoverInfo(code, line, column);
        if (item is null)
            return "";
        return $"{item.Title}\n\n{item.Documentation}".Trim();
    }

    public static void MsgBox(string title, string content)
    {
        async Task show_async()
        {
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            var window = new Window
            {
                Title = title,
                Width = 420,
                Height = 220,
                Content = new TextBlock
                {
                    Text = content,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16)
                }
            };
            if (owner is not null)
                await window.ShowDialog(owner);
            else
                window.Show();
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            _ = show_async();
        else
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(show_async).GetAwaiter().GetResult();
    }

    public static void Log(object? content)
    {
        string text = content?.ToString() ?? "";
        LogReceived?.Invoke(text);
        Console.WriteLine(text);
    }

    private static PyObject string_list(string[] values)
    {
        var list = new PyList();
        foreach (string value in values)
            list.Append(new PyString(value));
        return list;
    }

    private static PyDict jedi_globals(string code, int line, int column)
    {
        var globals = new PyDict();
        string prelude = python_analysis_prelude();
        int prelude_lines = prelude.Count(character => character == '\n');
        globals.SetItem("__builtins__", Py.Import("builtins"));
        globals.SetItem("jedi", (jedi_module ?? Py.Import("jedi")));
        globals.SetItem("np", (numpy_module ?? Py.Import("numpy")));
        globals.SetItem("pd", (pandas_module ?? Py.Import("pandas")));
        globals.SetItem("__code", $"{prelude}\n{code}".ToPython());
        globals.SetItem("__line", (line + prelude_lines + 1).ToPython());
        globals.SetItem("__column", column.ToPython());
        return globals;
    }

    private static string json_property(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return "";
        return property.ValueKind is JsonValueKind.String ? property.GetString() ?? "" : property.GetRawText();
    }

    private static void preload_jedi_modules()
    {
        using var globals = new PyDict();
        globals.SetItem("__builtins__", Py.Import("builtins"));
        globals.SetItem("jedi", (jedi_module ?? Py.Import("jedi")));
        dynamic builtins = Py.Import("builtins");
        builtins.exec("jedi.preload_module('numpy', 'pandas')", globals, globals);
    }

    private static void dispose_python_modules()
    {
        pandas_module?.Dispose();
        pandas_module = null;
        numpy_module?.Dispose();
        numpy_module = null;
        jedi_module?.Dispose();
        jedi_module = null;
    }

    internal static void WithGil(Action action) => caller_thread.InvokeWithGil(action);

    internal static T WithGil<T>(Func<T> action) => caller_thread.InvokeWithGil(action);

    private sealed class PythonInteropThread : IDisposable
    {
        private readonly BlockingCollection<PythonWorkItem> queue = new();
        private readonly Thread thread;
        private readonly bool owns_python;
        private int disposed;

        public PythonInteropThread(string name, bool owns_python)
        {
            this.owns_python = owns_python;
            thread = new Thread(run)
            {
                IsBackground = true,
                Name = name
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public bool IsCurrentThread => Thread.CurrentThread.ManagedThreadId == thread.ManagedThreadId;

        public void Invoke(Action action) => Invoke<object?>(() =>
        {
            action();
            return null;
        });

        public void InvokePythonShutdown(Action action)
        {
            if (!owns_python)
                throw new InvalidOperationException("Only the Python owner thread can shut down Python.");

            if (Volatile.Read(ref disposed) != 0)
                return;

            var item = new PythonWorkItem(() =>
            {
                action();
                return null;
            })
            {
                IsPythonShutdown = true,
                SkipModuleInitialization = true
            };
            queue.Add(item);
            item.Wait();
        }

        public T Invoke<T>(Func<T> action)
        {
            if (IsCurrentThread)
                return action();

            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(thread.Name);

            var item = new PythonWorkItem(() => action());
            queue.Add(item);
            item.Wait();
            return (T)item.Result!;
        }

        public void InvokeWithGil(Action action) => InvokeWithGil<object?>(() =>
        {
            action();
            return null;
        });

        public T InvokeWithGil<T>(Func<T> action)
        {
            if (!owns_python)
                throw new InvalidOperationException("Only the Python owner thread can acquire Python's GIL.");

            return Invoke(() =>
            {
                ensure_python_engine_initialized_on_owner_thread();
                using (Py.GIL())
                {
                    ensure_python_modules_initialized_on_owner_thread();
                    return action();
                }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            queue.CompleteAdding();
            if (Thread.CurrentThread.ManagedThreadId != thread.ManagedThreadId && thread.IsAlive)
                thread.Join(TimeSpan.FromSeconds(5));
            queue.Dispose();
        }

        private void run()
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (owns_python)
                    {
                        ensure_python_engine_initialized_on_owner_thread();
                        using (Py.GIL())
                        {
                            if (!item.SkipModuleInitialization)
                                ensure_python_modules_initialized_on_owner_thread();
                            item.Result = item.Action();
                        }

                        if (item.IsPythonShutdown)
                        {
                            PythonEngine.Shutdown();
                            lock (gate)
                            {
                                engine_initialized = false;
                                initialized = false;
                            }
                        }
                    }
                    else
                    {
                        item.Result = item.Action();
                    }
                }
                catch (Exception exception)
                {
                    item.Exception = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    item.Complete();
                }
            }
        }
    }

    private sealed class PythonWorkItem
    {
        private readonly ManualResetEventSlim completed = new(false);

        public PythonWorkItem(Func<object?> action)
        {
            Action = action;
        }

        public Func<object?> Action { get; }
        public object? Result { get; set; }
        public ExceptionDispatchInfo? Exception { get; set; }
        public bool IsPythonShutdown { get; init; }
        public bool SkipModuleInitialization { get; init; }

        public void Complete() => completed.Set();

        public void Wait()
        {
            completed.Wait();
            Exception?.Throw();
        }
    }

    private static string python_analysis_prelude() =>
        """
from __future__ import annotations
from typing import Callable
import numpy as np
import pandas as pd

class Compensation:
    '''
    A compensation matrix registered on a grouping.

    Attributes
    ==========
    name: readonly str
        The compensation name.

    channels: readonly list[str]
        The ordered channel names covered by the matrix.

    matrix: readonly np.ndarray
        A copy of the compensation matrix as a two-dimensional float array.
    '''
    name: str
    channels: list[str]
    matrix: np.ndarray

class StatisticDefinition:
    '''
    A statistic attached to a grouping or gate strategy.

    Attributes
    ==========
    kind: readonly str
        Native statistic kind name, or "Python" for source-backed Python statistics.
    '''
    kind: str
    def is_python(self) -> bool: ...
    def get_method(self) -> str: ...
    def set_method(self, source: str, callable_name: str = "statistic", display_name: str | None = None, parameters: np.ndarray | None = None): 
        '''
        Replaces this statistic with a Python-backed implementation.

        Parameters
        ==========
        source: str
            Python source text defining the callable.

        callable_name: str, default "statistic"
            Name of the callable inside source.

        display_name: str | None, default None
            Optional name shown in the statistics table.

        parameters: np.ndarray | None, default None
            Optional numeric parameter vector passed to the callable.
        '''
        ...

class Population:
    '''
    A population result for a sample and gate region.

    Attributes
    ==========
    mask: readonly np.ndarray
        Boolean sample-wide mask. True values mark events in this population.

    populations: readonly dict[str, Population]
        Child populations keyed by population key.

    strategy: readonly Strategy
        Strategy node that produced this population.

    population_keys: readonly list[str]
        Available child population keys.
    '''
    mask: np.ndarray
    populations: dict[str, "Population"]
    strategy: "Strategy"
    population_keys: list[str]
    def get_compensated_matrix(self) -> np.ndarray: 
        '''
        Returns compensated event rows selected by this population as a NumPy copy.
        '''
        ...

    def set_embedding(self, name: str, value: np.ndarray): 
        '''
        Writes a one-dimensional embedding into the owning sample for this population.

        value may contain exactly one value per selected event, or one value per
        event in the full sample. Only masked events are written; non-population
        events keep existing values or become NaN for a new embedding.
        '''
        ...

    def __getitem__(self, population_key: str) -> "Population": 
        '''
        Returns a child population by key, equivalent to population[population_key].
        '''
        ...

class Sample:
    '''
    A flow sample inside a grouping.

    Attributes
    ==========
    name: readonly str
        Sample name.

    channels: readonly list[str]
        Ordered channel names.

    embeddings: readonly list[str]
        Names of sample-level embedding arrays.

    matrix: readonly np.ndarray
        Raw event matrix as a NumPy copy.

    embedding_matrix: readonly np.ndarray
        Matrix containing all embeddings as columns.

    populations: readonly dict[str, Population]
        Populations keyed by population key.

    strategy: readonly Strategy
        Root strategy for this sample's grouping.
    '''
    name: str
    channels: list[str]
    embeddings: list[str]
    matrix: np.ndarray
    embedding_matrix: np.ndarray
    populations: dict[str, Population]
    strategy: "Strategy"
    population_keys: list[str]
    def get_compensated_matrix(self) -> np.ndarray: 
        '''
        Returns the compensated event matrix as a NumPy copy.
        '''
        ...
    def __getitem__(self, population_key: str) -> Population: 
        '''
        Returns a population by key, equivalent to sample[population_key].
        '''
        ...

class Strategy:
    '''
    A grouping or gate strategy node. Defining gates and statistics mutates the
    workspace tree immediately and recalculates the owning grouping.

    Attributes
    ==========
    name: readonly str
        Strategy or gate name.

    statistics: readonly list[StatisticDefinition]
        Statistics attached to this strategy node.

    population_keys: readonly list[str]
        Population keys generated by child gates.

    has_multiple_populations: readonly bool
        True for gates that expose multiple regions.
    '''
    name: str
    statistics: list[StatisticDefinition]
    population_keys: list[str]
    has_multiple_populations: bool
    def children(self, population_key: str = "default") -> list["Strategy"]: ...
    def get_population(self, sample: Sample) -> Population: ...
    def get_statistics(self, sample: Sample, statistic: StatisticDefinition) -> np.ndarray: ...
    def define_statistics(self, kind: str, channel: str = "") -> StatisticDefinition: 
        '''
        Adds a native statistic. kind is a StatisticKind name such as Mean,
        Median, NumberOfEvents, FrequencyOfParent, or FrequencyOfAll.
        '''
        ...
    def define_statistics_python(self, source: str, callable_name: str = "statistic", display_name: str | None = None, parameters: np.ndarray | None = None) -> StatisticDefinition: 
        '''
        Adds a Python statistic whose callable receives
        (matrix: np.ndarray, channels: list[str], parameters: np.ndarray).
        '''
        ...
    def define_gate_polygon(self, name: str, population_key: str, channel1: str, channel2: str, vertices: np.ndarray) -> "Strategy": 
        '''Adds a polygon gate. vertices is an N x 2 array.'''
        ...
    def define_gate_rectangle(self, name: str, population_key: str, channel1: str, channel2: str, rectangle: np.ndarray) -> "Strategy": 
        '''Adds a rectangle gate from a two-row or corner-style coordinate array.'''
        ...
    def define_gate_quadrant(self, name: str, population_key: str, channel1: str, channel2: str, center: np.ndarray) -> "Strategy": 
        '''Adds a quadrant gate using center coordinates.'''
        ...
    def define_gate_curly(self, name: str, population_key: str, channel1: str, channel2: str, center: np.ndarray) -> "Strategy": 
        '''Adds a curly quadrant gate using center coordinates.'''
        ...
    def define_gate_offset(self, name: str, population_key: str, channel1: str, channel2: str, positions: np.ndarray) -> "Strategy": 
        '''Adds an offset quadrant gate using offset positions.'''
        ...
    def define_gate_threshold(self, name: str, population_key: str, channel1: str, position: np.ndarray) -> "Strategy": 
        '''Adds a one-dimensional threshold gate.'''
        ...
    def define_gate_range(self, name: str, population_key: str, channel1: str, positions: np.ndarray) -> "Strategy": 
        '''Adds a one-dimensional range gate.'''
        ...
    def define_gate_overlap(self, name: str, population_key: str, gate2: str, population2: str) -> "Strategy": 
        '''Adds a boolean overlap gate against another gate population.'''
        ...
    def define_gate_exclude(self, name: str, population_key: str, gate2: str, population2: str) -> "Strategy": 
        '''Adds a boolean exclusion gate against another gate population.'''
        ...
    def define_gate_merge(self, name: str, population_key: str, gate2: str, population2: str) -> "Strategy": 
        '''Adds a boolean merge gate against another gate population.'''
        ...

class Grouping:
    '''
    A named collection of compatible samples, gates, statistics, and compensation.

    Attributes
    ==========
    name: readonly str
        Grouping name.

    samples: readonly list[Sample]
        Samples in this grouping.

    strategies: readonly Strategy
        Root strategy for gates and grouping-level statistics.

    compensations: readonly dict[str, Compensation]
        Compensation candidates keyed by name.

    current_compensation: readonly str
        Name of the applied compensation, or an empty string.

    channels: readonly list[str]
        Ordered channel names for compatible samples.
    '''
    name: str
    samples: list[Sample]
    strategies: Strategy
    compensations: dict[str, Compensation]
    current_compensation: str
    channels: list[str]
    def add_fcs(self, filename: str) -> Sample: 
        '''Reads an FCS file, validates compatibility, appends it, and recalculates.'''
        ...
    def can_accept_fcs(self, filename: str) -> bool: 
        '''Returns whether an FCS file is compatible with this grouping.'''
        ...
    def set_compensation(self, key: str) -> Compensation: 
        '''Applies a registered compensation candidate and recalculates samples.'''
        ...
    def create_compensation(self, key: str, channels: list[str], matrix: np.ndarray) -> Compensation: 
        '''Registers a compensation matrix for the supplied channel order.'''
        ...
    def __getitem__(self, sample: str) -> Sample: 
        '''Returns a sample by name, equivalent to grouping[sample].'''
        ...

class Workspace:
    '''
    The main workspace object representing the currently opened workspace in the application.
    All groupings, samples, and further properties, and metadata table are accessible through this object.

    Attributes
    ==========
    metadata: readonly pd.DataFrame
        A typed pandas DataFrame containing the workspace metadata table. Group and Sample are
        read-only identity columns; apply_metadata(dataframe) ignores them as metadata values.
    
    groupings: readonly list[Grouping]
        A list of groupings in the workspace. Each grouping represents a collection of samples that are 
        analyzed together. A grouping can have multiple samples.
    integration_jobs: readonly list[IntegrationJob]
        Integration jobs with source, logicle-normalized, integrated matrices, row maps, features,
        and batch ids for downstream Python macros.
    '''

    metadata: pd.DataFrame
    groupings: list[Grouping]
    integration_jobs: list[IntegrationJob]
    def add_grouping(self, name: str) -> Grouping: ...
    def apply_metadata(self, dataframe: pd.DataFrame): 
        '''
        Replaces workspace metadata with the given DataFrame. Group and Sample identify rows and are
        ignored as metadata fields; all other columns become typed metadata columns.
        '''
        ...
    def __getitem__(self, grouping: str) -> Grouping: ...

workspace: Workspace
def log(content): ...
def msgbox(title: str, content: str): ...
""";

    private static double scalar_result(PyObject result)
    {
        if (result.IsNone())
            return double.NaN;

        try
        {
            return result.As<double>();
        }
        catch
        {
            dynamic numpy = Py.Import("numpy");
            using PyObject array = numpy.asarray(result);
            if (array.GetAttr("size").As<int>() == 0)
                return double.NaN;
            using PyObject mean = numpy.nanmean(array);
            return mean.As<double>();
        }
    }
}

public sealed record PythonCompletionItem(
    string Name,
    string Complete,
    string Type,
    string Description,
    string Signature,
    string Documentation);

public sealed record PythonHoverItem(
    string Type,
    string Description,
    string Signature,
    string Documentation,
    string DocumentationBlocksJson)
{
    public string Title => string.IsNullOrWhiteSpace(Signature) ? Description : Signature;
}
