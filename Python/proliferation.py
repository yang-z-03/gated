import math

import numpy as np
from scipy import optimize, signal
from scipy.integrate import trapezoid


_BINS = 400
_MAX_FLOWFIT_PEAKS = 20


def _fmt(value):
    if value is None or not np.isfinite(value):
        return ""
    return f"{float(value):.6g}"


def _source_label(row):
    sample = str(row.get("sample", ""))
    population = str(row.get("population", ""))
    return f"{sample} - {population}" if population else sample


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


def _gaussian(x, amplitude, mu, sigma):
    sigma = max(abs(float(sigma)), 1e-9)
    z = (x - float(mu)) / sigma
    return float(amplitude) * np.exp(-0.5 * z * z)


def _histogram(values, x_min, x_max):
    counts, edges = np.histogram(values, bins=_BINS, range=(x_min, x_max))
    centers = edges[:-1] + np.diff(edges) * 0.5
    y = counts.astype(float) / max(float(np.sum(counts)), 1.0)
    return centers, y


def _detect_peaks(x, y, max_generations, prominence):
    distance = max(2, len(x) // max(max_generations * 2, 4))
    threshold = max(prominence * max(float(np.max(y)), 1e-9), 1e-9)
    peaks, properties = signal.find_peaks(y, prominence=threshold, distance=distance)
    if peaks.size == 0:
        peaks = np.array([int(np.argmax(y))], dtype=int)
    return peaks[np.argsort(x[peaks])[::-1]]


def _generation_distance(data_range, log_decades):
    log_decades = max(float(log_decades), 1e-9)
    return float(data_range) * math.log10(2.0) / log_decades


def _estimate_parent_size(x, y, parent_position, distance):
    width = max(distance * 0.45, x[1] - x[0])
    mask = (x >= parent_position - width) & (x <= parent_position + width)
    if np.count_nonzero(mask) < 4 or np.sum(y[mask]) <= 0:
        return max(distance * 0.18, x[1] - x[0])
    weights = y[mask]
    center = np.sum(x[mask] * weights) / np.sum(weights)
    variance = np.sum(((x[mask] - center) ** 2) * weights) / np.sum(weights)
    return max(math.sqrt(max(variance, 0.0)), x[1] - x[0])


def _flowfit_components(x, heights, parent_position, peak_size, generation_distance):
    peak_size = max(abs(float(peak_size)), 1e-9)
    generation_distance = max(abs(float(generation_distance)), 1e-9)
    components = []
    means = []
    for generation, height in enumerate(heights):
        mean = parent_position - generation * generation_distance
        means.append(mean)
        components.append((float(height) ** 2) * np.exp(-((x - mean) ** 2) / (2.0 * peak_size ** 2)))
    model = np.sum(components, axis=0) if components else np.zeros_like(x, dtype=float)
    return np.asarray(means, dtype=float), peak_size, generation_distance, components, model


def _flowfit_initial_parameters(x, y, max_generations, prominence, x_min, x_max):
    data_range = max(x_max - x_min, x[1] - x[0])
    log_decades = max(math.log10(max(data_range, 10.0)), 1.0)
    estimated_distance = _generation_distance(data_range, log_decades)
    peaks = _detect_peaks(x, y, max_generations, prominence)
    max_y = max(float(np.max(y)), 1e-9)
    prominent_peaks = np.asarray([peak for peak in peaks if y[peak] >= max(max_y * prominence, max_y * 0.10)], dtype=int)
    if prominent_peaks.size == 0:
        prominent_peaks = peaks
    parent_index = int(prominent_peaks[np.argmax(x[prominent_peaks])])
    if peaks.size > 1:
        lower_peaks = [int(peak) for peak in prominent_peaks if x[peak] < x[parent_index]]
        if lower_peaks:
            detected_distance = x[parent_index] - x[max(lower_peaks)]
            if detected_distance > x[1] - x[0]:
                estimated_distance = float(detected_distance)
    parent_position = float(x[parent_index])
    parent_size = _estimate_parent_size(x, y, parent_position, estimated_distance)
    real_space = max(parent_position - x_min, estimated_distance)
    number_of_peaks = int(math.ceil(real_space / max(estimated_distance, x[1] - x[0])))
    number_of_peaks = max(1, min(number_of_peaks, max_generations + 1, _MAX_FLOWFIT_PEAKS))
    means = parent_position - estimated_distance * np.arange(number_of_peaks)
    amplitudes = np.sqrt(np.maximum(np.interp(means, x, y, left=0.0, right=0.0), np.max(y) * 0.01))
    return amplitudes, parent_position, parent_size, estimated_distance, log_decades


def _fit_flowfit_generations(x, y, max_generations, prominence, x_min, x_max):
    heights0, parent0, size0, distance0, log_decades = _flowfit_initial_parameters(
        x, y, max_generations, prominence, x_min, x_max
    )
    n = len(heights0)

    def unpack(p):
        heights = p[:n]
        parent_position = p[n]
        peak_size = math.exp(p[n + 1])
        generation_distance = math.exp(p[n + 2])
        return heights, parent_position, peak_size, generation_distance

    def model(p):
        heights, parent_position, peak_size, generation_distance = unpack(p)
        return _flowfit_components(x, heights, parent_position, peak_size, generation_distance)[4]

    def residual(p):
        weight = 1.0 / np.sqrt(np.maximum(y, 1.0 / max(len(y), 1)))
        return (model(p) - y) * weight

    p0 = np.concatenate([heights0, [parent0, math.log(size0), math.log(distance0)]])
    height_upper = max(math.sqrt(max(float(np.max(y)), 1e-9)) * 10.0, 1.0)
    lower = np.concatenate([
        -np.full(n, height_upper),
        [x_min, math.log(max(x[1] - x[0], 1e-9)), math.log(max(x[1] - x[0], 1e-9))],
    ])
    upper = np.concatenate([
        np.full(n, height_upper),
        [x_max, math.log(max(x_max - x_min, x[1] - x[0])), math.log(max(x_max - x_min, x[1] - x[0]))],
    ])
    result = optimize.least_squares(residual, p0, bounds=(lower, upper), max_nfev=100000)
    heights, parent_position, peak_size, generation_distance = unpack(result.x)
    means, peak_size, generation_distance, components, fitted = _flowfit_components(
        x, heights, parent_position, peak_size, generation_distance
    )
    return means, peak_size, generation_distance, components, fitted


def _generation_counts(x, components):
    areas = np.asarray([trapezoid(component, x) for component in components], dtype=float)
    areas = np.maximum(areas, 0.0)
    total = float(np.sum(areas))
    fractions = areas / total if total > 0 else np.zeros_like(areas)
    return areas, fractions


def _main():
    platform.clear_results()
    row_map = platform.row_map
    transformed = np.asarray(platform.transformed, dtype=float)
    if transformed.ndim != 2 or transformed.shape[0] == 0 or transformed.shape[1] == 0:
        platform.set_result_table("proliferation", "Proliferation", ["Sample", "Population", "Events"], [])
        return

    channel = platform.channels[0] if len(platform.channels) else "CFSE"
    major = getattr(platform, "major", channel) or channel
    options = platform.transformations.get(major, None)
    values_for_range = transformed[:, 0][np.isfinite(transformed[:, 0])]
    x_min = float(options.min) if options is not None else float(np.nanmin(values_for_range))
    x_max = float(options.max) if options is not None else float(np.nanmax(values_for_range))
    if not np.isfinite(x_min) or not np.isfinite(x_max) or x_max <= x_min:
        x_min, x_max = float(np.min(values_for_range)), float(np.max(values_for_range))
    if x_max <= x_min:
        x_max = x_min + 1.0

    max_generations = int(platform.parameters.get("max_generations", 8) or 8)
    max_generations = max(1, min(max_generations, 32))
    prominence = float(platform.parameters.get("peak_prominence", 0.03) or 0.03)
    half_window = int(getattr(platform, "smoothing_window", 0) or 0)
    smoothing_enabled = bool(getattr(platform, "enable_smoothing", False))

    summary_rows = []
    generation_rows = []
    for source_id in sorted(int(item) for item in row_map["source_id"].dropna().unique()):
        indices = row_map.index[row_map["source_id"] == source_id].to_numpy(dtype=int)
        if indices.size == 0:
            continue
        first = row_map.loc[indices[0]]
        values = transformed[indices, 0]
        values = values[np.isfinite(values)]
        if values.size < 20:
            continue

        x, y = _histogram(values, x_min, x_max)
        fit_y = _smooth(y, half_window) if smoothing_enabled else y
        means, peak_size, generation_distance, components, model = _fit_flowfit_generations(
            x, fit_y, max_generations, prominence, x_min, x_max
        )
        areas, fractions = _generation_counts(x, components)
        divided_fraction = float(np.sum(fractions[1:])) if fractions.size > 1 else 0.0
        division_index = float(np.sum(np.arange(fractions.size) * fractions))
        precursor = np.asarray([areas[i] / (2.0 ** i) for i in range(areas.size)], dtype=float)
        precursor_total = float(np.sum(precursor))
        proliferation_index = float(np.sum(np.arange(1, areas.size) * areas[1:]) / max(np.sum(areas[1:]), 1e-12)) if areas.size > 1 else 0.0
        replication_index = float(np.sum(areas) / max(precursor_total, 1e-12))
        label = _source_label(first)
        summary_rows.append([
            str(first.get("sample", "")),
            str(first.get("population", "")),
            str(int(values.size)),
            str(int(len(areas))),
            _fmt(divided_fraction * 100.0),
            _fmt(division_index),
            _fmt(proliferation_index),
            _fmt(replication_index),
            _fmt(means[0] if len(means) else np.nan),
            _fmt(generation_distance),
            _fmt(peak_size),
        ])

        for generation, (area, fraction, mean) in enumerate(zip(areas, fractions, means)):
            generation_rows.append([
                str(first.get("sample", "")),
                str(first.get("population", "")),
                str(generation),
                _fmt(mean),
                _fmt(area),
                _fmt(fraction * 100.0),
                _fmt(area / (2.0 ** generation)),
            ])
            platform.set_plot_series(
                f"generation_{generation}_{source_id}",
                f"{label} generation {generation}",
                x.tolist(),
                components[generation].tolist(),
                major,
                "Normalized frequency",
            )
        platform.set_plot_series(f"fit_{source_id}", f"{label} fit", x.tolist(), model.tolist(), major, "Normalized frequency")

    platform.set_result_table(
        "proliferation",
        "Proliferation summary",
        ["Sample", "Population", "Events", "Generations", "Divided %", "Division index", "Proliferation index", "Replication index", "Parent M", "Distance D", "Peak size S"],
        summary_rows,
    )
    platform.set_result_table(
        "proliferation_generations",
        "Generation fractions",
        ["Sample", "Population", "Generation", "Mean", "Area", "Fraction %", "Precursor frequency"],
        generation_rows,
    )
    platform.set_statistic("Fitted populations", len(summary_rows))
    platform.set_statistic("Maximum generations", max_generations)


_main()
