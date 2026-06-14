
from scipy.spatial import cKDTree
import igraph as ig
import leidenalg

matrix = np.asarray(integration_job.integrated_matrix, dtype = np.float32)
row_count = matrix.shape[0]

if row_count == 0:
    application.log(f"{integration_job.name} Leiden skipped: no rows.")

elif row_count == 1:
    integration_job.set_embedding(f"{integration_job.name} Leiden", np.array(["1"] * row_count))
    application.log(f"{integration_job.name} Leiden embedding written.")

else:
    k = min(15, row_count - 1)
    distances, indices = cKDTree(matrix).query(matrix, k = k + 1)
    edges = set()
    weights = []

    application.progress(30, 'Constructing kNN Graph')
    for row in range(row_count):
        for neighbor, distance in zip(indices[row, 1:], distances[row, 1:]):
            a, b = sorted((int(row), int(neighbor)))
            edge = (a, b)
            if edge in edges:
                continue
            edges.add(edge)
            weights.append(1.0 / (1.0 + float(distance)))

    graph = ig.Graph(n=row_count, edges = list(edges), directed = False)
    graph.es["weight"] = weights

    application.progress(70, 'Partitioning')
    partition = leidenalg.find_partition(
        graph,
        leidenalg.RBConfigurationVertexPartition,
        weights = graph.es["weight"],
        seed = 0,
    )

    labels = np.asarray([str(value + 1) for value in partition.membership])
    integration_job.set_embedding(f"{integration_job.name} Leiden", labels)
    application.log(f"{integration_job.name} Leiden embedding written.")
