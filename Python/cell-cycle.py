import math

import numpy as np
from scipy import optimize, stats


_BINS = 400


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


def _histogram(values, x_min, x_max):
    counts, edges = np.histogram(values, bins=_BINS, range=(x_min, x_max))
    centers = edges[:-1] + np.diff(edges) * 0.5
    y = counts.astype(float) / max(float(np.sum(counts)), 1.0)
    return centers, y, counts


def _gaussian(x, amplitude, mu, sigma):
    sigma = max(abs(float(sigma)), 1e-9)
    z = (x - float(mu)) / sigma
    return float(amplitude) * np.exp(-0.5 * z * z)


def _normal_cdf(z):
    return stats.norm.cdf(z)


def _estimate_sigma_at_fraction(x, y, peak_index, side, fraction=0.60):
    peak = y[peak_index]
    target = peak * fraction
    if peak <= 0:
        return max((x[-1] - x[0]) / 20.0, 1e-6)
    if side < 0:
        search = range(peak_index, 0, -1)
    else:
        search = range(peak_index, len(y) - 1)
    crossing = None
    for index in search:
        left = index - 1 if side < 0 else index
        right = index if side < 0 else index + 1
        y0, y1 = y[left], y[right]
        if (y0 - target) * (y1 - target) <= 0 and y0 != y1:
            t = (target - y0) / (y1 - y0)
            crossing = x[left] + t * (x[right] - x[left])
            break
    if crossing is None:
        return max((x[-1] - x[0]) / 20.0, 1e-6)
    half_width = abs(x[peak_index] - crossing)
    return max(half_width / math.sqrt(-2.0 * math.log(fraction)), 1e-6)


def _fit_gaussian_flank(x, y, peak_index, flank):
    mu0 = float(x[peak_index])
    sigma0 = _estimate_sigma_at_fraction(x, y, peak_index, -1 if flank == "left" else 1)
    amp0 = max(float(y[peak_index]), 1e-9)
    if flank == "left":
        mask = (x >= mu0 - 3.0 * sigma0) & (x <= mu0 + 1.0 * sigma0)
    else:
        mask = (x >= mu0 - 1.0 * sigma0) & (x <= mu0 + 3.0 * sigma0)
    if np.count_nonzero(mask) < 6:
        mask = np.ones_like(x, dtype=bool)

    def residual(p):
        amp, mu, log_sigma = p
        return _gaussian(x[mask], amp, mu, math.exp(log_sigma)) - y[mask]

    result = optimize.least_squares(
        residual,
        np.array([amp0, mu0, math.log(sigma0)], dtype=float),
        bounds=([0.0, x[0], math.log(max((x[1] - x[0]) * 0.25, 1e-9))],
                [max(amp0 * 5.0, 1.0), x[-1], math.log(max(x[-1] - x[0], 1e-6))]),
        max_nfev=1000,
    )
    amp, mu, log_sigma = result.x
    return float(amp), float(mu), float(math.exp(log_sigma))


def _initial_peaks(x, y):
    g1_index = int(np.argmax(y))
    start = int(np.searchsorted(x, max(x[g1_index] * 1.75, x[g1_index] + (x[-1] - x[0]) * 0.20)))
    if start >= len(y) - 5:
        start = min(len(y) - 1, g1_index + max(5, len(y) // 4))
    g2_index = start + int(np.argmax(y[start:])) if start < len(y) else min(len(y) - 1, g1_index * 2)
    if g2_index <= g1_index:
        g2_index = min(len(y) - 1, g1_index + max(5, len(y) // 4))
    return g1_index, g2_index


def _watson_fit(x, y):
    g1_index, g2_index = _initial_peaks(x, y)
    g1_amp, g1_mu, g1_sigma = _fit_gaussian_flank(x, y, g1_index, "left")
    g2_amp, g2_mu, g2_sigma = _fit_gaussian_flank(x, y, g2_index, "right")
    if g2_mu <= g1_mu:
        g2_mu = min(x[-1], g1_mu * 2.0)

    g1 = _gaussian(x, g1_amp, g1_mu, g1_sigma)
    g2 = _gaussian(x, g2_amp, g2_mu, g2_sigma)
    left = _normal_cdf((x - g1_mu) / max(g1_sigma, 1e-9))
    right = _normal_cdf((g2_mu - x) / max(g2_sigma, 1e-9))
    s_probability = np.clip(left * right, 0.0, 1.0)
    s_probability[(x < g1_mu) | (x > g2_mu)] = 0.0
    s_phase = np.maximum(y - g1 - g2, 0.0) * s_probability
    model = g1 + g2 + s_phase
    return {
        "model": model,
        "g1": g1,
        "s": s_phase,
        "g2": g2,
        "params": (g1_amp, g1_mu, g1_sigma, g2_amp, g2_mu, g2_sigma),
    }


def _djf_fit(x, y, synchronous):
    g1_index, g2_index = _initial_peaks(x, y)
    g1_sigma = _estimate_sigma_at_fraction(x, y, g1_index, -1)
    g2_sigma = _estimate_sigma_at_fraction(x, y, g2_index, 1)
    width = max(x[g2_index] - x[g1_index], x[-1] - x[0], 1e-6)
    x_scale = max(width, 1e-6)

    def components(p):
        g1_amp, g1_mu, log_g1_sigma, g2_amp, g2_mu, log_g2_sigma, a, b, c = p[:9]
        g1_sigma_v = math.exp(log_g1_sigma)
        g2_sigma_v = math.exp(log_g2_sigma)
        t = (x - g1_mu) / max(g2_mu - g1_mu, 1e-6)
        gate = ((x >= g1_mu) & (x <= g2_mu)).astype(float)
        s = np.maximum(a + b * t + c * t * t, 0.0) * gate
        if synchronous:
            bump_amp, bump_mu_t, log_bump_sigma_t = p[9:12]
            bump_sigma = math.exp(log_bump_sigma_t)
            s += _gaussian(t, bump_amp, bump_mu_t, bump_sigma) * gate
        g1 = _gaussian(x, g1_amp, g1_mu, g1_sigma_v)
        g2 = _gaussian(x, g2_amp, g2_mu, g2_sigma_v)
        return g1, s, g2

    def residual(p):
        g1, s, g2 = components(p)
        weight = 1.0 / np.sqrt(np.maximum(y, 1.0 / max(len(y), 1)))
        return (g1 + s + g2 - y) * weight

    p0 = [
        max(y[g1_index], 1e-9),
        x[g1_index],
        math.log(max(g1_sigma, 1e-6)),
        max(y[g2_index], 1e-9),
        x[g2_index],
        math.log(max(g2_sigma, 1e-6)),
        max(float(np.median(y)), 1e-9),
        0.0,
        0.0,
    ]
    lower = [0.0, x[0], math.log(max((x[1] - x[0]) * 0.25, 1e-9)), 0.0, x[0], math.log(max((x[1] - x[0]) * 0.25, 1e-9)), 0.0, -1.0, -1.0]
    upper = [1.0, x[-1], math.log(x_scale), 1.0, x[-1], math.log(x_scale), 1.0, 1.0, 1.0]
    if synchronous:
        p0 += [max(float(np.max(y)) * 0.25, 1e-9), 0.5, math.log(0.18)]
        lower += [0.0, 0.0, math.log(0.02)]
        upper += [1.0, 1.0, math.log(1.0)]
    result = optimize.least_squares(residual, np.asarray(p0), bounds=(lower, upper), max_nfev=3000)
    g1, s, g2 = components(result.x)
    return {"model": g1 + s + g2, "g1": g1, "s": s, "g2": g2, "params": result.x}


def _phase_percentages(g1, s, g2):
    values = np.array([np.sum(g1), np.sum(s), np.sum(g2)], dtype=float)
    total = float(np.sum(values))
    if total <= 0:
        return 0.0, 0.0, 0.0
    return tuple(float(v / total * 100.0) for v in values)


def _main():
    platform.clear_results()
    row_map = platform.row_map
    transformed = np.asarray(platform.transformed, dtype=float)
    if transformed.ndim != 2 or transformed.shape[0] == 0 or transformed.shape[1] == 0:
        platform.set_result_table("cell_cycle", "Cell cycle", ["Sample", "Population", "Events"], [])
        return

    channel = platform.channels[0] if len(platform.channels) else "DNA"
    major = getattr(platform, "major", channel) or channel
    options = platform.transformations.get(major, None)
    values_for_range = transformed[:, 0][np.isfinite(transformed[:, 0])]
    x_min = float(options.min) if options is not None else float(np.nanmin(values_for_range))
    x_max = float(options.max) if options is not None else float(np.nanmax(values_for_range))
    if not np.isfinite(x_min) or not np.isfinite(x_max) or x_max <= x_min:
        x_min, x_max = float(np.min(values_for_range)), float(np.max(values_for_range))
    if x_max <= x_min:
        x_max = x_min + 1.0

    half_window = int(getattr(platform, "smoothing_window", 0) or 0)
    smoothing_enabled = bool(getattr(platform, "enable_smoothing", False))
    model_name = str(platform.parameters.get("model", "WatsonPragmatic") or "WatsonPragmatic")
    use_djf = model_name.lower().startswith("dean")
    synchronous = model_name.lower().endswith("fox")

    rows = []
    for source_id in sorted(int(item) for item in row_map["source_id"].dropna().unique()):
        indices = row_map.index[row_map["source_id"] == source_id].to_numpy(dtype=int)
        if indices.size == 0:
            continue
        first = row_map.loc[indices[0]]
        values = transformed[indices, 0]
        values = values[np.isfinite(values)]
        if values.size < 20:
            continue
        x, y, counts = _histogram(values, x_min, x_max)
        fit_y = _smooth(y, half_window) if smoothing_enabled else y
        fit = _djf_fit(x, fit_y, synchronous) if use_djf else _watson_fit(x, fit_y)
        g1_pct, s_pct, g2_pct = _phase_percentages(fit["g1"], fit["s"], fit["g2"])
        g1_amp, g1_mu, g1_sigma = fit["params"][0], fit["params"][1], math.exp(fit["params"][2]) if use_djf else fit["params"][2]
        g2_amp, g2_mu, g2_sigma = fit["params"][3], fit["params"][4], math.exp(fit["params"][5]) if use_djf else fit["params"][5]
        rows.append([
            str(first.get("sample", "")),
            str(first.get("population", "")),
            str(int(values.size)),
            _fmt(g1_pct),
            _fmt(s_pct),
            _fmt(g2_pct),
            _fmt(g1_mu),
            _fmt(g1_sigma / max(abs(g1_mu), 1e-9) * 100.0),
            _fmt(g2_mu),
            _fmt(g2_sigma / max(abs(g2_mu), 1e-9) * 100.0),
            _fmt(g2_mu / max(g1_mu, 1e-9)),
        ])
        label = _source_label(first)
        platform.set_plot_series(f"fit_{source_id}", f"{label} fit", x.tolist(), fit["model"].tolist(), major, "Normalized frequency")
        platform.set_plot_series(f"component_g1_{source_id}", f"{label} G1", x.tolist(), fit["g1"].tolist(), major, "Normalized frequency")
        platform.set_plot_series(f"component_s_{source_id}", f"{label} S", x.tolist(), fit["s"].tolist(), major, "Normalized frequency")
        platform.set_plot_series(f"component_g2m_{source_id}", f"{label} G2/M", x.tolist(), fit["g2"].tolist(), major, "Normalized frequency")

    columns = ["Sample", "Population", "Events", "G1 %", "S %", "G2/M %", "G1 mean", "G1 CV %", "G2/M mean", "G2/M CV %", "G2/G1 ratio"]
    platform.set_result_table("cell_cycle", "Cell cycle", columns, rows)
    platform.set_statistic("Fitted populations", len(rows))
    platform.set_statistic("Cell cycle model", model_name)


_main()
