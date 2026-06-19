# Embedded Platform Python API

Platform scripts are developer-maintained Avalonia resources. The current Python
surface intentionally mirrors the C# model names and does not keep compatibility
aliases from the previous integration-job API.

## Workspace

- `workspace.platforms`: `dict[str, Platform]` keyed by platform name.

## Platform

- `name`: platform name.
- `populations`: selected platform population descriptors.
- `channels`: selected channel or embedding names.
- `matrix`: raw matrix.
- `compensated`: compensated matrix.
- `transform`: one of `linear`, `logarithm`, or `logicle`.
- `transformations`: `dict[str, ViewOptions]` keyed by channel or embedding name.
- `transformed`: transformed matrix.
- `series`: keyed output/display series.
- `models`: keyed overall/aggregate models.
- `components`: keyed component model lists.
- `result`: keyed data-table results.
- `parameters`: `dict[str, object]` for platform-specific settings.
- `row_map`: pandas DataFrame containing row metadata. Build metadata tables from this field.
- `has_graphics`, `has_data_table`: layout placement capability flags.

`ViewOptions` fields are `min`, `max`, `t`, `w`, `m`, and `a`.

## Platform Subclasses

- `UnivariatePlatform`: adds `major`, `histogram`, `smoothed`, `smoothing_window`, and `enable_smoothing`.
- `BivariatePlatform`: adds `major`, `minor`, `trend`, `binned`, `smoothed`, `smoothing_window`, and `enable_smoothing`.
- `MultivariatePlatform`: adds `normalized`.

## Outputs

Boundary methods remain available for embedded scripts:

- `clear_results()`
- `set_result_table(key, title, columns, rows)`
- `set_plot_series(key, title, x, y, x_label="", y_label="")`
- `set_fit_curve(key, title, kind, source_id, parameters, normalizer=1.0, x_label="", y_label="")`
- `add_component_gamma(key, alpha, beta, amplitude)`
- `add_component_normal(key, mu, sigma, amplitude)`
- `add_component_exponential(key, slope, expn, intercept)`
- `set_fit_addition(key, models, weights, intercept=0)`
- `set_statistic(name, value)`

All C# wrapper methods that accept `PyObject` acquire the Python GIL before reading Python objects.
