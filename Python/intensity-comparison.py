
import math
from scipy import stats
import numpy as np

def _fmt(value):
    if value is None or not np.isfinite(value):
        return ""
    return f"{float(value):.6g}"


def _fmt_fold(value):
    if value is None or not np.isfinite(value):
        return ""
    return f"{float(value):.4g}"


def _fmt_p(value):
    if value is None or not np.isfinite(value):
        return ""
    value = float(value)
    if value < 0.0001:
        exponent = int(math.floor(math.log10(max(value, np.finfo(float).tiny))))
        return f"(< 1 e {exponent})"
    return f"{value:.4f}"


def _geometric_mean(values):
    values = np.asarray(values, dtype=float)
    values = values[np.isfinite(values) & (values > 0)]
    if values.size == 0:
        return np.nan
    return float(np.exp(np.mean(np.log(values))))


def _smooth(values, half_window):
    values = np.asarray(values, dtype=float)
    half_window = int(max(0, half_window))
    if half_window <= 0 or values.size == 0:
        return values
    out = np.empty_like(values)
    for index in range(values.size):
        start = max(0, index - half_window)
        stop = min(values.size, index + half_window + 1)
        out[index] = np.mean(values[start:stop])
    return out


def _normality_p(values):
    values = np.asarray(values, dtype=float)
    values = values[np.isfinite(values)]
    if values.size < 8:
        return np.nan
    return float(stats.normaltest(values).pvalue)


def _ztest_p(values, reference):
    values = np.asarray(values, dtype=float)
    reference = np.asarray(reference, dtype=float)
    values = values[np.isfinite(values)]
    reference = reference[np.isfinite(reference)]
    if values.size < 2 or reference.size < 2:
        return np.nan
    variance = np.var(values, ddof=1) / values.size + np.var(reference, ddof=1) / reference.size
    if variance <= 0:
        return np.nan
    z_value = (np.mean(values) - np.mean(reference)) / math.sqrt(variance)
    return float(2.0 * stats.norm.sf(abs(z_value)))


def _mannwhitney_p(values, reference):
    values = np.asarray(values, dtype=float)
    reference = np.asarray(reference, dtype=float)
    values = values[np.isfinite(values)]
    reference = reference[np.isfinite(reference)]
    if values.size == 0 or reference.size == 0:
        return np.nan
    return float(stats.mannwhitneyu(values, reference, alternative="two-sided").pvalue)


def _ks_p(values, reference):
    values = np.asarray(values, dtype=float)
    reference = np.asarray(reference, dtype=float)
    values = values[np.isfinite(values)]
    reference = reference[np.isfinite(reference)]
    if values.size == 0 or reference.size == 0:
        return np.nan
    return float(stats.ks_2samp(values, reference, alternative="two-sided").pvalue)


def _chisquare_p(values, reference, minimum, maximum):
    values = np.asarray(values, dtype=float)
    reference = np.asarray(reference, dtype=float)
    values = values[np.isfinite(values)]
    reference = reference[np.isfinite(reference)]
    if values.size == 0 or reference.size == 0 or maximum <= minimum:
        return np.nan
    observed, _ = np.histogram(values, bins=64, range=(minimum, maximum))
    expected, _ = np.histogram(reference, bins=64, range=(minimum, maximum))
    observed = observed.astype(float)
    expected = expected.astype(float)
    keep = (observed + expected) > 0
    observed = observed[keep]
    expected = expected[keep]
    if observed.size < 2:
        return np.nan
    expected = expected + 1e-9
    expected *= max(observed.sum(), 1.0) / max(expected.sum(), 1.0)
    return float(stats.chisquare(observed, expected).pvalue)


def _source_label(row):
    sample = str(row.get("sample", ""))
    population = str(row.get("population", ""))
    return f"{sample} - {population}" if population else sample


def _main():
    
    platform.clear_results()

    row_map = platform.row_map
    transformed = np.asarray(platform.transformed, dtype=float)
    compensated = np.asarray(platform.compensated, dtype=float)
    if transformed.ndim != 2 or transformed.shape[0] == 0 or transformed.shape[1] == 0:
        platform.set_result_table(
            "intensity_comparison",
            "Intensity comparison",
            ["Sample", "Population", "Events"],
            [],
        )
        return

    channel = platform.channels[0] if len(platform.channels) else "Intensity"
    major = getattr(platform, "major", channel) or channel
    options = platform.transformations.get(major, None)
    x_min = float(options.min) if options is not None else float(np.nanmin(transformed[:, 0]))
    x_max = float(options.max) if options is not None else float(np.nanmax(transformed[:, 0]))
    values_for_range = transformed[:, 0][np.isfinite(transformed[:, 0])]
    if values_for_range.size and (not np.isfinite(x_min) or not np.isfinite(x_max) or x_max <= x_min):
        x_min = float(np.min(values_for_range))
        x_max = float(np.max(values_for_range))
    if not np.isfinite(x_min) or not np.isfinite(x_max) or x_max <= x_min:
        x_min, x_max = 0.0, 1.0

    source_ids = sorted(int(item) for item in row_map["source_id"].dropna().unique())
    sources = []
    for source_id in source_ids:
        rows = row_map.index[row_map["source_id"] == source_id].to_numpy(dtype=int)
        if rows.size == 0:
            continue
        first = row_map.loc[rows[0]]
        values = transformed[rows, 0]
        raw_values = compensated[rows, 0] if compensated.ndim == 2 and compensated.shape[0] == transformed.shape[0] else values
        values = values[np.isfinite(values)]
        raw_values = raw_values[np.isfinite(raw_values)]
        if values.size == 0:
            continue
        sources.append(
            {
                "source_id": source_id,
                "sample": str(first.get("sample", "")),
                "population": str(first.get("population", "")),
                "label": _source_label(first),
                "values": values,
                "raw_values": raw_values,
            }
        )

    if not sources:
        platform.set_result_table(
            "intensity_comparison",
            "Intensity comparison",
            ["Sample", "Population", "Events"],
            [],
        )
        return

    requested_reference = str(platform.parameters.get("reference_sample", "") or "")
    reference = next((item for item in sources if item["label"] == requested_reference), None)
    if reference is None:
        reference = sources[0]
    reference_values = reference["values"]

    columns = [
        "Sample",
        "Population",
        "Events",
        "Mean",
        "Median",
        "Geometric mean",
        "Mean fold",
        "Median fold",
        "Geomean fold",
        "Chi-square p",
        "Normality p",
        "Z-test p",
        "Mann-Whitney p",
        "KS p",
    ]

    rows = []
    reference_mean = float(np.mean(reference_values))
    reference_median = float(np.median(reference_values))
    reference_geomean = _geometric_mean(reference["raw_values"])
    for item in sources:
        values = item["values"]
        raw_values = item["raw_values"]
        mean = float(np.mean(values))
        median = float(np.median(values))
        geomean = _geometric_mean(raw_values)
        is_reference = item["source_id"] == reference["source_id"]
        rows.append(
            [
                item["sample"],
                item["population"],
                str(int(values.size)),
                _fmt(mean),
                _fmt(median),
                _fmt(geomean),
                "1" if is_reference else _fmt_fold(mean / reference_mean if reference_mean else np.nan),
                "1" if is_reference else _fmt_fold(median / reference_median if reference_median else np.nan),
                "1" if is_reference else _fmt_fold(geomean / reference_geomean if reference_geomean else np.nan),
                "-" if is_reference else _fmt_p(_chisquare_p(values, reference_values, x_min, x_max)),
                _fmt_p(_normality_p(values)),
                "-" if is_reference else _fmt_p(_ztest_p(values, reference_values)),
                "-" if is_reference else _fmt_p(_mannwhitney_p(values, reference_values)),
                "-" if is_reference else _fmt_p(_ks_p(values, reference_values)),
            ]
        )

    platform.set_result_table("intensity_comparison", "Intensity comparison", columns, rows)

    bins = 400
    centers = np.linspace(x_min, x_max, bins, endpoint=False) + (x_max - x_min) / (2.0 * bins)
    half_window = int(getattr(platform, "smoothing_window", 0) or 0)
    smoothing_enabled = bool(getattr(platform, "enable_smoothing", False))
    for item in sources:
        counts, _ = np.histogram(item["values"], bins=bins, range=(x_min, x_max))
        frequency = counts.astype(float) / max(float(np.sum(counts)), 1.0)
        if smoothing_enabled:
            frequency = _smooth(frequency, half_window)
        platform.set_plot_series(
            f"source_{item['source_id']}",
            item["label"],
            centers.tolist(),
            frequency.tolist(),
            major,
            "Frequency",
        )

    platform.set_statistic("Reference", reference["label"])
    platform.set_statistic("Compared populations", len(sources))


_main()
