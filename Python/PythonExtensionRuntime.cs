using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using gated.Models;
using gated.Shared;
using Python.Runtime;
using Avalonia.Platform;

namespace gated.Python;

public static class PythonExtensionRuntime
{
    private static readonly object gate = new();
    private static readonly TimeSpan shutdown_python_timeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan shutdown_thread_join_timeout = TimeSpan.FromMilliseconds(250);
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
    private static PythonLogExecutionContext? current_log_context;
    private static int shutdown_started;
    public static event Action<PythonLogRunStarted>? LogRunStarted;
    public static event Action<PythonLogMessage>? LogReceived;
    public static event Action<PythonExecutionStatus>? StatusChanged;
    public static Func<string, IReadOnlyList<PythonStatisticRequirement>, string?>? InputRequested;

    public static void StartBackground()
    {
        lock (gate)
        {
            if (Volatile.Read(ref shutdown_started) != 0)
                return;

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

            Runtime.PythonDLL = PlatformSupport.EmbeddedPythonLibraryPath();
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
        if (Interlocked.Exchange(ref shutdown_started, 1) != 0)
            return;

        bool shutdown_python;
        lock (gate)
        {
            shutdown_python = engine_initialized;
            startup_task = null;
        }

        try
        {
            if (shutdown_python)
            {
                bool completed = caller_thread.InvokePythonShutdown(() =>
                {
                    lock (gate)
                    {
                        if (initialized)
                            dispose_python_modules();
                        initialized = false;
                        startup_task = null;
                    }
                }, shutdown_python_timeout);

                if (!completed)
                    Console.WriteLine("Timed out while waiting for Python runtime shutdown.");
            }
        }
        finally
        {
            language_thread.Stop(shutdown_thread_join_timeout);
            caller_thread.Stop(shutdown_thread_join_timeout);
        }
    }

    public static void DisposeWorkspaceStorage(FlowWorkspace workspace)
    {
        if (!workspace.HasPythonStorage)
            return;

        caller_thread.InvokeWithGil(() =>
        {
            workspace.DetachPythonStorage()?.Dispose();
        });
    }

    public static void Execute(string code, Workspace workspace, string task_key = "code:interactive", string task_name = "Interactive code")
    {
        if (string.IsNullOrWhiteSpace(code))
            return;

        report_status("Script running ...", null, true, true);
        try
        {
            caller_thread.InvokeWithGil(() =>
            {
                using var log_context = begin_log_run(task_key, task_name);
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
        } 
        catch (PythonException pex)
        {
            if (current_log_context?.ConsumeSuppressedException() is { } suppressed_exception)
                throw suppressed_exception;

            string error_loc = "";
            try
            {
                string[] traces = pex.StackTrace.Split('\n');
                foreach (var trace in traces)
                {
                    string ln = trace.Trim();
                    if (ln.StartsWith("File"))
                    {
                        // format: File "xxx", line xxx, in xxx
                        var match = Regex.Match(ln, @"^\s*File ""(?<file>.+?)"", line (?<line>\d+), in (?<function>.+?)\s*$");
                        string file = match.Groups["file"].Value;
                        if (file == "<string>") file = "Evaluated code";
                        else if (file.Contains("site-packages")) file = file.Substring(file.ToLower().IndexOf("site-packages") + 14);
                        int lno = int.Parse(match.Groups["line"].Value);
                        string function = match.Groups["function"].Value;
                        error_loc += $"\n  Function call {function}, {file} ({lno})";
                    }
                }
            } catch { error_loc = "\nNo stack trace available"; }

            if (error_loc == "") Fatal("Python interpreter error: " + pex.Message);
            else Fatal("Python interpreter error: " + pex.Message + error_loc);
            throw new PythonExecutionFailedException(pex.Message, pex);
        }
        catch (PythonApplicationErrorException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fatal(ex.Message);
            throw;
        }
    }

    private static PythonLogExecutionContext begin_log_run(string task_key, string task_name)
    {
        var context = new PythonLogExecutionContext(
            string.IsNullOrWhiteSpace(task_key) ? "task:unknown" : task_key,
            string.IsNullOrWhiteSpace(task_name) ? "Python task" : task_name,
            Guid.NewGuid(),
            DateTimeOffset.Now);
        current_log_context = context;
        LogRunStarted?.Invoke(new PythonLogRunStarted(context.TaskKey, context.TaskName, context.RunId, context.StartedAt));
        return context;
    }

    // Internal calls only. Not exposed to user script.
    public static void ExecutePlatformScript(
        string resource_path,
        FlowWorkspace workspace,
        gated.Models.Platform platform,
        string? task_key = null,
        string? task_name = null)
    {
        report_status("Platform script running ...", null, true, true);
        try
        {
            caller_thread.InvokeWithGil(() =>
            {
                using var log_context = begin_log_run(
                    task_key ?? $"platform:{platform.Id}",
                    task_name ?? platform.Name);
                using var globals = new PyDict();
                globals.SetItem("__builtins__", Py.Import("builtins"));
                globals.SetItem("workspace", new Workspace(workspace).ToPython());
                var wrapper = Platform.Wrap(workspace, platform);
                globals.SetItem("platform", wrapper.ToPython());
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

    public static SpectralPythonFitResult FitSpectralUnmixing(
        IReadOnlyList<float[,]> positive_matrices,
        float[,] unstained_matrix,
        IReadOnlyList<int> peak_indices)
    {
        if (positive_matrices.Count == 0 || positive_matrices.Count != peak_indices.Count)
            throw new ArgumentException("Spectral positives and peak indices must have equal non-zero length.");

        report_status("Fitting spectral signatures ...", null, true, true);
        try
        {
            return caller_thread.InvokeWithGil(() =>
            {
                using var globals = new PyDict();
                globals.SetItem("__builtins__", Py.Import("builtins"));
                globals.SetItem("np", (numpy_module ?? Py.Import("numpy")));
                using var positives = new PyList();
                foreach (var matrix in positive_matrices)
                {
                    using var array = PythonArrayConverter.ToNumpy(matrix);
                    positives.Append(array);
                }
                globals.SetItem("_positive_matrices", positives);
                using var unstained = PythonArrayConverter.ToNumpy(unstained_matrix);
                globals.SetItem("_unstained_matrix", unstained);
                using var peaks = peak_indices.ToArray().ToPython();
                globals.SetItem("_peak_indices", peaks);
                string code = new StreamReader(AssetLoader.Open(new Uri("avares://gated/Python/spectral-unmixing.py"))).ReadToEnd();
                try_execute(code, globals);
                string json = globals.GetItem("_spectral_result_json").As<string>();
                var payload = JsonSerializer.Deserialize<SpectralPythonPayload>(json) ?? throw new InvalidOperationException("Python returned no spectral fit.");
                return new SpectralPythonFitResult(
                    jagged_to_matrix(payload.Signatures),
                    jagged_to_matrix(payload.Similarity),
                    jagged_to_matrix(payload.Coefficients),
                    payload.Rank,
                    payload.Warning ?? "");
            });
        }
        finally { report_ready(); }
    }

    private static float[,] jagged_to_matrix(float[][]? source)
    {
        if (source is null || source.Length == 0) return new float[0, 0];
        int columns = source[0].Length;
        var result = new float[source.Length, columns];
        for (int row = 0; row < source.Length; row++)
        {
            if (source[row].Length != columns) throw new InvalidOperationException("Python returned a ragged spectral matrix.");
            for (int column = 0; column < columns; column++) result[row, column] = source[row][column];
        }
        return result;
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
        using (Py.GIL())
        {
        return caller_thread.InvokeWithGil(() =>
        {
            using (Py.GIL())
            {
            if (value is null || value.IsNone())
                return "[]";

            dynamic json = Py.Import("json");
            using PyObject text = json.dumps(value);
            return text.As<string>();
            }
        });
        }
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
                case "proceed-cancel":
                    add_button("Cancel", "cancel");
                    add_button("Proceed", "proceed");
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
        log(PythonLogLevel.Info, content);
    }

    public static void Warning(object? content)
    {
        log(PythonLogLevel.Warning, content);
    }

    public static void Error(object? content)
    {
        log(PythonLogLevel.Error, content);
        var exception = new PythonApplicationErrorException(content?.ToString() ?? "Python application error.");
        current_log_context?.SuppressNextPythonException(exception);
        throw exception;
    }

    public static void CancelFromWarning()
    {
        log(PythonLogLevel.Error, "User cancelled the task due to the warning");
        var exception = new OperationCanceledException("Python run cancelled by warning prompt.");
        current_log_context?.SuppressNextPythonException(exception);
        throw exception;
    }

    private static void Fatal(object? content)
    {
        log(PythonLogLevel.Fatal, content);
    }

    private static void log(PythonLogLevel level, object? content)
    {
        string text = content?.ToString() ?? "";
        if (current_log_context is { } context)
            LogReceived?.Invoke(new PythonLogMessage(context.TaskKey, context.RunId, DateTimeOffset.Now, level, text));
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
        using (Py.GIL())
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

        public bool InvokePythonShutdown(Action action, TimeSpan timeout)
        {
            if (!owns_python)
                throw new InvalidOperationException("Only the Python owner thread can shut down Python.");

            if (Volatile.Read(ref disposed) != 0)
                return true;

            var item = new PythonWorkItem(() =>
            {
                action();
                return null;
            })
            {
                IsPythonShutdown = true,
                SkipModuleInitialization = true
            };
            try
            {
                queue.Add(item);
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            return item.Wait(timeout);
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
            Stop(TimeSpan.FromSeconds(5));
        }

        public bool Stop(TimeSpan join_timeout)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return true;

            queue.CompleteAdding();
            if (Thread.CurrentThread.ManagedThreadId != thread.ManagedThreadId && thread.IsAlive)
            {
                if (!thread.Join(join_timeout))
                    return false;
            }

            queue.Dispose();
            return true;
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

        public bool Wait(TimeSpan timeout)
        {
            if (!completed.Wait(timeout))
                return false;

            Exception?.Throw();
            return true;
        }
    }

    private sealed class PythonLogExecutionContext : IDisposable
    {
        public PythonLogExecutionContext(string task_key, string task_name, Guid run_id, DateTimeOffset started_at)
        {
            TaskKey = task_key;
            TaskName = task_name;
            RunId = run_id;
            StartedAt = started_at;
        }

        public string TaskKey { get; }
        public string TaskName { get; }
        public Guid RunId { get; }
        public DateTimeOffset StartedAt { get; }
        private Exception? suppressed_exception;

        public void SuppressNextPythonException(Exception exception)
        {
            suppressed_exception = exception;
        }

        public Exception? ConsumeSuppressedException()
        {
            var exception = suppressed_exception;
            suppressed_exception = null;
            return exception;
        }

        public void Dispose()
        {
            if (ReferenceEquals(current_log_context, this))
                current_log_context = null;
        }
    }

    private static string python_analysis_prelude() => new StreamReader(AssetLoader.Open(new Uri("avares://gated/Python/stub.py"))).ReadToEnd();

}

public sealed record PythonStatisticEvaluation(PyObject? Value, string DisplayValue);

public sealed record PythonExecutionStatus(string Description, double? Progress, bool IsIndeterminate, bool IsVisible);

public sealed record SpectralPythonFitResult(float[,] Signatures, float[,] Similarity, float[,] Coefficients, int Rank, string Warning);

internal sealed class SpectralPythonPayload
{
    public float[][]? Signatures { get; set; }
    public float[][]? Similarity { get; set; }
    public float[][]? Coefficients { get; set; }
    public int Rank { get; set; }
    public string? Warning { get; set; }
}

public enum PythonLogLevel
{
    Info,
    Warning,
    Error,
    Fatal
}

public sealed record PythonLogRunStarted(string TaskKey, string TaskName, Guid RunId, DateTimeOffset StartedAt);

public sealed record PythonLogMessage(string TaskKey, Guid RunId, DateTimeOffset Timestamp, PythonLogLevel Level, string Text);

public sealed class PythonExecutionFailedException : Exception
{
    public PythonExecutionFailedException(string message, Exception inner_exception)
        : base(message, inner_exception)
    {
    }
}

public sealed class PythonApplicationErrorException : Exception
{
    public PythonApplicationErrorException(string message)
        : base(message)
    {
    }
}

public sealed class PythonApplication
{
    public void log(object? content) => PythonExtensionRuntime.Log(content);

    public void warning(object? content)
    {
        PythonExtensionRuntime.Warning(content);
        string choice = PythonExtensionRuntime.MsgBox("Python warning", content?.ToString() ?? "", "proceed-cancel");
        if (!string.Equals(choice, "proceed", StringComparison.OrdinalIgnoreCase))
            PythonExtensionRuntime.CancelFromWarning();
    }

    public void error(object? content) => PythonExtensionRuntime.Error(content);

    public string msgbox(string title, string content, string buttons = "ok") =>
        PythonExtensionRuntime.MsgBox(title, content, buttons);

    public PyObject input(PyObject requires)
    {
        using (Py.GIL())
            return PythonExtensionRuntime.RequestInput(requires);
    }

    public PyObject require_channel(string name, PyObject? @default = null, bool multiple = false)
    {
        using (Py.GIL())
            return PythonExtensionRuntime.CreateRequirement("channel", name, @default, multiple);
    }

    public PyObject require_integer(string name, PyObject? @default = null, PyObject? min = null, PyObject? max = null)
    {
        using (Py.GIL())
            return PythonExtensionRuntime.CreateRequirement("integer", name, @default ?? 0.ToPython(), false, py_double(min), py_double(max));
    }

    public PyObject require_float(string name, PyObject? @default = null, PyObject? min = null, PyObject? max = null)
    {
        using (Py.GIL())
            return PythonExtensionRuntime.CreateRequirement("float", name, @default ?? 0.0.ToPython(), false, py_double(min), py_double(max));
    }

    public PyObject require_enum(string name, PyObject possible_values, PyObject? @default = null)
    {
        using (Py.GIL())
            return PythonExtensionRuntime.CreateRequirement("enum", name, @default, false, possible_values: possible_values);
    }

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
