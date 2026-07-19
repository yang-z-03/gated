import json
import numpy as np


def _fit():
    signatures = np.asarray(_signature_matrix, dtype=float)
    if signatures.ndim != 2 or signatures.shape[0] < 2 or signatures.shape[1] < 2:
        raise ValueError("The spectral signature matrix must contain at least two signatures and two detectors")
    if not np.all(np.isfinite(signatures)):
        raise ValueError("The spectral signature matrix contains non-finite values")

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
