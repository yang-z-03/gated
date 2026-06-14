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
using Avalonia.Platform;

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
    public static event Action<PythonExecutionStatus>? StatusChanged;
    public static Func<string, IReadOnlyList<PythonStatisticRequirement>, string?>? InputRequested;

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

        report_status("Script running ...", null, true, true);
        try
        {
            caller_thread.InvokeWithGil(() =>
            {
                using var globals = new PyDict();
                globals.SetItem("__builtins__", Py.Import("builtins"));
                globals.SetItem("workspace", workspace.ToPython());
                globals.SetItem("np", (numpy_module ?? Py.Import("numpy")));
                globals.SetItem("pd", (pandas_module ?? Py.Import("pandas")));
                globals.SetItem("application", new PythonApplication().ToPython());
                try_execute(code, globals);
            });
        }
        finally
        {
            report_ready();
        }
    }

    private static void try_execute(string code, PyDict globals)
    {
        try
        {
            PythonEngine.Exec(code, globals, globals);
        } catch (PythonException pex)
        {
            string error_loc = "<Unk>";
            try
            {
                string location = pex.StackTrace.Split('\n')[0].Trim();
                string line = location.Replace("File \"<string>\", line ", "").Replace(", in <module>", "");
                error_loc = line;
            } catch { error_loc = "<Unk>"; }

            if (error_loc == "<Unk>") Log("Python interpreter error: " + pex.Message);
            else Log("Python interpreter error: " + pex.Message + "\n  " + $"at line {error_loc}: ...");
        } catch (Exception ex)
        {
            Log(ex.Message);
        }
    }

    // Internal calls only. Not exposed to user script.
    public static void ExecuteIntegrationJobScript(string resource_path, FlowWorkspace workspace, gated.Models.IntegrationJob job)
    {
        report_status("Script running ...", null, true, true);
        try
        {
            caller_thread.InvokeWithGil(() =>
            {
                using var globals = new PyDict();
                globals.SetItem("__builtins__", Py.Import("builtins"));
                globals.SetItem("workspace", new Workspace(workspace).ToPython());
                globals.SetItem("integration_job", new IntegrationJob(workspace, job).ToPython());
                globals.SetItem("np", (numpy_module ?? Py.Import("numpy")));
                globals.SetItem("pd", (pandas_module ?? Py.Import("pandas")));
                globals.SetItem("application", new PythonApplication().ToPython());
                string code = new StreamReader(AssetLoader.Open(new Uri(resource_path))).ReadToEnd();
                try_execute(code, globals);
            });
        }
        finally
        {
            report_ready();
        }
    }

    public static PythonStatisticEvaluation CalculateStatistic(gated.Models.StatisticDefinition definition, float[,] matrix, string[] channels)
    {
        if (definition.Kind != StatisticKind.Python || string.IsNullOrWhiteSpace(definition.PythonSource))
            return new PythonStatisticEvaluation(null, "");

        return caller_thread.InvokeWithGil(() =>
        {
            using var globals = statistic_globals();
            PythonEngine.Exec(definition.PythonSource, globals, globals);

            using PyObject callable = globals.GetItem(definition.PythonCallableName);
            if (callable.IsNone() || !callable.HasAttr("__call__"))
                throw new InvalidOperationException($"Python statistic callable '{definition.PythonCallableName}' was not found.");

            using PyObject py_matrix = PythonArrayConverter.ToNumpy(matrix);
            using PyObject py_channels = string_list(channels);
            using PyObject py_parameters = parameters_from_json(definition.PythonParametersJson);
            PyObject result = callable.Invoke(py_matrix, py_channels, py_parameters);
            string display_value = format_statistic_result(globals, result);
            return new PythonStatisticEvaluation(result, display_value);
        });
    }

    public static IReadOnlyList<PythonStatisticRequirement> GetStatisticRequirements(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return [];

        return caller_thread.InvokeWithGil<IReadOnlyList<PythonStatisticRequirement>>(() =>
        {
            using var globals = statistic_globals();
            PythonEngine.Exec(source, globals, globals);

            using PyObject key = "requires".ToPython();
            if (!globals.HasKey(key))
                return Array.Empty<PythonStatisticRequirement>();

            using PyObject requires = globals.GetItem("requires");
            if (requires.IsNone() || !requires.HasAttr("__call__"))
                return Array.Empty<PythonStatisticRequirement>();

            using PyObject result = requires.Invoke();
            dynamic json = Py.Import("json");
            using PyObject json_text = json.dumps(result);
            return JsonSerializer.Deserialize<List<PythonStatisticRequirement>>(
                json_text.As<string>(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        });
    }

    public static string ToJson(PyObject? value)
    {
        return caller_thread.InvokeWithGil(() =>
        {
            if (value is null || value.IsNone())
                return "[]";

            dynamic json = Py.Import("json");
            using PyObject text = json.dumps(value);
            return text.As<string>();
        });
    }

    public static void ValidateStatisticSource(string source, string callable_name)
    {
        caller_thread.InvokeWithGil(() =>
        {
            using var globals = statistic_globals();
            PythonEngine.Exec(source, globals, globals);
            ensure_callable(globals, callable_name, "Python statistic entry point");
            ensure_callable(globals, "requires", "Python statistic requirement function");
            ensure_callable(globals, "format", "Python statistic formatter function");
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
                builtins.exec(new StreamReader(AssetLoader.Open(new Uri("avares://gated/Python/completion.py"))).ReadToEnd(), globals, globals);
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
                builtins.exec(new StreamReader(AssetLoader.Open(new Uri("avares://gated/Python/hover.py"))).ReadToEnd(), globals, globals);
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

    public static string MsgBox(string title, string content, string buttons = "ok")
    {
        async Task<string> show_async()
        {
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            string selected = "cancel";
            var window = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var panel = new StackPanel
            {
                Spacing = 16,
                Margin = new Thickness(16)
            };
            panel.Children.Add(new TextBlock
            {
                Text = content,
                TextWrapping = TextWrapping.Wrap
            });
            var button_panel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8
            };

            void add_button(string caption, string value)
            {
                var button = new Button { Content = caption, MinWidth = 84 };
                button.Classes.Add("Small");
                button.Click += (_, _) =>
                {
                    selected = value;
                    window.Close(value);
                };
                button_panel.Children.Add(button);
            }

            switch ((buttons ?? "ok").Trim().ToLowerInvariant())
            {
                case "ok-cancel":
                    add_button("Cancel", "cancel");
                    add_button("OK", "ok");
                    break;
                case "yes-no-cancel":
                    add_button("Cancel", "cancel");
                    add_button("No", "no");
                    add_button("Yes", "yes");
                    break;
                default:
                    selected = "ok";
                    add_button("OK", "ok");
                    break;
            }

            panel.Children.Add(button_panel);
            window.Content = panel;
            if (owner is not null)
                return await window.ShowDialog<string>(owner) ?? selected;

            window.Show();
            return selected;
        }

        var completion = new TaskCompletionSource<string>();
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            _ = show_async().ContinueWith(task => complete_task(completion, task), TaskScheduler.Default);
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    completion.SetResult(await show_async());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            });
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    public static void Log(object? content)
    {
        string text = content?.ToString() ?? "";
        LogReceived?.Invoke(text);
        Console.WriteLine(text);
    }

    public static void Progress(double percentage, string description)
    {
        double clamped = double.IsNaN(percentage) ? 0 : Math.Clamp(percentage, 0, 100);
        report_status(string.IsNullOrWhiteSpace(description) ? "Script running ..." : description, clamped, false, true);
    }

    public static PyObject RequestInput(PyObject requirements)
    {
        using (Py.GIL())
        {
            var parsed = parse_requirements(requirements);
            string? json = InputRequested?.Invoke("Script input", parsed);
            if (json is null)
                throw new OperationCanceledException("Script input was cancelled.");
            dynamic json_module = Py.Import("json");
            return json_module.loads(json);
        }
    }

    public static PyObject CreateRequirement(
        string kind,
        string name,
        PyObject? default_value = null,
        bool multiple = false,
        double? min = null,
        double? max = null,
        PyObject? possible_values = null)
    {
        using (Py.GIL())
        {
            var item = new PyDict();
            item.SetItem("type", kind.ToPython());
            item.SetItem("name", name.ToPython());
            item.SetItem("multiple", multiple.ToPython());
            if (default_value is not null && !default_value.IsNone())
                item.SetItem("default", default_value);
            if (min.HasValue)
                item.SetItem("min", min.Value.ToPython());
            if (max.HasValue)
                item.SetItem("max", max.Value.ToPython());
            if (possible_values is not null && !possible_values.IsNone())
                item.SetItem("values", new PyList(possible_values));
            return item;
        }
    }

    private static IReadOnlyList<PythonStatisticRequirement> parse_requirements(PyObject requirements)
    {
        using (Py.GIL())
        {
            dynamic json = Py.Import("json");
            using PyObject json_text = json.dumps(requirements);
            return JsonSerializer.Deserialize<List<PythonStatisticRequirement>>(
                json_text.As<string>(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
    }

    private static void complete_task(TaskCompletionSource<string> completion, Task<string> task)
    {
        if (task.IsFaulted && task.Exception is not null)
            completion.SetException(task.Exception.InnerExceptions);
        else if (task.IsCanceled)
            completion.SetCanceled();
        else
            completion.SetResult(task.Result);
    }

    private static void report_status(string description, double? progress, bool indeterminate, bool visible) =>
        StatusChanged?.Invoke(new PythonExecutionStatus(description, progress, indeterminate, visible));

    private static void report_ready() =>
        StatusChanged?.Invoke(new PythonExecutionStatus("Script engine ready.", null, false, false));

    private static PyObject string_list(string[] values)
    {
        var list = new PyList();
        foreach (string value in values)
            list.Append(new PyString(value));
        return list;
    }

    private static PyDict statistic_globals()
    {
        var globals = new PyDict();
        globals.SetItem("__builtins__", Py.Import("builtins"));
        globals.SetItem("np", (numpy_module ?? Py.Import("numpy")));
        globals.SetItem("pd", (pandas_module ?? Py.Import("pandas")));
        globals.SetItem("application", new PythonApplication().ToPython());
        return globals;
    }

    private static PyObject parameters_from_json(string? json_text)
    {
        dynamic json = Py.Import("json");
        try
        {
            return json.loads(string.IsNullOrWhiteSpace(json_text) ? "[]" : json_text);
        }
        catch
        {
            return json.loads("[]");
        }
    }

    private static string format_statistic_result(PyDict globals, PyObject result)
    {
        using PyObject key = "format".ToPython();
        if (globals.HasKey(key))
        {
            using PyObject formatter = globals.GetItem("format");
            if (!formatter.IsNone() && formatter.HasAttr("__call__"))
            {
                using PyObject formatted = formatter.Invoke(result);
                return formatted.ToString() ?? "";
            }
        }

        return result.IsNone() ? "" : result.ToString() ?? "";
    }

    private static void ensure_callable(PyDict globals, string name, string description)
    {
        using PyObject key = name.ToPython();
        if (!globals.HasKey(key))
            throw new InvalidOperationException($"{description} '{name}' was not found.");

        using PyObject callable = globals.GetItem(name);
        if (callable.IsNone() || !callable.HasAttr("__call__"))
            throw new InvalidOperationException($"{description} '{name}' is not callable.");
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

    private static string python_analysis_prelude() => new StreamReader(AssetLoader.Open(new Uri("avares://gated/Python/stub.py"))).ReadToEnd();

}

public sealed record PythonStatisticEvaluation(PyObject? Value, string DisplayValue);

public sealed record PythonExecutionStatus(string Description, double? Progress, bool IsIndeterminate, bool IsVisible);

public sealed class PythonApplication
{
    public void log(object? content) => PythonExtensionRuntime.Log(content);

    public string msgbox(string title, string content, string buttons = "ok") =>
        PythonExtensionRuntime.MsgBox(title, content, buttons);

    public PyObject input(PyObject requires) => PythonExtensionRuntime.RequestInput(requires);

    public PyObject require_channel(string name, PyObject? @default = null, bool multiple = false) =>
        PythonExtensionRuntime.CreateRequirement("channel", name, @default, multiple);

    public PyObject require_integer(string name, PyObject? @default = null, PyObject? min = null, PyObject? max = null) =>
        PythonExtensionRuntime.CreateRequirement("integer", name, @default ?? 0.ToPython(), false, py_double(min), py_double(max));

    public PyObject require_float(string name, PyObject? @default = null, PyObject? min = null, PyObject? max = null) =>
        PythonExtensionRuntime.CreateRequirement("float", name, @default ?? 0.0.ToPython(), false, py_double(min), py_double(max));

    public PyObject require_enum(string name, PyObject possible_values, PyObject? @default = null) =>
        PythonExtensionRuntime.CreateRequirement("enum", name, @default, false, possible_values: possible_values);

    public PyObject require_option(string name, bool @default = false) =>
        PythonExtensionRuntime.CreateRequirement("option", name, @default.ToPython());

    public void progress(double percentage, string description = "") =>
        PythonExtensionRuntime.Progress(percentage, description);

    private static double? py_double(PyObject? value)
    {
        using (Py.GIL())
        {
            if (value is null || value.IsNone())
                return null;
            return value.As<double>();
        }
    }
}

public sealed class PythonStatisticRequirement
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Multiple { get; set; }
    public JsonElement Default { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public string[] Values { get; set; } = [];
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
