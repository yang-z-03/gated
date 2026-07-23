using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia;
using gated.Models;
using gated.Services;
using Python.Runtime;

namespace gated.Python;

public sealed class Workspace
{
    internal readonly FlowWorkspace Model;

    public Workspace(FlowWorkspace model)
    {
        Model = model;
    }

    public PyObject metadata
    {
        get
        {
            return PythonExtensionRuntime.WithGil(() =>
            {
                var rows = new PyList();
                using PyObject none = Py.Import("builtins").GetAttr("None");
                foreach (var group in Model.Groups)
                foreach (var sample in group.Samples)
                {
                    var row = new PyDict();
                    row.SetItem("Group", group.Name.ToPython());
                    row.SetItem("Sample", sample.Name.ToPython());
                    ensure_metadata_schema();
                    foreach (var column in Model.MetadataColumns
                                 .Where(item => item.Key is not ("Group" or "Sample"))
                                 .OrderBy(item => item.Key, StringComparer.Ordinal))
                    {
                        if (!sample.Metadata.TryGetValue(column.Key, out string? value) || string.IsNullOrWhiteSpace(value))
                        {
                            row.SetItem(column.Key, none);
                            continue;
                        }
                        row.SetItem(column.Key, metadata_value_to_python(value, column.Value));
                    }
                    rows.Append(row);
                }

                dynamic pandas = Py.Import("pandas");
                return pandas.DataFrame(rows);
            });
        }
    }

    public PyObject groupings => PythonObjects.List(Model.Groups.Select(group => new Grouping(Model, group)));
    public PyObject platforms => PythonObjects.Dict(Model.Platforms.Select(job => KeyValuePair.Create(job.Name, Platform.Wrap(Model, job))));
    public PyObject storage => PythonExtensionRuntime.WithGil(Model.GetPythonStorage);

    public Grouping add_grouping(string name)
    {
        string unique = unique_name(name, Model.Groups.Select(group => group.Name), "Group");
        var group = new FlowGroup { Name = unique };
        Model.Groups.Add(group);
        return new Grouping(Model, group);
    }

    public Grouping __getitem__(string grouping) =>
        Model.Groups
            .Select(group => new Grouping(Model, group))
            .FirstOrDefault(group => group.name == grouping)
        ?? throw new KeyNotFoundException($"Grouping '{grouping}' was not found.");

    public Grouping this[string grouping] => __getitem__(grouping);

    public void apply_metadata(PyObject dataframe)
    {
        using (Py.GIL())
        PythonExtensionRuntime.WithGil(() => {
            using (Py.GIL())
            {
                dynamic pandas = Py.Import("pandas");
                using PyObject frame = pandas.DataFrame(dataframe);
                var metadata_columns = new PyList(frame.GetAttr("columns").InvokeMethod("tolist"))
                    .Select(column => column.As<string>() ?? "")
                    .Where(column => column.Length > 0 && column is not ("Group" or "Sample"))
                    .ToArray();
                Model.MetadataColumns.Clear();
                Model.MetadataColumns["Group"] = MetadataColumnKind.String;
                Model.MetadataColumns["Sample"] = MetadataColumnKind.String;
                Model.MetadataColumns[Configuration.CytometerMetadataKey] = MetadataColumnKind.String;
                foreach (string column in metadata_columns)
                    Model.MetadataColumns[column] = infer_metadata_kind_from_frame(frame, pandas, column);
                using PyObject records = frame.InvokeMethod("to_dict", "records".ToPython());
                var rows = new PyList(records);
                foreach (PyObject rawdict in rows)
                {
                    var pdict = new PyDict(rawdict);
                    Dictionary<string, PyObject> row = new Dictionary<string, PyObject>();
                    foreach (PyObject key in pdict.Keys())
                    {
                        string key_str = key.As<string>() ?? "";
                        row[key_str] = pdict[key];
                    }
                
                    if (!row.TryGetValue("Group", out var group_value) || !row.TryGetValue("Sample", out var sample_value))
                        continue;
                    string group_name = group_value.As<string>() ?? "";
                    string sample_name = sample_value.As<string>() ?? "";
                    var sample = Model.Groups.FirstOrDefault(group => group.Name == group_name)?.Samples.FirstOrDefault(sample => sample.Name == sample_name);
                    if (sample is null)
                        continue;
                
                    foreach (string existing_key in sample.Metadata.Keys
                                 .Where(key => key is not ("Group" or "Sample") && key != Configuration.CytometerMetadataKey)
                                 .Except(metadata_columns, StringComparer.Ordinal)
                                 .ToArray())
                        sample.Metadata.Remove(existing_key);
                    foreach (string column in metadata_columns)
                    {
                        if (!row.TryGetValue(column, out var value) || pandas.isna(value).As<bool>())
                            continue;
                        sample.Metadata[column] = python_metadata_value_to_string(value!, Model.MetadataColumns[column]);
                    }
                    sample.Metadata[Configuration.CytometerMetadataKey] = Configuration.CytometerNameForSample(sample);
                }
            }
        });
    }

    private void ensure_metadata_schema()
    {
        foreach (var group in Model.Groups)
        foreach (var sample in group.Samples)
        {
            sample.Metadata["Group"] = group.Name;
            sample.Metadata["Sample"] = sample.Name;
            sample.Metadata[Configuration.CytometerMetadataKey] = Configuration.CytometerNameForSample(sample);
        }
        Model.MetadataColumns["Group"] = MetadataColumnKind.String;
        Model.MetadataColumns["Sample"] = MetadataColumnKind.String;
        Model.MetadataColumns[Configuration.CytometerMetadataKey] = MetadataColumnKind.String;
        foreach (string key in Model.Groups
                     .SelectMany(group => group.Samples)
                     .SelectMany(sample => sample.Metadata.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(key => key, StringComparer.Ordinal))
            if (!Model.MetadataColumns.ContainsKey(key))
                Model.MetadataColumns[key] = infer_metadata_kind_from_samples(key);
    }

    private MetadataColumnKind infer_metadata_kind_from_samples(string key)
    {
        var values = Model.Groups.SelectMany(group => group.Samples)
            .Select(sample => sample.Metadata.TryGetValue(key, out string? value) ? value : "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (values.Length == 0)
            return MetadataColumnKind.String;
        if (values.All(value => int.TryParse(value, out _)))
            return MetadataColumnKind.Integer;
        if (values.All(value => double.TryParse(value, out _)))
            return MetadataColumnKind.Float;
        return MetadataColumnKind.String;
    }

    private static PyObject metadata_value_to_python(string value, MetadataColumnKind kind) =>
        kind switch
        {
            MetadataColumnKind.Integer when int.TryParse(value, out int int_value) => int_value.ToPython(),
            MetadataColumnKind.Float when double.TryParse(value, out double double_value) => double_value.ToPython(),
            _ => value.ToPython()
        };

    private static MetadataColumnKind infer_metadata_kind_from_frame(PyObject frame, dynamic pandas, string column)
    {
        using (Py.GIL())
        {
        using PyObject series = frame.GetItem(column);
        using PyObject values = series.InvokeMethod("dropna").InvokeMethod("tolist");
        var list = new PyList(values);
        if (list.Length() == 0)
            return MetadataColumnKind.String;
        bool all_integer = true;
        bool all_numeric = true;
        foreach (PyObject value in list)
        {
            string text = value.ToString() ?? "";
            all_integer &= int.TryParse(text, out _);
            all_numeric &= double.TryParse(text, out _);
        }
        if (all_integer)
            return MetadataColumnKind.Integer;
        if (all_numeric)
            return MetadataColumnKind.Float;
        return MetadataColumnKind.String;
        }
    }

    private static string python_metadata_value_to_string(PyObject value, MetadataColumnKind kind)
    {
        using (Py.GIL())
        {
            return kind switch
            {
                MetadataColumnKind.Integer => Convert.ToInt32(value.As<double>()).ToString(),
                MetadataColumnKind.Float => value.As<double>().ToString("G17"),
                _ => value.As<string>() ?? value.ToString() ?? ""
            };
        }
    }

    internal static string unique_name(string? preferred, IEnumerable<string> existing, string fallback)
    {
        string base_name = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
        var names = existing.ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(base_name))
            return base_name;

        int index = 2;
        while (names.Contains($"{base_name} {index}"))
            index++;
        return $"{base_name} {index}";
    }
}

public class Platform
{
    protected readonly FlowWorkspace workspace;
    internal readonly Models.Platform Model;

    internal Platform(FlowWorkspace workspace, Models.Platform model)
    {
        this.workspace = workspace;
        Model = model;
    }

    internal static Platform Wrap(FlowWorkspace workspace, Models.Platform model) =>
        model switch
        {
            Models.UnivariatePlatform univariate => new UnivariatePlatform(workspace, univariate),
            Models.MultivariatePlatform multivariate => new MultivariatePlatform(workspace, multivariate),
            _ => new Platform(workspace, model)
        };

    public string name => Model.Name;
    public string guid => Model.Id.ToString();
    public string transform => transform_name(Model.Axis.Transform);
    public PyObject transformations => PythonObjects.Dict(build_transformations());
    public PyObject parameters => PythonObjects.Dict(Model.Parameters);
    public PyObject channels => PythonObjects.List(Model.SelectedFeatureNames);
    public PyObject populations => PythonObjects.List(platform_populations());
    public PyObject matrix => PythonArrayConverter.ToNumpy(Model.Matrix ?? new float[0, 0]);
    public PyObject compensated => PythonArrayConverter.ToNumpy(Model.Compensated ?? new float[0, 0]);
    public PyObject transformed => PythonArrayConverter.ToNumpy(Model.Transformed ?? new float[0, 0]);
    public PyObject series => PythonObjects.Dict(Model.Series);
    public PyObject models => PythonObjects.Dict(Model.Models);
    public PyObject components => PythonObjects.Dict(Model.Components);
    public PyObject result => result_tables();
    public bool has_graphics => Model.HasGraphics;
    public bool has_data_table => Model.HasDataTable;
    public PyObject row_map => build_row_map();

    public void clear_results()
    {
        Model.ClearFitResults();
    }

    public void set_result_table(string key, string title, PyObject columns, PyObject rows)
    {
        using (Py.GIL())
        {
            var table = new PlatformResultTable
            {
                Key = string.IsNullOrWhiteSpace(key) ? "results" : key,
                Title = string.IsNullOrWhiteSpace(title) ? "Results" : title,
                Columns = PlatformPythonHelpers.StringArray(columns)
            };

            foreach (var row in PlatformPythonHelpers.Rows(rows))
                table.Rows.Add(row);

            var existing = Model.ResultTables.FirstOrDefault(item => item.Key == table.Key);
            if (existing is not null)
                Model.ResultTables.Remove(existing);
            Model.ResultTables.Add(table);
        }
    }

    public void set_plot_series(string key, string title, PyObject x, PyObject y, string x_label = "", string y_label = "", int source_id = -1, string role = "observed")
    {
        using (Py.GIL())
        {
            if (!Enum.TryParse(role, ignoreCase: true, out PlatformSeriesRole series_role))
                throw new ArgumentException($"Unknown platform series role '{role}'. Expected observed, fit, or component.", nameof(role));
            var series = new PlatformPlotSeries
            {
                Key = string.IsNullOrWhiteSpace(key) ? "plot" : key,
                Title = string.IsNullOrWhiteSpace(title) ? "Plot" : title,
                XLabel = x_label ?? "",
                YLabel = y_label ?? "",
                SourceId = source_id,
                Role = series_role,
                X = PlatformPythonHelpers.DoubleArray(x),
                Y = PlatformPythonHelpers.DoubleArray(y)
            };
    
            var existing = Model.PlotSeries.FirstOrDefault(item => item.Key == series.Key);
            if (existing is not null)
                Model.PlotSeries.Remove(existing);
            Model.PlotSeries.Add(series);
            Model.Series[series.Key] = series;
        }
    }

    public void set_fit_curve(string key, string title, string kind, int source_id, PyObject parameters, double normalizer = 1.0, string x_label = "", string y_label = "")
    {
        using (Py.GIL())
        {
            if (!Enum.TryParse(kind, ignoreCase: true, out PlatformFitCurveKind curve_kind))
                throw new ArgumentException($"Unknown fit curve kind '{kind}'.", nameof(kind));

            var curve = new PlatformFitCurve
            {
                Key = string.IsNullOrWhiteSpace(key) ? "fit" : key,
                Title = string.IsNullOrWhiteSpace(title) ? "Fit" : title,
                Kind = curve_kind,
                SourceId = source_id,
                Role = PlatformSeriesRole.Fit,
                Parameters = PlatformPythonHelpers.DoubleArray(parameters),
                Normalizer = double.IsFinite(normalizer) && normalizer > 0 ? normalizer : 1.0,
                XLabel = x_label ?? "",
                YLabel = y_label ?? "",
                FitTransformation = Model.Axis.Transform,
                FitLogicle = Model.Axis.Logicle
            };

            var existing = Model.FitCurves.FirstOrDefault(item => item.Key == curve.Key);
            if (existing is not null)
                Model.FitCurves.Remove(existing);
            Model.FitCurves.Add(curve);
            Model.Models[curve.Key] = curve;
        }
    }

    public void add_component_normal(string key, double mu, double sigma, double amplitude)
    {
        var curve = create_curve(key, key, PlatformFitCurveKind.Gaussian, -1, [amplitude, mu, sigma], role: PlatformSeriesRole.Component);
        add_component_curve(key, curve);
    }

    public void add_component_gamma(string key, double alpha, double beta, double amplitude)
    {
        var curve = create_curve(key, key, PlatformFitCurveKind.Gamma, -1, [alpha, beta, amplitude], role: PlatformSeriesRole.Component);
        add_component_curve(key, curve);
    }

    public void add_component_exponential(string key, double slope, double expn, double intercept)
    {
        var curve = create_curve(key, key, PlatformFitCurveKind.Exponential, -1, [slope, expn, intercept], role: PlatformSeriesRole.Component);
        add_component_curve(key, curve);
    }

    public void set_fit_addition(string key, PyObject models, PyObject weights, double intercept = 0)
    {
        using (Py.GIL())
        {
            string[] model_keys = PlatformPythonHelpers.StringArray(models);
            double[] weight_values = PlatformPythonHelpers.DoubleArray(weights);
            if (model_keys.Length != weight_values.Length)
                throw new ArgumentException("models and weights must have the same length.");
            var curve = create_curve(key, key, PlatformFitCurveKind.Addition, -1, [], model_keys: model_keys, weights: weight_values, intercept: intercept);
            set_model_curve(key, curve);
        }
    }

    public void set_statistic(string name, object? value)
    {
        using (Py.GIL())
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
    
            var existing = Model.PlatformStatistics.FirstOrDefault(item => item.Name == name);
            if (existing is not null)
                Model.PlatformStatistics.Remove(existing);
            Model.PlatformStatistics.Add(new PlatformStatisticResult { Name = name, Value = value?.ToString() ?? "" });
        }
    }

    private PlatformFitCurve create_curve(
        string key,
        string title,
        PlatformFitCurveKind kind,
        int source_id,
        double[] parameters,
        PlatformSeriesRole role = PlatformSeriesRole.Fit,
        double normalizer = 1.0,
        string x_label = "",
        string y_label = "",
        string[]? model_keys = null,
        double[]? weights = null,
        double intercept = 0) =>
        new()
        {
            Key = string.IsNullOrWhiteSpace(key) ? kind.ToString().ToLowerInvariant() : key,
            Title = string.IsNullOrWhiteSpace(title) ? key : title,
            Kind = kind,
            SourceId = source_id,
            Role = role,
            Parameters = parameters,
            Normalizer = double.IsFinite(normalizer) && normalizer > 0 ? normalizer : 1.0,
            XLabel = x_label,
            YLabel = y_label,
            FitTransformation = Model.Axis.Transform,
            FitLogicle = Model.Axis.Logicle,
            ModelKeys = model_keys ?? [],
            Weights = weights ?? [],
            Intercept = intercept
        };

    private void add_component_curve(string key, PlatformFitCurve curve)
    {
        if (!Model.Components.TryGetValue(key, out var curves))
        {
            curves = new List<PlatformFitCurve>();
            Model.Components[key] = curves;
        }
        curves.Add(curve);
        var existing = Model.FitCurves.FirstOrDefault(item => item.Key == curve.Key);
        if (existing is not null)
            Model.FitCurves.Remove(existing);
        Model.FitCurves.Add(curve);
    }

    private void set_model_curve(string key, PlatformFitCurve curve)
    {
        Model.Models[key] = curve;
        upsert_fit_curve(curve);
    }

    private void upsert_fit_curve(PlatformFitCurve curve)
    {
        var existing = Model.FitCurves.FirstOrDefault(item => item.Key == curve.Key);
        if (existing is not null)
            Model.FitCurves.Remove(existing);
        Model.FitCurves.Add(curve);
        Model.Models[curve.Key] = curve;
    }

    public string sample_metadata(string sample_name, string column_name)
    {
        using (Py.GIL())
        {
            if (string.IsNullOrWhiteSpace(sample_name) || string.IsNullOrWhiteSpace(column_name))
                return "";
            var sample = workspace.Groups.SelectMany(group => group.Samples)
                .FirstOrDefault(item => string.Equals(item.Name, sample_name, StringComparison.Ordinal));
            return sample is not null && sample.Metadata.TryGetValue(column_name, out string? value) ? value ?? "" : "";
        }
    }

    public void set_embedding(string name, PyObject value)
    {
        using (Py.GIL())
        {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Embedding name cannot be empty.", nameof(name));
        if (string.Equals(name, "Populations", StringComparison.Ordinal))
            throw new ArgumentException("Embedding 'Populations' is generated from gates and cannot be overwritten.", nameof(name));

        var input = PythonArrayConverter.ToEmbeddingArray(value);
        if (input.Values.Length != Model.RowMap.Count)
            throw new ArgumentException($"Embedding '{name}' expects {Model.RowMap.Count} values, one for each platform row.");

        var samples = Model.RowMap.Sources
            .Select(source => find_sample(source.SampleId))
            .Where(sample => sample is not null)
            .Cast<FlowSample>()
            .DistinctBy(sample => sample.Id)
            .ToDictionary(sample => sample.Id);

        var sample_values = new Dictionary<Guid, float[]>();
        var sample_categories = new Dictionary<Guid, Dictionary<int, string>>();
        foreach (var sample in samples.Values)
        {
            bool existing_matches = sample.Embeddings.TryGetValue(name, out var existing) &&
                                    existing.Values.Length == sample.EventCount &&
                                    existing.Kind == input.Kind;
            sample_values[sample.Id] = existing_matches
                ? existing!.Values.ToArray()
                : Enumerable.Repeat(float.NaN, sample.EventCount).ToArray();
            sample_categories[sample.Id] = existing_matches && existing is not null
                ? new Dictionary<int, string>(existing.Categories)
                : new Dictionary<int, string>();
        }

        var remapped_by_sample = new Dictionary<Guid, float[]>();
        if (input.Kind == EmbeddingValueKind.Integer)
        {
            foreach (Guid sample_id in samples.Keys)
                remapped_by_sample[sample_id] = PlatformPythonHelpers.RemapCategoryValues(input.Values, input.Categories, sample_categories[sample_id]);
        }

        for (int row = 0; row < Model.RowMap.Count; row++)
        {
            int source_id = Model.RowMap.SourceIds[row];
            if (source_id < 0 || source_id >= Model.RowMap.Sources.Count)
                continue;
            var source = Model.RowMap.Sources[source_id];
            if (!samples.TryGetValue(source.SampleId, out var sample))
                continue;
            int event_index = Model.RowMap.EventIndices[row];
            if (event_index < 0 || event_index >= sample.EventCount)
                continue;
            sample_values[sample.Id][event_index] = input.Kind == EmbeddingValueKind.Integer
                ? remapped_by_sample[sample.Id][row]
                : input.Values[row];
        }

        foreach (var sample in samples.Values)
        {
            var embedding = new EmbeddingData { Kind = input.Kind, Values = sample_values[sample.Id] };
            foreach (var category in sample_categories[sample.Id])
                embedding.Categories[category.Key] = category.Value;
            sample.Embeddings[name] = embedding;
            sample.InvalidateNormalizedChannelCache();
        }
        }
    }

    private PyObject build_row_map()
    {
        return PythonExtensionRuntime.WithGil(() =>
        {
            var rows = new PyList();
            for (int row = 0; row < Model.RowMap.Count; row++)
            {
                int source_id = Model.RowMap.SourceIds[row];
                var source = source_id >= 0 && source_id < Model.RowMap.Sources.Count
                    ? Model.RowMap.Sources[source_id]
                    : null;
                var sample = source is null ? null : find_sample(source.SampleId);
                var group = source is null ? null : workspace.Groups.FirstOrDefault(item => item.Id == source.GroupId);
                var population = source is null || sample is null ? null : find_population(sample.Populations, source.GateId, source.Region);
                var dict = new PyDict();
                dict.SetItem("row", row.ToPython());
                dict.SetItem("source_id", source_id.ToPython());
                dict.SetItem("group", (group?.Name ?? "").ToPython());
                dict.SetItem("sample", (sample?.Name ?? "").ToPython());
                dict.SetItem("population", (population?.DisplayName ?? sample?.Name ?? "").ToPython());
                dict.SetItem("group_id", (source?.GroupId.ToString() ?? "").ToPython());
                dict.SetItem("sample_id", (source?.SampleId.ToString() ?? "").ToPython());
                dict.SetItem("gate_id", (source?.GateId.ToString() ?? "").ToPython());
                dict.SetItem("region", (source?.Region.ToString() ?? "").ToPython());
                dict.SetItem("event_index", Model.RowMap.EventIndices[row].ToPython());
                rows.Append(dict);
            }

            dynamic pandas = Py.Import("pandas");
            return pandas.DataFrame(rows);
        });
    }

    protected FlowSample? find_sample(Guid sample_id) =>
        workspace.Groups.SelectMany(group => group.Samples).FirstOrDefault(sample => sample.Id == sample_id);

    private IEnumerable<PlatformPopulation> platform_populations() =>
        Model.Populations
            .Where(row => row.IsPlatformDropped)
            .Select(row => new PlatformPopulation(row));

    private IEnumerable<KeyValuePair<string, ViewOptions>> build_transformations()
    {
        foreach (string channel in Model.SelectedFeatureNames)
        {
            if (Model.Transformations.TryGetValue(channel, out var options))
                yield return KeyValuePair.Create(channel, new ViewOptions(options));
            else
                yield return KeyValuePair.Create(channel, new ViewOptions(Model));
        }
    }

    private PyObject result_tables() =>
        PythonObjects.Dict(Model.ResultTables.Select(table => KeyValuePair.Create(table.Key, table)));

    private static string transform_name(PlatformTransformationKind kind) =>
        kind switch
        {
            PlatformTransformationKind.Logarithm => "logarithm",
            PlatformTransformationKind.Logicle => "logicle",
            PlatformTransformationKind.Arcsinh => "arcsinh",
            _ => "linear"
        };

    private static PopulationResult? find_population(IEnumerable<PopulationResult> populations, Guid gate_id, PopulationRegion region)
    {
        foreach (var population in populations)
        {
            if (population.Gate.Id == gate_id && population.Region == region)
                return population;
            var child = find_population(population.Children, gate_id, region);
            if (child is not null)
                return child;
        }

        return null;
    }
}

public sealed class UnivariatePlatform : Platform
{
    private readonly Models.UnivariatePlatform model;

    internal UnivariatePlatform(FlowWorkspace workspace, Models.UnivariatePlatform model) : base(workspace, model)
    {
        this.model = model;
    }

    public string major => model.Major;
    public PyObject histogram => PythonArrayConverter.ToNumpy(model.Histogram);
    public PyObject smoothed => PythonArrayConverter.ToNumpy(model.Smoothed);
    public int smoothing_window => model.SmoothingWindow;
    public bool enable_smoothing => model.EnableSmoothing;
}

public sealed class MultivariatePlatform : Platform
{
    private readonly Models.MultivariatePlatform model;

    internal MultivariatePlatform(FlowWorkspace workspace, Models.MultivariatePlatform model) : base(workspace, model)
    {
        this.model = model;
    }

    public PyObject normalized => PythonArrayConverter.ToNumpy(model.Normalized ?? new float[0, 0]);
}

public sealed class ViewOptions
{
    private readonly PlatformChannelTransformation? options;
    private readonly Models.Platform? platform;

    internal ViewOptions(PlatformChannelTransformation options)
    {
        this.options = options;
    }

    internal ViewOptions(Models.Platform platform)
    {
        this.platform = platform;
    }

    public double min => options?.Minimum ?? platform?.Axis.Minimum ?? 0;
    public double max => options?.Maximum ?? platform?.Axis.Maximum ?? 0;
    public double t => options?.Logicle.T ?? platform?.Axis.Logicle.T ?? 0;
    public double w => options?.Logicle.W ?? platform?.Axis.Logicle.W ?? 0;
    public double m => options?.Logicle.M ?? platform?.Axis.Logicle.M ?? 0;
    public double a => options?.Logicle.A ?? platform?.Axis.Logicle.A ?? 0;
}

public sealed class PlatformPopulation
{
    private readonly PlatformPopulationInput model;

    internal PlatformPopulation(PlatformPopulationInput model)
    {
        this.model = model;
    }

    public string group => model.GroupName;
    public string sample => model.SampleName;
    public string name => model.PopulationName;
    public string population => model.PopulationName;
    public string group_id => model.GroupId.ToString();
    public string sample_id => model.SampleId.ToString();
    public string gate_id => model.GateId.ToString();
    public string region => model.Region.ToString();
    public bool selected => model.IsSelected;
}

internal static class PlatformPythonHelpers
{
    public static string[] StringArray(PyObject value)
    {
        using (Py.GIL())
        {
        dynamic json = Py.Import("json");
        using PyObject text = json.dumps(value);
        return JsonSerializer.Deserialize<string[]>(text.As<string>()) ?? [];
        }
    }

    public static double[] DoubleArray(PyObject value)
    {
        using (Py.GIL())
        {
        dynamic json = Py.Import("json");
        using PyObject text = json.dumps(value);
        return JsonSerializer.Deserialize<double[]>(text.As<string>()) ?? [];
        }
    }

    public static IEnumerable<string[]> Rows(PyObject value)
    {
        using (Py.GIL())
        {
        dynamic json = Py.Import("json");
        using PyObject text = json.dumps(value);
        return JsonSerializer.Deserialize<List<string[]>>(text.As<string>()) ?? [];
        }
    }

    public static float[] RemapCategoryValues(
        float[] values,
        IReadOnlyDictionary<int, string> input_categories,
        Dictionary<int, string> target_categories)
    {
        var ids_by_label = target_categories.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
        var id_map = new Dictionary<int, int>();
        foreach (var input_category in input_categories)
        {
            if (!ids_by_label.TryGetValue(input_category.Value, out int target_id))
            {
                target_id = target_categories.Count == 0 ? 1 : target_categories.Keys.Max() + 1;
                target_categories[target_id] = input_category.Value;
                ids_by_label[input_category.Value] = target_id;
            }

            id_map[input_category.Key] = target_id;
        }

        var mapped = new float[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
            {
                mapped[index] = float.NaN;
                continue;
            }

            int source_id = Convert.ToInt32(values[index]);
            mapped[index] = id_map.TryGetValue(source_id, out int target_id) ? target_id : float.NaN;
        }

        return mapped;
    }
}

public sealed class Grouping
{
    private readonly FlowWorkspace workspace;
    internal readonly FlowGroup Model;

    internal Grouping(FlowWorkspace workspace, FlowGroup model)
    {
        this.workspace = workspace;
        Model = model;
    }

    public string name => Model.Name;
    public PyObject samples => PythonObjects.List(Model.Samples.Select(sample => new Sample(Model, sample)));
    public Strategy strategies => new(Model, null);
    public PyObject compensations => PythonObjects.Dict(Model.CompensationCandidates.Select(item => KeyValuePair.Create(item.Name, new Compensation(item))));
    public string current_compensation => Model.AppliedCompensation?.Name ?? "";
    public PyObject channels => PythonObjects.List(Model.Channels.Select(channel => channel.Name));

    public Sample add_fcs(string filename)
    {
        var sample = new FcsReader().Read(filename);
        if (!Model.CanAccept(sample))
            throw new InvalidOperationException($"FCS file '{filename}' is not compatible with grouping '{Model.Name}'.");
        Model.AddSample(sample);
        return new Sample(Model, sample);
    }

    public bool can_accept_fcs(string filename)
    {
        var sample = new FcsReader().Read(filename);
        return Model.CanAccept(sample);
    }

    public Compensation set_compensation(string key)
    {
        var compensation = Model.CompensationCandidates.FirstOrDefault(item => item.Name == key)
            ?? throw new KeyNotFoundException($"Compensation '{key}' was not found.");
        Model.SetAppliedCompensation(compensation, manual: true);
        return new Compensation(compensation);
    }

    public Compensation create_compensation(string key, PyObject channels, PyObject matrix)
    {
        using (Py.GIL())
        {
        PythonExtensionRuntime.EnsureInitialized();
        return PythonExtensionRuntime.WithGil(() =>
        {
            using (Py.GIL())
            {
            var channel_names = PythonArrayConverter.To<string>(new PyList(channels));
            var values = PythonArrayConverter.ToFloatMatrix(matrix);
            if (values.GetLength(0) != channel_names.Count || values.GetLength(1) != channel_names.Count)
                throw new ArgumentException("Compensation matrix dimensions must match the channel list.");

            var compensation = CompensationMatrix.Create(key, channel_names, values);
            return new Compensation(Model.RegisterCompensation(compensation, make_applied_if_first: false));
            }
        });
        }
    }

    public Sample __getitem__(string sample) =>
        Model.Samples
            .Select(item => new Sample(Model, item))
            .FirstOrDefault(item => item.name == sample)
        ?? throw new KeyNotFoundException($"Sample '{sample}' was not found.");

    public Sample this[string sample] => __getitem__(sample);
}

public sealed class Strategy
{
    private readonly FlowGroup group;
    private readonly GateDefinition? gate;

    internal Strategy(FlowGroup group, GateDefinition? gate)
    {
        this.group = group;
        this.gate = gate;
    }

    public string name => gate?.Name ?? group.Name;
    public PyObject statistics => PythonObjects.List(definitions().Select(definition => new StatisticDefinition(group, definition)));
    public PyObject population_keys => PythonObjects.List(source_gates().SelectMany(item => Population.KeysForGate(item)));
    public bool has_multiple_populations => gate?.PopulationRegions.Count > 1;

    public PyObject children(string population_key = "default")
    {
        var region = gate is null ? PopulationRegion.Primary : Population.ResolveRegion(gate, population_key);
        return PythonObjects.List(source_gates()
            .Where(child => gate is null || child.ParentPopulationRegion == region)
            .Select(child => new Strategy(group, child)));
    }

    public Population get_population(Sample sample)
    {
        if (gate is null)
            return new Population(group, sample.Model, null);
        var population = Population.Find(sample.Model.Populations, gate, PopulationRegion.Primary)
            ?? throw new KeyNotFoundException($"Population for strategy '{gate.Name}' was not found in sample '{sample.name}'.");
        return new Population(group, sample.Model, population);
    }

    public PyObject get_statistics(Sample sample, StatisticDefinition statistic)
    {
        PythonExtensionRuntime.EnsureInitialized();
        return PythonExtensionRuntime.WithGil(() =>
        {
            var definition = statistic.Model;
            double value;
            if (gate is null)
            {
                var indices = Enumerable.Range(0, sample.Model.EventCount).ToArray();
                value = StatisticsCalculator.Calculate(sample.Model, definition, indices, sample.Model.EventCount, sample.Model.EventCount).Value;
            }
            else
            {
                var population = Population.Find(sample.Model.Populations, gate, PopulationRegion.Primary);
                value = population?.Statistics.FirstOrDefault(item => item.Kind == definition.Kind && item.ChannelName == definition.ChannelName)?.Value ?? double.NaN;
            }
            return PythonArrayConverter.ToNumpy([value]);
        });
    }

    public StatisticDefinition define_statistics(string kind, string channel = "")
    {
        if (!Enum.TryParse(kind, ignoreCase: true, out StatisticKind statistic_kind))
            throw new ArgumentException($"Unknown statistic kind '{kind}'.");

        var definition = new Models.StatisticDefinition
        {
            Kind = statistic_kind,
            ChannelName = string.IsNullOrWhiteSpace(channel) ? default_channel() : channel
        };
        definitions().Add(definition);
        group.RecalculateSamples();
        return new StatisticDefinition(group, definition);
    }

    public StatisticDefinition define_statistics_python(
        string source,
        string callable_name = "entry",
        string? display_name = null,
        PyObject? parameters = null)
    {
        using (Py.GIL())
        {
        PythonExtensionRuntime.ValidateStatisticSource(source, callable_name);
        var definition = new Models.StatisticDefinition();
        definition.SetPythonMethod(source, callable_name, display_name, PythonExtensionRuntime.ToJson(parameters));
        definitions().Add(definition);
        group.RecalculateSamples();
        return new StatisticDefinition(group, definition);
        }
    }

    public Strategy define_gate_polygon(string name, string population_key, string channel1, string channel2, PyObject vertices)
    {
        using (Py.GIL())
            return add_gate(name, GateKind.Polygon, population_key, channel1, channel2, PythonArrayConverter.ToFloatMatrix(vertices));
    }

    public Strategy define_gate_rectangle(string name, string population_key, string channel1, string channel2, PyObject rectangle)
    {
        using (Py.GIL())
            return add_gate(name, GateKind.Rectangle, population_key, channel1, channel2, PythonArrayConverter.ToFloatMatrix(rectangle));
    }

    public Strategy define_gate_quadrant(string name, string population_key, string channel1, string channel2, PyObject center)
    {
        using (Py.GIL())
            return add_gate(name, GateKind.Quadrant, population_key, channel1, channel2, PythonArrayConverter.ToFloatMatrix(center));
    }

    public Strategy define_gate_curly(string name, string population_key, string channel1, string channel2, PyObject center)
    {
        using (Py.GIL())
            return add_gate(name, GateKind.CurlyQuadrant, population_key, channel1, channel2, PythonArrayConverter.ToFloatMatrix(center));
    }

    public Strategy define_gate_offset(string name, string population_key, string channel1, string channel2, PyObject positions)
    {
        using (Py.GIL())
            return add_gate(name, GateKind.OffsetQuadrant, population_key, channel1, channel2, PythonArrayConverter.ToFloatMatrix(positions));
    }

    public Strategy define_gate_threshold(string name, string population_key, string channel1, PyObject position)
    {
        using (Py.GIL())
            return add_gate(name, GateKind.Threshold, population_key, channel1, null, PythonArrayConverter.ToFloatMatrix(position));
    }

    public Strategy define_gate_range(string name, string population_key, string channel1, PyObject positions)
    {
        using (Py.GIL())
            return add_gate(name, GateKind.Range, population_key, channel1, null, PythonArrayConverter.ToFloatMatrix(positions));
    }

    public Strategy define_gate_overlap(string name, string population_key, string gate2, string population2) =>
        add_boolean_gate(name, GateKind.Overlap, population_key, gate2, population2);

    public Strategy define_gate_exclude(string name, string population_key, string gate2, string population2) =>
        add_boolean_gate(name, GateKind.Exclude, population_key, gate2, population2);

    public Strategy define_gate_merge(string name, string population_key, string gate2, string population2) =>
        add_boolean_gate(name, GateKind.Merge, population_key, gate2, population2);

    private Strategy add_gate(string name, GateKind kind, string population_key, string channel1, string? channel2, float[,] positions)
    {
        var child = new GateDefinition
        {
            Name = Workspace.unique_name(name, all_gates(group.Gates).Select(item => item.Name), "Gate"),
            Kind = kind,
            XChannel = channel1,
            YChannel = kind is GateKind.Threshold or GateKind.Range ? null : channel2,
            Parent = gate,
            ParentPopulationRegion = gate is null ? PopulationRegion.Primary : Population.ResolveRegion(gate, population_key)
        };

        for (int row = 0; row < positions.GetLength(0); row++)
        {
            double x = positions[row, 0];
            double y = positions.GetLength(1) > 1 ? positions[row, 1] : 0;
            child.Vertices.Add(new Point(x, y));
        }

        child.Statistics.Add(new Models.StatisticDefinition { Kind = StatisticKind.NumberOfEvents, ChannelName = channel1 });
        child.Statistics.Add(new Models.StatisticDefinition { Kind = StatisticKind.FrequencyOfParent, ChannelName = channel1 });
        siblings().Add(child);
        group.RecalculateSamples();
        return new Strategy(group, child);
    }

    private Strategy add_boolean_gate(string name, GateKind kind, string population_key, string gate2, string population2)
    {
        if (gate is null)
            throw new InvalidOperationException("Boolean gates must be defined from an existing strategy.");
        var other = all_gates(group.Gates).FirstOrDefault(item => item.Name == gate2)
            ?? throw new KeyNotFoundException($"Strategy '{gate2}' was not found.");
        var child = new GateDefinition
        {
            Name = Workspace.unique_name(name, all_gates(group.Gates).Select(item => item.Name), kind.ToString()),
            Kind = kind,
            XChannel = gate.XChannel,
            YChannel = gate.YChannel,
            Parent = gate,
            ParentPopulationRegion = Population.ResolveRegion(gate, population_key),
            BooleanFirstGateId = gate.Id,
            BooleanFirstRegion = Population.ResolveRegion(gate, population_key),
            BooleanSecondGateId = other.Id,
            BooleanSecondRegion = Population.ResolveRegion(other, population2)
        };
        child.Statistics.Add(new Models.StatisticDefinition { Kind = StatisticKind.NumberOfEvents, ChannelName = child.XChannel });
        child.Statistics.Add(new Models.StatisticDefinition { Kind = StatisticKind.FrequencyOfParent, ChannelName = child.XChannel });
        gate.Children.Add(child);
        group.RecalculateSamples();
        return new Strategy(group, child);
    }

    private IList<Models.StatisticDefinition> definitions() => gate?.Statistics ?? group.Statistics;
    private IEnumerable<GateDefinition> source_gates() => gate?.Children ?? group.Gates;
    private IList<GateDefinition> siblings() => gate?.Children ?? group.Gates;
    private string default_channel() => gate?.XChannel ?? group.Channels.FirstOrDefault()?.Name ?? "";

    private static IEnumerable<GateDefinition> all_gates(IEnumerable<GateDefinition> gates)
    {
        foreach (var item in gates)
        {
            yield return item;
            foreach (var child in all_gates(item.Children))
                yield return child;
        }
    }
}

public sealed class Sample
{
    private readonly FlowGroup group;
    internal readonly FlowSample Model;

    internal Sample(FlowGroup group, FlowSample model)
    {
        this.group = group;
        Model = model;
    }

    public string name => Model.Name;
    public PyObject channels => PythonObjects.List(Model.Channels.Select(channel => channel.Name));
    public PyObject embeddings => PythonObjects.List(embedding_names());
    public PyObject matrix => PythonArrayConverter.ToNumpy(Model.RawEvents);
    public PyObject embedding_matrix => PythonArrayConverter.ToNumpy(build_embedding_matrix());
    public PyObject populations => PythonObjects.Dict(population_items());
    public Strategy strategy => new(group, null);
    public PyObject population_keys => PythonObjects.List(population_items().Select(item => item.Key));
    public PyObject compensated_matrix => PythonArrayConverter.ToNumpy(Model.CompensatedEvents);

    public Population __getitem__(string population_key) =>
        population_items().ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal).TryGetValue(population_key, out var population)
            ? population
            : throw new KeyNotFoundException($"Population '{population_key}' was not found.");

    public Population this[string population_key] => __getitem__(population_key);

    private float[,] build_embedding_matrix()
    {
        var names = embedding_names();
        var matrix = new float[Model.EventCount, names.Count];
        for (int column = 0; column < names.Count; column++)
        {
            var values = Model.Embeddings[names[column]].Values;
            for (int row = 0; row < Model.EventCount && row < values.Length; row++)
                matrix[row, column] = values[row];
        }
        return matrix;
    }

    private List<string> embedding_names() => Model.Embeddings.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();

    private IEnumerable<KeyValuePair<string, Population>> population_items() =>
        Model.Populations
            .SelectMany(item => Population.Flatten([item]))
            .Select(item => KeyValuePair.Create(item.Key, new Population(group, Model, item.Value)));
}

public sealed class Population
{
    private readonly FlowGroup group;
    private readonly FlowSample sample;
    private readonly PopulationResult? model;

    internal Population(FlowGroup group, FlowSample sample, PopulationResult? model)
    {
        this.group = group;
        this.sample = sample;
        this.model = model;
    }

    public PyObject mask
    {
        get
        {
            var values = new bool[sample.EventCount];
            foreach (int index in indices())
                if (index >= 0 && index < values.Length)
                    values[index] = true;
            return PythonArrayConverter.ToNumpy(values);
        }
    }

    public PyObject populations => PythonObjects.Dict(population_items());
    public Strategy strategy => new(resolve_group(), model?.Gate);
    public PyObject population_keys => PythonObjects.List(population_items().Select(item => item.Key));
    public PyObject compensated_matrix => PythonArrayConverter.ToNumpy(PythonArrayConverter.SelectRows(sample.CompensatedEvents, indices()));

    public void set_embedding(string name, PyObject value)
    {
        using (Py.GIL())
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Embedding name cannot be empty.", nameof(name));
            if (string.Equals(name, "Populations", StringComparison.Ordinal))
                throw new ArgumentException("Embedding 'Populations' is generated from gates and cannot be overwritten.", nameof(name));
            
            var selected_indices = indices();
            var input = PythonArrayConverter.ToEmbeddingArray(value);
            if (input.Values.Length != selected_indices.Length && input.Values.Length != sample.EventCount)
                throw new ArgumentException($"Embedding '{name}' expects either {selected_indices.Length} selected values or {sample.EventCount} sample-wide values.");
            
            var existing_matches = sample.Embeddings.TryGetValue(name, out var existing) &&
                existing.Values.Length == sample.EventCount &&
                existing.Kind == input.Kind;
            var values = existing_matches
                ? existing!.Values.ToArray()
                : Enumerable.Repeat(float.NaN, sample.EventCount).ToArray();
            var categories = existing_matches && existing is not null
                ? new Dictionary<int, string>(existing.Categories)
                : new Dictionary<int, string>();
            var input_values = input.Values;
            if (input.Kind == EmbeddingValueKind.Integer)
                input_values = remap_category_values(input.Values, input.Categories, categories);
            
            if (input_values.Length == sample.EventCount)
            {
                foreach (int event_index in selected_indices)
                    if (event_index >= 0 && event_index < values.Length)
                        values[event_index] = input_values[event_index];
            }
            else
            {
                for (int index = 0; index < selected_indices.Length; index++)
                {
                    int event_index = selected_indices[index];
                    if (event_index >= 0 && event_index < values.Length)
                        values[event_index] = input_values[index];
                }
            }
            
            var embedding = new EmbeddingData { Kind = input.Kind, Values = values };
            foreach (var category in categories)
                embedding.Categories[category.Key] = category.Value;
            sample.Embeddings[name] = embedding;
            sample.InvalidateNormalizedChannelCache();
        }
    }

    private static float[] remap_category_values(
        float[] values,
        IReadOnlyDictionary<int, string> input_categories,
        Dictionary<int, string> target_categories)
    {
        var ids_by_label = target_categories.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
        var id_map = new Dictionary<int, int>();
        foreach (var input_category in input_categories)
        {
            if (!ids_by_label.TryGetValue(input_category.Value, out int target_id))
            {
                target_id = target_categories.Count == 0 ? 1 : target_categories.Keys.Max() + 1;
                target_categories[target_id] = input_category.Value;
                ids_by_label[input_category.Value] = target_id;
            }

            id_map[input_category.Key] = target_id;
        }

        var mapped = new float[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
            {
                mapped[index] = float.NaN;
                continue;
            }

            int source_id = Convert.ToInt32(values[index]);
            mapped[index] = id_map.TryGetValue(source_id, out int target_id) ? target_id : float.NaN;
        }

        return mapped;
    }

    public Population __getitem__(string population_key) =>
        population_items().ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal).TryGetValue(population_key, out var population)
            ? population
            : throw new KeyNotFoundException($"Population '{population_key}' was not found.");

    public Population this[string population_key] => __getitem__(population_key);

    internal static PopulationResult? Find(IEnumerable<PopulationResult> populations, GateDefinition gate, PopulationRegion region)
    {
        foreach (var population in populations)
        {
            if (population.Gate == gate && population.Region == region)
                return population;
            var child = Find(population.Children, gate, region);
            if (child is not null)
                return child;
        }
        return null;
    }

    internal static IEnumerable<KeyValuePair<string, PopulationResult>> Flatten(IEnumerable<PopulationResult> populations)
    {
        foreach (var population in populations)
        {
            yield return new KeyValuePair<string, PopulationResult>(KeyFor(population), population);
            foreach (var child in Flatten(population.Children))
                yield return child;
        }
    }

    internal static IEnumerable<string> KeysForGate(GateDefinition gate) =>
        gate.PopulationRegions.Select(region => region == PopulationRegion.Primary ? gate.Name : $"{gate.Name}:{region}");

    internal static PopulationRegion ResolveRegion(GateDefinition gate, string population_key)
    {
        if (string.IsNullOrWhiteSpace(population_key) || population_key is "default" || population_key == gate.Name)
            return PopulationRegion.Primary;
        string region_name = population_key.Contains(':') ? population_key.Split(':').Last() : population_key;
        return Enum.TryParse(region_name, ignoreCase: true, out PopulationRegion region) ? region : PopulationRegion.Primary;
    }

    private static string KeyFor(PopulationResult population) =>
        population.Region == PopulationRegion.Primary ? population.Gate.Name : $"{population.Gate.Name}:{population.Region}";

    private int[] indices() => model?.EventIndices ?? Enumerable.Range(0, sample.EventCount).ToArray();

    private IEnumerable<KeyValuePair<string, Population>> population_items() =>
        (model?.Children ?? sample.Populations)
            .SelectMany(item => Flatten([item]))
            .Select(item => KeyValuePair.Create(item.Key, new Population(group, sample, item.Value)));

    private FlowGroup resolve_group()
    {
        return group;
    }
}

public sealed class StatisticDefinition
{
    private readonly FlowGroup group;
    internal readonly Models.StatisticDefinition Model;

    internal StatisticDefinition(FlowGroup group, Models.StatisticDefinition model)
    {
        this.group = group;
        Model = model;
    }

    public string kind => Model.Kind.ToString();

    public bool is_python() => Model.Kind == StatisticKind.Python;

    public string get_method() => Model.PythonSource;

    public void set_method(string source, string callable_name = "entry", string? display_name = null, PyObject? parameters = null)
    {
        using (Py.GIL())
        {
            PythonExtensionRuntime.EnsureInitialized();
            PythonExtensionRuntime.ValidateStatisticSource(source, callable_name);
            PythonExtensionRuntime.WithGil(() =>
            {
                using (Py.GIL())
                    Model.SetPythonMethod(source, callable_name, display_name, PythonExtensionRuntime.ToJson(parameters));
            });
            group.RecalculateSamples();
        }
    }
}

public sealed class Compensation
{
    internal readonly CompensationMatrix Model;

    internal Compensation(CompensationMatrix model)
    {
        Model = model;
    }

    public string name => Model.Name;
    public PyObject channels => PythonObjects.List(Model.ChannelNames);
    public PyObject matrix => PythonArrayConverter.ToNumpy(Model.Values);
}

internal static class PythonObjects
{
    public static PyObject Dict(IEnumerable<KeyValuePair<string, object?>> values)
    {
        return PythonExtensionRuntime.WithGil(() =>
        {
            var dict = new PyDict();
            foreach (var pair in values)
            {
                using PyObject key = pair.Key.ToPython();
                using PyObject value = object_to_python(pair.Value);
                dict.SetItem(key, value);
            }

            return dict;
        });
    }

    public static PyObject List<T>(IEnumerable<T> values)
    {
        return PythonExtensionRuntime.WithGil(() =>
        {
            var list = new PyList();
            foreach (var value in values)
            {
                using PyObject item = value.ToPython();
                list.Append(item);
            }

            return list;
        });
    }

    public static PyObject Dict<T>(IEnumerable<KeyValuePair<string, T>> values)
    {
        return PythonExtensionRuntime.WithGil(() =>
        {
            var dict = new PyDict();
            foreach (var pair in values)
            {
                using PyObject key = pair.Key.ToPython();
                using PyObject value = pair.Value.ToPython();
                dict.SetItem(key, value);
            }

            return dict;
        });
    }

    private static PyObject object_to_python(object? value) =>
        value switch
        {
            null => Py.Import("builtins").GetAttr("None"),
            string text => text.ToPython(),
            bool boolean => boolean.ToPython(),
            int integer => integer.ToPython(),
            long integer => integer.ToPython(),
            float number => number.ToPython(),
            double number => number.ToPython(),
            decimal number => Convert.ToDouble(number, CultureInfo.InvariantCulture).ToPython(),
            JsonElement element => json_element_to_python(element),
            _ => value.ToPython()
        };

    private static PyObject json_element_to_python(JsonElement element)
    {
        dynamic json = Py.Import("json");
        return json.loads(element.GetRawText());
    }
}
