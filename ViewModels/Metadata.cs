using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using gated.Models;
using gated.Services;

namespace gated.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void refresh_workspace_sample_metadata()
    {
        rebuild_workspace_metadata_table();
        refresh_batch_column_choices();
    }

    private void rebuild_workspace_metadata_table()
    {
        sync_identity_metadata();
        workspace_metadata_table = build_metadata_table(workspace_sample_rows());
        workspace_metadata_table.ColumnChanged += metadata_table_column_changed;
        OnPropertyChanged(nameof(WorkspaceMetadataTable));
        OnPropertyChanged(nameof(WorkspaceMetadataTableView));
    }

    private void metadata_table_column_changed(object sender, DataColumnChangeEventArgs e)
    {
        if (syncing_metadata || e.Column is null || e.Column.ColumnName.StartsWith("__", StringComparison.Ordinal) || e.Column.ColumnName is "Group" or "Sample")
            return;
        syncing_metadata = true;
        try
        {
            commit_metadata_row(e.Row);
            refresh_batch_column_choices();
            invalidate_platforms_from_metadata();
        }
        finally
        {
            syncing_metadata = false;
        }
    }

    private IEnumerable<(FlowGroup Group, FlowSample Sample)> workspace_sample_rows()
    {
        foreach (var group in Workspace.Groups)
        foreach (var sample in group.Samples)
            yield return (group, sample);
    }

    private DataTable build_metadata_table(IEnumerable<(FlowGroup Group, FlowSample Sample)> rows)
    {
        ensure_metadata_schema();
        var table = new DataTable();
        table.Columns.Add("__GroupId", typeof(Guid));
        table.Columns.Add("__SampleId", typeof(Guid));
        table.Columns.Add("Group", typeof(string));
        table.Columns.Add("Sample", typeof(string));
        foreach (var column in Workspace.MetadataColumns
                     .Where(item => item.Key is not ("Group" or "Sample"))
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
            table.Columns.Add(column.Key, type_for_metadata_kind(column.Value));

        foreach (var (group, sample) in rows)
        {
            var row = table.NewRow();
            row["__GroupId"] = group.Id;
            row["__SampleId"] = sample.Id;
            row["Group"] = group.Name;
            row["Sample"] = sample.Name;
            foreach (var column in Workspace.MetadataColumns.Where(column => column.Key is not ("Group" or "Sample")))
            {
                if (!sample.Metadata.TryGetValue(column.Key, out string? value) || string.IsNullOrWhiteSpace(value))
                    row[column.Key] = DBNull.Value;
                else
                    row[column.Key] = parse_metadata_value(value, column.Value) ?? DBNull.Value;
            }
            table.Rows.Add(row);
        }

        return table;
    }

    private void ensure_metadata_schema()
    {
        sync_identity_metadata();
        migrate_fcs_metadata_names();
        Workspace.MetadataColumns["Group"] = MetadataColumnKind.String;
        Workspace.MetadataColumns["Sample"] = MetadataColumnKind.String;
        Workspace.MetadataColumns[Configuration.CytometerMetadataKey] = MetadataColumnKind.String;
        foreach (var key in Workspace.Groups
                     .SelectMany(group => group.Samples)
                     .SelectMany(sample => sample.Metadata.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!Workspace.MetadataColumns.ContainsKey(key))
                Workspace.MetadataColumns[key] = infer_metadata_kind(key);
        }
    }

    private void migrate_fcs_metadata_names()
    {
        foreach (var sample in Workspace.Groups.SelectMany(group => group.Samples))
        {
            foreach (string old_name in sample.Metadata.Keys.Where(key => key.StartsWith('$')).ToArray())
            {
                string display_name = FcsReader.MetadataColumnName(old_name);
                if (!sample.Metadata.ContainsKey(display_name))
                    sample.Metadata[display_name] = sample.Metadata[old_name];
                sample.Metadata.Remove(old_name);
            }
        }

        foreach (string old_name in Workspace.MetadataColumns.Keys.Where(key => key.StartsWith('$')).ToArray())
        {
            string display_name = FcsReader.MetadataColumnName(old_name);
            if (!Workspace.MetadataColumns.ContainsKey(display_name))
                Workspace.MetadataColumns[display_name] = Workspace.MetadataColumns[old_name];
            Workspace.MetadataColumns.Remove(old_name);
            foreach (var integration in Workspace.Platforms.OfType<IntegrationPlatform>()
                         .Where(integration => integration.BatchColumnName == old_name))
                integration.BatchColumnName = display_name;
        }
    }

    private void sync_identity_metadata()
    {
        foreach (var group in Workspace.Groups)
        foreach (var sample in group.Samples)
        {
            sample.Metadata["Group"] = group.Name;
            sample.Metadata["Sample"] = sample.Name;
            sample.Metadata[Configuration.CytometerMetadataKey] = Configuration.CytometerNameForSample(sample);
        }
    }

    private void refresh_batch_column_choices()
    {
        ensure_metadata_schema();
        var metadata_choices = Workspace.MetadataColumns.Keys
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        MetadataColumnChoices.Clear();
        foreach (string choice in metadata_choices)
            MetadataColumnChoices.Add(choice);
    }

    public void CommitWorkspaceSampleMetadata()
    {
        commit_metadata_table(workspace_metadata_table);
        StatusText = "Workspace sample metadata updated";
        refresh_batch_column_choices();
    }

    private void commit_metadata_table(DataTable table)
    {
        if (syncing_metadata)
            return;
        syncing_metadata = true;
        try
        {
            foreach (DataRow row in table.Rows)
                commit_metadata_row(row);
            refresh_batch_column_choices();
            invalidate_platforms_from_metadata();
        }
        finally
        {
            syncing_metadata = false;
        }
    }

    private void commit_metadata_row(DataRow row)
    {
        if (row["__SampleId"] is not Guid sample_id)
            return;
        var sample = Workspace.Groups.SelectMany(group => group.Samples).FirstOrDefault(sample => sample.Id == sample_id);
        if (sample is null)
            return;

        foreach (var column in Workspace.MetadataColumns.Where(column => column.Key is not ("Group" or "Sample")))
        {
            object value = row.Table.Columns.Contains(column.Key) ? row[column.Key] : DBNull.Value;
            if (value == DBNull.Value || value is null || string.IsNullOrWhiteSpace(Convert.ToString(value)))
            {
                if (column.Key == Configuration.CytometerMetadataKey)
                    sample.Metadata[column.Key] = Configuration.DefaultCytometerName;
                else
                    sample.Metadata.Remove(column.Key);
            }
            else
            {
                sample.Metadata[column.Key] = format_metadata_value(value, column.Value);
            }
        }
        sync_identity_metadata();
    }

    private void invalidate_platforms_from_metadata()
    {
        foreach (var platform in Workspace.Platforms.Where(platform => platform.HasIntegrated))
            platform.InvalidateFromConfiguration();
        raise_command_states();
    }

    private async Task add_metadata_column_async(MetadataColumnKind kind)
    {
        if (RequestTextInputAsync is null)
            return;
        string? name = await RequestTextInputAsync($"Add {kind.ToString().ToLowerInvariant()} metadata column", "");
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name.Trim();
        if (name is "Group" or "Sample" || Workspace.MetadataColumns.ContainsKey(name))
            return;
        Workspace.MetadataColumns[name] = kind;
        rebuild_workspace_metadata_table();
        refresh_batch_column_choices();
    }

    private MetadataColumnKind infer_metadata_kind(string key)
    {
        var values = Workspace.Groups.SelectMany(group => group.Samples)
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

    private static Type type_for_metadata_kind(MetadataColumnKind kind) =>
        kind switch
        {
            MetadataColumnKind.Integer => typeof(int),
            MetadataColumnKind.Float => typeof(double),
            _ => typeof(string)
        };

    private static object? parse_metadata_value(string value, MetadataColumnKind kind) =>
        kind switch
        {
            MetadataColumnKind.Integer => int.TryParse(value, out int int_value) ? int_value : null,
            MetadataColumnKind.Float => double.TryParse(value, out double double_value) ? double_value : null,
            _ => value
        };

    private static string format_metadata_value(object value, MetadataColumnKind kind) =>
        kind switch
        {
            MetadataColumnKind.Integer => Convert.ToInt32(value).ToString(),
            MetadataColumnKind.Float => Convert.ToDouble(value).ToString("G17"),
            _ => Convert.ToString(value) ?? ""
        };
}
