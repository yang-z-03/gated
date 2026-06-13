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
            try
            {
                PythonEngine.Exec(code, globals, globals);
            }
            catch(PythonException pex)
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
            }
            catch(Exception ex)
            {
                Log(ex.Message);
            }
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
