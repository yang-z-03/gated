
try:
    from functools import singledispatch
    from contextlib import suppress, contextmanager
    import random

    import numpy as np
    import scipy.sparse as sp
    import igraph as ig
    from sklearn.utils import check_random_state

    import numba
    import umap
    from numba import njit

except:
    application.error(
        'Package setup error: \n'
        '  You should make sure that all required packages are set up properly with no environment error. \n'
        '  Required: numpy, scipy, igraph, scikit-learn, numba, umap-learn'
    )


def compute_neighbors(
    embedding, n_neighbors: int = 30, *, knn: bool = True, method = "umap",
    transformer = None, metric = "euclidean", metric_kwds = {},
    random_state = 0, n_jobs = -1
):
    n_cell, n_dim = embedding.shape
    if transformer is not None and not isinstance(transformer, str):
        n_neighbors = transformer.get_params()["n_neighbors"]
    
    # for very small dataset where there are less cells than requested knn.
    elif n_neighbors > n_cell:
        n_neighbors = 1 + int(0.5 * n_cell)
        application.warning(
            f"The number of cells is below the number of neighbors: \n"
             "  Adjusting to `n_neighbors = {n_neighbors}`")
    
    # default keyword arguments when `transformer` is not an instance
    method, transformer, shortcut = select_transformer(
        n_cell, method = method,  transformer = transformer, knn = knn, n_jobs = n_jobs, kwds = {
            'n_neighbors': n_neighbors,
            'metric': metric,
            'metric_params': metric_kwds,
            'random_state': random_state
        }
    )

    if n_cell >= 10000 and not knn:
        application.warning("Using such high number of cells without `knn = True` may take a lot of memory.")

    X = embedding

    application.progress(10, f'Running kNN ...')
    distances = transformer.fit_transform(X)
    knn_indices, knn_distances = get_indices_distances_from_sparse_matrix(
        distances, n_neighbors
    )

    if shortcut:
        # self._distances is a sparse matrix with a diag of 1, fix that
        distances[np.diag_indices_from(distances)] = 0
        if knn:  # remove too far away entries in self._distances
            distances = get_sparse_matrix_from_indices_distances(
                knn_indices, knn_distances, keep_self=False)
        
        else:  # convert to dense
            distances = distances.toarray()
    
    application.progress(30, f'Computing connectivities using {method} ...')
    if method == "umap":
        connectivities = umap_connectivity(
            knn_indices,
            knn_distances,
            n_obs = n_cell,
            n_neighbors= n_neighbors,
        )

    elif method == "gauss":
        connectivities = gauss_connectivity(
            distances, n_neighbors, knn = knn
        )

    elif method is not None:
        application.error('Invalid method. Possible values are "umap" and "gauss"')
    
    return (
        knn_indices, knn_distances, 
        distances, connectivities
    )


def get_sparse_matrix_from_indices_distances(
    indices, distances, *, keep_self: bool,
) -> sp.csr_matrix:
    """
    Create a sparse matrix from a pair of indices and distances.

    If keep_self = False, it verifies that the first column is the cell itself,
    then removes it from the explicitly stored zeroes.

    Duplicates in the data are kept as explicitly stored zeroes.
    """

    # instead of calling .eliminate_zeros() on our sparse matrix,
    # we manually handle the nearest neighbor being the cell itself.
    # this allows us to use _ind_dist_shortcut even when the data has duplicates.
    if not keep_self:
        indices, distances = remove_self_column(indices, distances)
    indptr = np.arange(0, np.prod(indices.shape) + 1, indices.shape[1])
    return sp.csr_matrix(
        (
            # copy the data, otherwise strange behavior here
            distances.copy().ravel(),
            indices.copy().ravel(),
            indptr,
        ),
        shape=(indices.shape[0],) * 2,
    )


def select_transformer(
    n_obs, method, transformer, *, knn: bool, n_jobs, kwds
):
    """
    Return effective `method` and transformer. `method` will be coerced to `'gauss'` or `'umap'`.
    `transformer` is coerced from a str or instance to an instance class. If `transformer` is 
    `None` and there are few data points, `transformer` will be set to a brute force
    `sklearn.neighbors.KNeighborsTransformer`. If `transformer` is `None` and there are many 
    data points, `transformer` will be set like `umap` does (i.e. to a 
    `pynndescent.PyNNDescentTransformer` with custom `n_trees` and `n_iter`). This pynndescent
    implementation will return a much faster nn graph but with some approximations.
    """

    use_dense_distances = (
        kwds["metric"] == "euclidean" and n_obs < 8192
    ) or not knn

    shortcut = transformer == "sklearn" or (
        transformer is None and 
        (use_dense_distances or n_obs < 4096)
    )

    # validate `knn`
    conn_method = method if method in { "gauss", None } else "umap"
    if not knn and not (conn_method == "gauss" and transformer is None):
        # 'knn = False' seems to be only intended for method 'gauss'
        application.error('For method other than "gauss", you should set `knn = True`, since `knn = False` makes no sense.')
        
    # coerce `transformer` to an instance

    if shortcut:

        # for less than 4096 cells, you should just use brute force searcher.
        # this is not slow as for this scale.
        from sklearn.neighbors import KNeighborsTransformer
        assert transformer in { None, "sklearn" }

        n_neighbors = n_obs - 1
        if knn:  # only obey n_neighbors arg if knn set
            n_neighbors = min(n_neighbors, kwds["n_neighbors"])
        transformer = KNeighborsTransformer(
            algorithm = "brute",
            n_jobs = n_jobs,
            n_neighbors = n_neighbors,
            metric = kwds["metric"],
            metric_params = dict(kwds["metric_params"]),
            # no random_state
        )
    
    elif transformer is None or transformer == "pynndescent":
        from pynndescent import PyNNDescentTransformer
        kwds = kwds.copy()
        kwds["metric_kwds"] = kwds.pop("metric_params")
        if transformer is None:
            # Use defaults from UMAP’s `nearest_neighbors` function
            kwds.update(
                n_jobs = n_jobs,
                n_trees = min(64, 5 + int(round((n_obs) ** 0.5 / 20.0))),
                n_iters = max(5, int(round(np.log2(n_obs)))),
            )
        
        transformer = PyNNDescentTransformer(**kwds)

    elif isinstance(transformer, str):
        application.error(f'Unknown transformer: {transformer}.')

    # else `transformer` is probably an instance
    return conn_method, transformer, shortcut


def get_indices_distances_from_sparse_matrix(
    D: sp.csr_matrix, n_neighbors: int
):
    """
    Get indices and distances from a sparse matrix.

    Makes sure that for both of the returned matrices:
    1. the first column corresponds to the cell itself as nearest neighbor.
    2. the number of neighbors (`.shape[1]`) is restricted to `n_neighbors`.
    """
    
    if (shortcut := index_distance_matrix_f(D)) is not None:
        indices, distances = shortcut
    else: indices, distances = index_distance_matrix_s(D, n_neighbors)

    # handle RAPIDS style indices_distances lacking the self-column
    if not has_self_column(indices, distances):
        indices = np.hstack([np.arange(indices.shape[0])[:, None], indices])
        distances = np.hstack([np.zeros(distances.shape[0])[:, None], distances])

    # if using the shortcut or adding the self column resulted in too many neighbors,
    # restrict the output matrices to the correct size
    if indices.shape[1] > n_neighbors:
        indices, distances = indices[:, :n_neighbors], distances[:, :n_neighbors]

    return indices, distances


def get_indices_distances_from_dense_matrix(D, n_neighbors: int):
    sample_range = np.arange(D.shape[0])[:, None]
    indices = np.argpartition(D, n_neighbors - 1, axis=1)[:, :n_neighbors]
    indices = indices[sample_range, np.argsort(D[sample_range, indices])]
    distances = D[sample_range, indices]
    return indices, distances


def has_self_column(indices, distances) -> bool:
    # some algorithms have some messed up reordering.
    return (indices[:, 0] == np.arange(indices.shape[0])).any()


def remove_self_column(indices, distances):
    if not has_self_column(indices, distances):
        application.error("The first neighbor should be the cell itself.")
    return indices[:, 1:], distances[:, 1:]


def index_distance_matrix_s(D, n_neighbors: int):

    indices = np.zeros((D.shape[0], n_neighbors), dtype = int)
    distances = np.zeros((D.shape[0], n_neighbors), dtype = D.dtype)
    n_neighbors_m1 = n_neighbors - 1

    for i in range(indices.shape[0]):
        neighbors = D[i].nonzero()  # 'true' and 'spurious' zeros
        indices[i, 0] = i
        distances[i, 0] = 0

        # account for the fact that there might be more than n_neighbors
        # due to an approximate search
        # the point itself was not detected as its own neighbor during the search
        if len(neighbors[1]) > n_neighbors_m1:
            sorted_indices = np.argsort(D[i][neighbors].A1)[:n_neighbors_m1]
            indices[i, 1:] = neighbors[1][sorted_indices]
            distances[i, 1:] = D[i][
                neighbors[0][sorted_indices], neighbors[1][sorted_indices]
            ]

        else:
            indices[i, 1:] = neighbors[1]
            distances[i, 1:] = D[i][neighbors]
    
    return indices, distances


def index_distance_matrix_f(D: sp.csr_matrix):
    # check if each row has the correct number of entries
    nnzs = D.getnnz(axis = 1)
    if not is_constant(nnzs):
        warning("sparse matrix has no constant number of neighbors per row. ")
        warning("cannot efficiently get indices and distances.")
        return None
    
    n_obs, n_neighbors = D.shape[0], int(nnzs[0])
    return (
        D.indices.reshape(n_obs, n_neighbors),
        D.data.reshape(n_obs, n_neighbors),
    )


@singledispatch
def is_constant(a, axis = None):
    """ Check whether values in array are constant. """
    raise NotImplementedError()


@is_constant.register(np.ndarray)
def _(a, axis = None):
    # should eventually support nd, not now.
    if axis is None: return bool((a == a.flat[0]).all())
    if axis == 0: return _is_constant_rows(a.T)
    elif axis == 1: return _is_constant_rows(a)
    else: application.error('`is_constant`: Not implemented.')


def _is_constant_rows(a):
    b = np.broadcast_to(a[:, 0][:, np.newaxis], a.shape)
    return (a == b).all(axis = 1)


@is_constant.register(sp.csr_matrix)
def _(a, axis = None):
    if axis is None:
        if len(a.data) == np.multiply(*a.shape):
            return is_constant(a.data)
        else: return (a.data == 0).all()
    if axis == 1: return _is_constant_csr_rows(a.data, a.indptr, a.shape)
    elif axis == 0:
        a = a.T.tocsr()
        return _is_constant_csr_rows(a.data, a.indptr, a.shape)
    else: application.error('`is_constant`: Not implemented.')


@njit
def _is_constant_csr_rows(data, indptr, shape,):
    n = len(indptr) - 1
    result = np.ones(n, dtype=np.bool_)
    for i in numba.prange(n):
        start = indptr[i]
        stop = indptr[i + 1]
        val = data[start] if stop - start == shape[1] else 0
        for j in range(start, stop):
            if data[j] != val:
                result[i] = False
                break
    return result


@is_constant.register(sp.csc_matrix)
def _(a, axis = None):
    if axis is None:
        if len(a.data) == np.multiply(*a.shape): return is_constant(a.data)
        else: return (a.data == 0).all()
    if axis == 0: return _is_constant_csr_rows(a.data, a.indptr, a.shape[::-1])
    elif axis == 1:
        a = a.T.tocsc()
        return _is_constant_csr_rows(a.data, a.indptr, a.shape[::-1])
    else: application.error('`is_constant`: Not implemented.')


def gauss_connectivity(distances, n_neighbors: int, *, knn: bool):
    """
    Derive gaussian connectivities between data points from their distances.

    Parameters
    ----------
    distances
        The input matrix of distances between data points.

    n_neighbors
        The number of nearest neighbors to consider.

    knn
        Specify if the distances have been restricted to k nearest neighbors.
    """

    # init distances
    if isinstance(distances, sp.csr_matrix):
        Dsq = distances.power(2)
        indices, distances_sq = get_indices_distances_from_sparse_matrix(
            Dsq, n_neighbors
        )
    
    else:
        assert isinstance(distances, np.ndarray)
        Dsq = np.power(distances, 2)
        indices, distances_sq = get_indices_distances_from_dense_matrix(
            Dsq, n_neighbors
        )

    # exclude the first point, the 0 th neighbor
    indices = indices[:, 1:]
    distances_sq = distances_sq[:, 1:]

    # choose sigma, the heuristic here doesn't seem to make much of a difference,
    # but is used to reproduce the figures of Haghverdi et al. (2016)
    if sp.issparse(distances):
        # as the distances are not sorted
        # we have decay within the n_neighbors first neighbors
        sigmas_sq = np.median(distances_sq, axis=1)
    
    else:
        # the last item is already in its sorted position through argpartition
        # we have decay beyond the n_neighbors neighbors
        sigmas_sq = distances_sq[:, -1] / 4
    sigmas = np.sqrt(sigmas_sq)

    # compute the symmetric weight matrix
    if not sp.issparse(distances):
        Num = 2 * np.multiply.outer(sigmas, sigmas)
        Den = np.add.outer(sigmas_sq, sigmas_sq)
        W = np.sqrt(Num / Den) * np.exp(-Dsq / Den)
        # make the weight matrix sparse
        if not knn:
            mask = W > 1e-14
            W[~ mask] = 0
        else:
            # restrict number of neighbors to ~ k
            # build a symmetric mask
            mask = np.zeros(Dsq.shape, dtype=bool)
            for i, row in enumerate(indices):
                mask[i, row] = True
                for j in row:
                    if i not in set(indices[j]):
                        W[j, i] = W[i, j]
                        mask[j, i] = True
            
            # set all entries that are not nearest neighbors to zero
            W[~ mask] = 0
    
    else:
        assert isinstance(Dsq, sp.csr_matrix)
        W = Dsq.copy()  # need to copy the distance matrix here; what follows is inplace
        for i in range(len(Dsq.indptr[:-1])):
            row = Dsq.indices[Dsq.indptr[i] : Dsq.indptr[i + 1]]
            num = 2 * sigmas[i] * sigmas[row]
            den = sigmas_sq[i] + sigmas_sq[row]
            W.data[Dsq.indptr[i] : Dsq.indptr[i + 1]] = np.sqrt(num / den) * np.exp(
                -Dsq.data[Dsq.indptr[i] : Dsq.indptr[i + 1]] / den
            )

        W = W.tolil()
        for i, row in enumerate(indices):
            for j in row:
                if i not in set(indices[j]):
                    W[j, i] = W[i, j]
        W = W.tocsr()

    return W


def umap_connectivity(
    knn_indices, knn_dists, *,
    n_obs: int,
    n_neighbors: int,
    set_op_mix_ratio: float = 1.0,
    local_connectivity: float = 1.0,
) -> sp.csr_matrix:
    """
    This is from umap.fuzzy_simplicial_set.

    Given a set of data X, a neighborhood size, and a measure of distance
    compute the fuzzy simplicial set (here represented as a fuzzy graph in
    the form of a sparse matrix) associated to the data. This is done by
    locally approximating geodesic distance at each point, creating a fuzzy
    simplicial set for each such point, and then combining all the local
    fuzzy simplicial sets into a global one via a fuzzy union.
    """

    import warnings
    with warnings.catch_warnings():
        # umap 0.5.0
        warnings.filterwarnings("ignore")
        from umap.umap_ import fuzzy_simplicial_set

    X = sp.coo_matrix(([], ([], [])), shape = (n_obs, 1))
    connectivities, sigmas, rhos = fuzzy_simplicial_set(
        X, n_neighbors, None, None,
        knn_indices = knn_indices,
        knn_dists = knn_dists,
        set_op_mix_ratio = set_op_mix_ratio,
        local_connectivity = local_connectivity,
    )

    return connectivities.tocsr()


def umap_simplical(
    X, connectivities, init_coords, *,
    n_components: int = 2,
    epoch: int | None = None,
    alpha: float = 1.0,
    gamma: float = 1.0,
    negative_sample_rate: int = 5,
    random_state = 0,
    a: float | None = None,
    b: float | None = None,
    metric = 'euclidean', metric_kwds = {}
):
    from umap.umap_ import simplicial_set_embedding
    
    emb, _ = simplicial_set_embedding(
        data = X,
        graph = connectivities.tocoo(),
        n_components = n_components,
        initial_alpha = alpha,
        a = a, b = b,
        gamma = gamma,
        negative_sample_rate = negative_sample_rate,
        n_epochs = epoch,
        init = init_coords,
        random_state = random_state,
        metric = metric,
        metric_kwds = metric_kwds,
        densmap = False,
        densmap_kwds = {},
        output_dens = False,
        verbose = False,
    )

    return emb


def run_umap(
    mat, connectivities, *,
    metric: str = 'euclidean',
    min_dist: float = 0.5,
    spread: float = 1.0,
    n_components: int = 2,
    maxiter: int | None = None,
    alpha: float = 1.0,
    gamma: float = 1.0,
    negative_sample_rate: int = 5,
    init_pos = "spectral",
    random_state = 0,
    a: float | None = None,
    b: float | None = None,
):

    from umap.umap_ import find_ab_params, simplicial_set_embedding

    if a is None or b is None:
        a, b = find_ab_params(spread, min_dist)

    init_coords = init_pos # let umap handle it
    random_state = check_random_state(random_state)
    
    # the data matrix X is really only used for determining the number of connected components
    # for the init condition in the UMAP embedding

    default_epochs = 500 if connectivities.shape[0] <= 10000 else 200
    n_epochs = default_epochs if maxiter is None else maxiter
    
    application.log(f'Running UMAP simplical')
    application.progress(60, f'Running UMAP simplical ...')
    
    emb = umap_simplical(
        mat, connectivities, init_coords,
        n_components = n_components,
        epoch = n_epochs,
        alpha = alpha, gamma = gamma, negative_sample_rate = negative_sample_rate,
        random_state = random_state, a = a, b = b,
        metric = metric,
    )

    return emb


# Script

import os

# Get the number of CPUs in the system
cpu_count = os.cpu_count()

connectivities_key = f'{platform.guid}.knn.connectivities'
skip_knn = False

if connectivities_key in workspace.storage.keys():
    connectivities = workspace.storage[connectivities_key]
    skip_knn = True

if not skip_knn:
    knn_params = application.input(requires = [
        application.require_integer('Number of neighbors', 15, 2, 100),
        application.require_enum('Connectivities algorithm', ['UMAP', 'Gauss'], 'UMAP'),
        application.require_enum('Distance metrics', ['Euclidean', 'Cosine'], 'Cosine'),
        application.require_integer('Random state', 42, 0, 10000),
        application.require_integer('Jobs (Multithreading)', -1, -1, cpu_count),
    ])

    nn_knn, nn_conn, nn_metrics, nn_rand, nn_jobs = knn_params
    nn_conn = nn_conn.lower()
    nn_metrics = nn_metrics.lower()

umap_params = application.input(requires = [
    application.require_enum('Initial position distance metrics', ['Euclidean', 'Cosine'], 'Cosine'),
    application.require_enum('Initial position', ['Spectral', 'Random'], 'Spectral'),
    application.require_float('Minimal distance', 0.5, 0, 5),
    application.require_float('Spread', 1, 0.3, 20),
    application.require_integer('Number of iterations', -1, -1, 500),
    application.require_integer('Random state', 42, 0, 10000),
    application.require_enum('Key added', [f'{platform.name} UMAP'], f'{platform.name} UMAP')
])

u_initpm, u_init, u_dist, u_spread, u_niter, u_rnd, u_key = umap_params
u_initpm = u_initpm.lower()
u_init = u_init.lower()
if u_niter == -1: u_niter = None

mat = platform.normalized.astype('float32')
application.log(f'Get embedding matrix of size {mat.shape} [{mat.dtype}]')


if not skip_knn:

    knn_indices, knn_distances, distances, connectivities = compute_neighbors(
        embedding = mat,
        n_neighbors = nn_knn,
        method = nn_conn,
        metric = nn_metrics,
        random_state = nn_rand,
        n_jobs = nn_jobs 
    )

    workspace.storage[f'{platform.guid}.knn.indices'] = knn_indices
    workspace.storage[f'{platform.guid}.knn.distances'] = knn_distances
    workspace.storage[f'{platform.guid}.knn.connectivities'] = connectivities

else: application.log(f'Get computed neighbor connectivities from storage.')

emb = run_umap(
    mat, connectivities,
    metric = u_initpm,
    min_dist = u_dist,
    spread = u_spread,
    n_components = 2,
    maxiter = u_niter,
    alpha = 1.0,
    gamma = 1.0,
    negative_sample_rate = 5,
    init_pos = u_init,
    random_state = u_rnd,
    a = None,
    b = None,
)

emb = np.array(emb.T)
platform.set_embedding(f'{u_key} X', emb[0])
platform.set_embedding(f'{u_key} Y', emb[1])