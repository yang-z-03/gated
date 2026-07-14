import json
import numpy as np
from scipy import optimize


def _robust_line(x, y):
    x = np.asarray(x, dtype=float)
    y = np.asarray(y, dtype=float)
    good = np.isfinite(x) & np.isfinite(y)
    x, y = x[good], y[good]
    if x.size < 3 or np.ptp(x) <= np.finfo(float).eps:
        raise ValueError("Insufficient variation for robust spectral regression")
    design = np.column_stack((np.ones(x.size), x))
    initial, *_ = np.linalg.lstsq(design, y, rcond=None)
    try:
        fit = optimize.least_squares(
            lambda p: design @ p - y,
            initial,
            loss="huber",
            max_nfev=100,
        )
        if fit.success and np.all(np.isfinite(fit.x)):
            return float(fit.x[1])
    except Exception:
        pass
    return float(initial[1])


def _fit():
    positives = [np.asarray(item, dtype=float) for item in _positive_matrices]
    unstained = np.asarray(_unstained_matrix, dtype=float)
    peaks = np.asarray(_peak_indices, dtype=int)
    if unstained.ndim != 2 or unstained.shape[0] < 3:
        raise ValueError("At least three gated Unstained/AF events are required")
    detector_count = unstained.shape[1]
    signatures = []
    for positive, peak in zip(positives, peaks):
        if positive.ndim != 2 or positive.shape[1] != detector_count or positive.shape[0] < 3:
            raise ValueError("Every stained control requires at least three selected events")
        if peak < 0 or peak >= detector_count:
            raise ValueError("Peak detector index is outside the spectral detector set")
        combined = np.vstack((unstained, positive))
        x = combined[:, peak]
        signature = np.empty(detector_count, dtype=float)
        for detector in range(detector_count):
            signature[detector] = 1.0 if detector == peak else _robust_line(x, combined[:, detector])
        maximum = np.max(signature)
        if not np.isfinite(maximum) or maximum <= np.finfo(float).eps:
            raise ValueError("A fluorophore signature is degenerate")
        signatures.append(signature / maximum)

    norms = np.max(np.abs(unstained), axis=1)
    valid = np.isfinite(norms) & (norms > np.finfo(float).eps)
    if np.count_nonzero(valid) < 3:
        raise ValueError("Unstained/AF events do not contain a measurable spectrum")
    af = np.mean(unstained[valid] / norms[valid, None], axis=0)
    af_norm = np.max(np.abs(af))
    if not np.isfinite(af_norm) or af_norm <= np.finfo(float).eps:
        raise ValueError("The mean AF signature is degenerate")
    signatures.append(af / af_norm)
    signatures = np.asarray(signatures, dtype=float)

    norm = np.sqrt(np.sum(signatures * signatures, axis=1) + 1e-9)
    similarity = (signatures @ signatures.T) / np.outer(norm, norm)
    design = signatures.T
    u, singular, vt = np.linalg.svd(design, full_matrices=False)
    tolerance = np.finfo(float).eps * max(design.shape) * singular[0]
    rank = int(np.count_nonzero(singular > tolerance))
    if rank < signatures.shape[0]:
        raise ValueError(f"Spectral model is rank deficient ({rank}/{signatures.shape[0]})")
    coefficients = (vt.T / singular) @ u.T
    off_diagonal = similarity - np.eye(similarity.shape[0])
    maximum_similarity = float(np.max(off_diagonal)) if off_diagonal.size else 0.0
    warning = ""
    if maximum_similarity > 0.95:
        warning = f"High emission similarity detected ({maximum_similarity:.4f})"
    return {
        "Signatures": signatures.astype(np.float32).tolist(),
        "Similarity": similarity.astype(np.float32).tolist(),
        "Coefficients": coefficients.astype(np.float32).tolist(),
        "Rank": rank,
        "Warning": warning,
    }


_spectral_result_json = json.dumps(_fit())
