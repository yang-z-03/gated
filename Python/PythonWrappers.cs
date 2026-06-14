using System;
using System.Collections.Generic;
using System.Linq;
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
    public PyObject integration_jobs => PythonObjects.List(Model.IntegrationJobs.Select(job => new IntegrationJob(Model, job)));

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

    public IntegrationJob integration_job(string name) =>
        Model.IntegrationJobs
            .Select(job => new IntegrationJob(Model, job))
            .FirstOrDefault(job => job.name == name)
        ?? throw new KeyNotFoundException($"Integration job '{name}' was not found.");

    public void apply_metadata(PyObject dataframe)
    {
        PythonExtensionRuntime.WithGil(() => {
            dynamic pandas = Py.Import("pandas");
            using PyObject frame = pandas.DataFrame(dataframe);
            var metadata_columns = new PyList(frame.GetAttr("columns").InvokeMethod("tolist"))
                .Select(column => column.As<string>() ?? "")
                .Where(column => column.Length > 0 && column is not ("Group" or "Sample"))
                .ToArray();
            Model.MetadataColumns.Clear();
            Model.MetadataColumns["Group"] = MetadataColumnKind.String;
            Model.MetadataColumns["Sample"] = MetadataColumnKind.String;
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
                             .Where(key => key is not ("Group" or "Sample"))
                             .Except(metadata_columns, StringComparer.Ordinal)
                             .ToArray())
                    sample.Metadata.Remove(existing_key);
                foreach (string column in metadata_columns)
                {
                    if (!row.TryGetValue(column, out var value) || pandas.isna(value).As<bool>())
                        continue;
                    sample.Metadata[column] = python_metadata_value_to_string(value!, Model.MetadataColumns[column]);
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
        }
        Model.MetadataColumns["Group"] = MetadataColumnKind.String;
        Model.MetadataColumns["Sample"] = MetadataColumnKind.String;
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

    private static string python_metadata_value_to_string(PyObject value, MetadataColumnKind kind) =>
        kind switch
        {
            MetadataColumnKind.Integer => Convert.ToInt32(value.As<double>()).ToString(),
            MetadataColumnKind.Float => value.As<double>().ToString("G17"),
            _ => value.As<string>() ?? value.ToString() ?? ""
        };

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

public sealed class IntegrationJob
{
    private readonly FlowWorkspace workspace;
    internal readonly Models.IntegrationJob Model;

    internal IntegrationJob(FlowWorkspace workspace, Models.IntegrationJob model)
    {
        this.workspace = workspace;
        Model = model;
    }

    public string name => Model.Name;
    public string batch_column => Model.BatchColumnName;
    public PyObject features => PythonObjects.List(Model.SelectedFeatureNames);
    public PyObject batch_ids => PythonArrayConverter.ToNumpy(Model.BatchIds);
    public bool has_integrated_matrix => Model.CurrentMatrix is not null;
    public PyObject matrix => PythonArrayConverter.ToNumpy(Model.SourceData ?? new float[0, 0]);
    public PyObject logicle_matrix => PythonArrayConverter.ToNumpy(Model.LogicleNormalized ?? new float[0, 0]);
    public PyObject integrated_matrix => PythonArrayConverter.ToNumpy(Model.CurrentMatrix ?? new float[0, 0]);
    public PyObject row_source_ids => PythonArrayConverter.ToNumpy(Model.RowMap.SourceIds);
    public PyObject row_event_indices => PythonArrayConverter.ToNumpy(Model.RowMap.EventIndices);

    public PyObject row_map
    {
        get
        {
            return PythonExtensionRuntime.WithGil(() =>
            {
                var dict = new PyDict();
                using PyObject sources = row_sources();
                using PyObject source_ids = row_source_ids;
                using PyObject event_indices = row_event_indices;
                dict.SetItem("sources", sources);
                dict.SetItem("source_ids", source_ids);
                dict.SetItem("event_indices", event_indices);
                return dict;
            });
        }
    }

    public PyObject row_sources()
    {
        return PythonExtensionRuntime.WithGil(() =>
        {
            var rows = new PyList();
            for (int source_id = 0; source_id < Model.RowMap.Sources.Count; source_id++)
            {
                var source = Model.RowMap.Sources[source_id];
                var group = workspace.Groups.FirstOrDefault(item => item.Id == source.GroupId);
                var sample = find_sample(source.SampleId);
                var dict = new PyDict();
                using PyObject py_source_id = source_id.ToPython();
                using PyObject py_group = (group?.Name ?? "").ToPython();
                using PyObject py_sample = (sample?.Name ?? "").ToPython();
                using PyObject py_group_id = source.GroupId.ToString().ToPython();
                using PyObject py_sample_id = source.SampleId.ToString().ToPython();
                using PyObject py_gate_id = source.GateId.ToString().ToPython();
                using PyObject py_region = source.Region.ToString().ToPython();
                dict.SetItem("source_id", py_source_id);
                dict.SetItem("group", py_group);
                dict.SetItem("sample", py_sample);
                dict.SetItem("group_id", py_group_id);
                dict.SetItem("sample_id", py_sample_id);
                dict.SetItem("gate_id", py_gate_id);
                dict.SetItem("region", py_region);
                rows.Append(dict);
            }

            dynamic pandas = Py.Import("pandas");
            return pandas.DataFrame(rows);
        });
    }

    public PyObject row_map_frame()
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
                var dict = new PyDict();
                using PyObject py_group = (group?.Name ?? "").ToPython();
                using PyObject py_sample = (sample?.Name ?? "").ToPython();
                using PyObject py_sample_id = (source?.SampleId.ToString() ?? "").ToPython();
                using PyObject py_gate_id = (source?.GateId.ToString() ?? "").ToPython();
                using PyObject py_region = (source?.Region.ToString() ?? "").ToPython();
                using PyObject py_event_index = Model.RowMap.EventIndices[row].ToPython();
                dict.SetItem("group", py_group);
                dict.SetItem("sample", py_sample);
                dict.SetItem("sample_id", py_sample_id);
                dict.SetItem("gate_id", py_gate_id);
                dict.SetItem("region", py_region);
                dict.SetItem("event_index", py_event_index);
                rows.Append(dict);
            }

            dynamic pandas = Py.Import("pandas");
            return pandas.DataFrame(rows);
        });
    }

    public void set_embedding(string name, PyObject value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Embedding name cannot be empty.", nameof(name));
        if (string.Equals(name, "Populations", StringComparison.Ordinal))
            throw new ArgumentException("Embedding 'Populations' is generated from gates and cannot be overwritten.", nameof(name));

        var input = PythonArrayConverter.ToEmbeddingArray(value);
        if (input.Values.Length != Model.RowMap.Count)
            throw new ArgumentException($"Embedding '{name}' expects {Model.RowMap.Count} values, one for each integration job row.");

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
                remapped_by_sample[sample_id] = remap_category_values(input.Values, input.Categories, sample_categories[sample_id]);
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

    private FlowSample? find_sample(Guid sample_id) =>
        workspace.Groups.SelectMany(group => group.Samples).FirstOrDefault(sample => sample.Id == sample_id);

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
        PythonExtensionRuntime.EnsureInitialized();
        return PythonExtensionRuntime.WithGil(() =>
        {
            var channel_names = PythonArrayConverter.To<string>(new PyList(channels));
            var values = PythonArrayConverter.ToFloatMatrix(matrix);
            if (values.GetLength(0) != channel_names.Count || values.GetLength(1) != channel_names.Count)
                throw new ArgumentException("Compensation matrix dimensions must match the channel list.");

            var compensation = CompensationMatrix.Create(key, channel_names, values);
            return new Compensation(Model.RegisterCompensation(compensation, make_applied_if_first: false));
        });
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
        PythonExtensionRuntime.ValidateStatisticSource(source, callable_name);
        var definition = new Models.StatisticDefinition();
        definition.SetPythonMethod(source, callable_name, display_name, PythonExtensionRuntime.ToJson(parameters));
        definitions().Add(definition);
        group.RecalculateSamples();
        return new StatisticDefinition(group, definition);
    }

    public Strategy define_gate_polygon(string name, string population_key, string channel1, string channel2, PyObject vertices) =>
        add_gate(name, GateKind.Polygon, population_key, channel1, channel2, PythonArrayConverter.ToFloatMatrix(vertices));

    public Strategy define_gate_rectangle(string name, string population_key, string channel1, string channel2, PyObject rectangle) =>
        add_gate(name, GateKind.Rectangle, population_key, channel1, channel2, PythonArrayConverter.ToFloatMatrix(rectangle));

    public Strategy define_gate_quadrant(string name, string population_key, string channel1, string channel2, PyObject center) =>
        add_gate(name, GateKind.Quadrant, population_key, channel1, channel2, PythonArrayConverter.ToFloatMatrix(center));

    public Strategy define_gate_curly(string name, string population_key, string channel1, string channel2, PyObject center) =>
        add_gate(name, GateKind.CurlyQuadrant, population_key, channel1, channel2, PythonArrayConverter.ToFloatMatrix(center));

    public Strategy define_gate_offset(string name, string population_key, string channel1, string channel2, PyObject positions) =>
        add_gate(name, GateKind.OffsetQuadrant, population_key, channel1, channel2, PythonArrayConverter.ToFloatMatrix(positions));

    public Strategy define_gate_threshold(string name, string population_key, string channel1, PyObject position) =>
        add_gate(name, GateKind.Threshold, population_key, channel1, null, PythonArrayConverter.ToFloatMatrix(position));

    public Strategy define_gate_range(string name, string population_key, string channel1, PyObject positions) =>
        add_gate(name, GateKind.Range, population_key, channel1, null, PythonArrayConverter.ToFloatMatrix(positions));

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
        PythonExtensionRuntime.EnsureInitialized();
        PythonExtensionRuntime.ValidateStatisticSource(source, callable_name);
        PythonExtensionRuntime.WithGil(() =>
            Model.SetPythonMethod(source, callable_name, display_name, PythonExtensionRuntime.ToJson(parameters)));
        group.RecalculateSamples();
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
}
