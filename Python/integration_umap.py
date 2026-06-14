
import umap

matrix = np.asarray(integration_job.integrated_matrix, dtype = np.float32)

if matrix.shape[0] == 0:
    application.log(f"{integration_job.name} UMAP skipped: no rows.")

else:
    neighbors = min(30, max(2, matrix.shape[0] - 1))
    application.progress(30, 'Calculating UMAP Embedding')
    reducer = umap.UMAP(
        n_components = 2,
        n_neighbors = neighbors,
        min_dist = 0.1,
        metric = "euclidean",
        random_state = 0,
    )
    embedding = reducer.fit_transform(matrix)
    integration_job.set_embedding(f"{integration_job.name} UMAP 1", embedding[:, 0])
    integration_job.set_embedding(f"{integration_job.name} UMAP 2", embedding[:, 1])
    application.log(f"{integration_job.name} UMAP embeddings written.")
