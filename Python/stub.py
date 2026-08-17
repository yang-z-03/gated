
from __future__ import annotations
from typing import Callable, Literal
import numpy as np
import pandas as pd

class Compensation:
    '''
    A compensation matrix registered on a grouping.

    Attributes
    ==========
    name: readonly str
        The compensation name.

    channels: readonly list[str]
        The ordered channel names covered by the matrix.

    matrix: readonly np.ndarray
        A copy of the compensation matrix as a two-dimensional float array.

    Examples
    ========
    >>> compensation = workspace["Experiment"].compensations["Spillover"]
    >>> compensation.matrix.shape
    (8, 8)

    This retrieves the named eight-channel matrix. The channel order in
    ``compensation.channels`` defines both axes of the readonly NumPy matrix.
    '''
    name: str
    channels: list[str]
    matrix: np.ndarray

class StatisticDefinition:
    '''
    A statistic attached to a grouping or gate strategy.

    Attributes
    ==========
    kind: readonly str
        Native statistic kind name, or "Python" for source-backed Python statistics.

    Examples
    ========
    >>> definition = grouping.strategy.define_statistics("Median", "CD3")
    >>> definition.kind
    'Median'

    This attaches a native median statistic to the root strategy and exposes its
    definition through the uniquely keyed ``strategy.statistics`` dictionary.
    '''
    kind: str
    def is_python(self) -> bool:
        '''
        Return whether this definition is backed by Python source.

        Examples
        ========
        >>> definition.is_python()
        False

        This distinguishes native statistics from definitions whose implementation
        can be inspected and replaced with ``get_method`` and ``set_method``.
        '''
        ...
    def get_method(self) -> str:
        '''
        Return the stored Python source, or an empty string for a native statistic.

        Examples
        ========
        >>> source = custom_definition.get_method()

        ``source`` contains the complete callable definition used when recalculating
        the custom statistic.
        '''
        ...
    def set_method(self, source: str, callable_name: str = "entry", display_name: str | None = None, parameters: np.ndarray | None = None): 
        '''
        Replaces this statistic with a Python-backed implementation.

        Parameters
        ==========
        source: str
            Python source text defining the callable.
            It must define the selected entry callable plus ``requires()`` and
            ``format(value)``. The entry signature is
            ``entry(matrix: np.ndarray, channels: list[str], parameters: list)``.

        callable_name: str, default "entry"
            Name of the callable inside source.

        display_name: str | None, default None
            Optional name shown in the statistics table.

        parameters: np.ndarray | None, default None
            Optional numeric parameter vector passed to the callable.

        Examples
        ========
        >>> source = "def entry(matrix, channels, parameters):\n    return matrix[:, 0].mean()\n\ndef requires():\n    return []\n\ndef format(value):\n    return f'{value:.2f}'"
        >>> definition.set_method(source, display_name="First-channel mean")

        This converts the definition to a Python statistic, validates ``entry``,
        assigns a unique display key, and recalculates the owning grouping.
        '''
        ...

class Population:
    '''
    A population result for a sample and gate region.

    Attributes
    ==========
    mask: readonly np.ndarray
        Boolean sample-wide mask. True values mark events in this population.

    populations: readonly dict[str, Population]
        Child populations keyed by population key.

    strategy: readonly Strategy
        Strategy node that produced this population.

    population_keys: readonly list[str]
        Available child population keys.

    compensated_matrix: readonly np.ndarray
        Compensated event matrix as a NumPy readonly array.

    Examples
    ========
    >>> population = grouping.strategy["Lymphocytes"].get_population(grouping["Tube 1"])
    >>> selected_events = population.compensated_matrix

    ``selected_events`` contains one row for each event selected by the bound gate
    population, while ``population.mask`` maps those rows back to the full sample.
    '''
    mask: np.ndarray
    populations: dict[str, "Population"]
    strategy: "Strategy"
    population_keys: list[str]
    compensated_matrix: np.ndarray

    def set_embedding(self, name: str, value: np.ndarray): 
        '''
        Writes a one-dimensional embedding into the owning sample for this population.

        value must be a one-dimensional NumPy array of floats or strings. Float
        arrays are stored as floating-point embeddings. String arrays are stored
        as categorical integer embeddings with category text metadata.

        value may contain exactly one value per selected event, or one value per
        event in the full sample. Only masked events are written; non-population
        events keep existing values or become NaN for a new embedding.

        Examples
        ========
        >>> scores = np.linspace(0.0, 1.0, population.compensated_matrix.shape[0])
        >>> population.set_embedding("Activation score", scores)

        This writes one score to every selected event in the owning sample and
        leaves events outside the population unchanged or missing.
        '''
        ...

    def __getitem__(self, population_key: str) -> "Population": 
        '''
        Return a descendant population result by its sample population key.

        Examples
        ========
        >>> activated = population["Activated"]

        This retrieves the named descendant population below ``population`` and
        raises KeyError if the key is not present in ``population.population_keys``.
        '''
        ...

class Sample:
    '''
    A flow sample inside a grouping.

    Attributes
    ==========
    name: readonly str
        Sample name.

    channels: readonly list[str]
        Ordered channel names.

    embeddings: readonly list[str]
        Names of sample-level embedding arrays.

    matrix: readonly np.ndarray
        Raw event matrix as a NumPy readonly array.

    embedding_matrix: readonly np.ndarray
        Matrix containing all embeddings as columns.

    compensated_matrix: readonly np.ndarray
        Compensated event matrix as a NumPy readonly array.

    populations: readonly dict[str, Population]
        Populations keyed by population key.

    strategy: readonly Strategy
        Root strategy for this sample's grouping.

    population_keys: readonly list[str]
        Available population keys for this sample.

    Examples
    ========
    >>> sample = workspace["Experiment"].samples["Tube 1"]
    >>> raw = sample.matrix
    >>> compensated = sample.compensated_matrix

    This retrieves readonly event matrices for the same events before and after
    compensation. Their columns follow ``sample.channels``.
    '''
    name: str
    channels: list[str]
    embeddings: list[str]
    matrix: np.ndarray
    embedding_matrix: np.ndarray
    populations: dict[str, Population]
    strategy: "Strategy"
    population_keys: list[str]
    compensated_matrix: np.ndarray
        
    def __getitem__(self, population_key: str) -> Population: 
        '''
        Return a calculated population by its sample-level key.

        Examples
        ========
        >>> lymphocytes = sample["Lymphocytes"]

        This is equivalent to ``sample.populations["Lymphocytes"]`` and provides
        the selected matrix, mask, child populations, and originating strategy.
        '''
        ...

class Strategy:
    '''
    A root or gate strategy used to define gates, select their output populations,
    and attach statistics. Mutating methods update the workspace immediately and
    recalculate the owning grouping.

    Attributes
    ==========
    name: readonly str
        Strategy or gate name.

    statistics: readonly dict[str, StatisticDefinition]
        Statistics attached to this node, keyed by unique display name.

    population_keys: readonly list[str]
        Keys for this strategy's own output populations. Root and ordinary gates
        expose ``["primary"]``. Quadrant, threshold, and range gates expose their
        named regions, including any names customized in the project tree.

    has_multiple_populations: readonly bool
        True for gates that expose multiple regions.

    Examples
    ========
    >>> root = workspace["Experiment"].strategy
    >>> polygon = root.define_gate_polygon(
    ...     "Lymphocytes", "primary", "FSC-A", "SSC-A",
    ...     np.array([[100, 100], [800, 120], [700, 900]], dtype=float))
    >>> selected = root["Lymphocytes"]

    The code creates an ordinary child gate and then indexes its ``primary``
    output directly from the root. ``selected`` is a Strategy bound to that
    child population and can retrieve its result for any compatible sample.
    '''
    name: str
    statistics: dict[str, StatisticDefinition]
    population_keys: list[str]
    has_multiple_populations: bool

    def children(self, population_key: str = "default") -> dict[str, "Strategy"]:
        '''
        Return direct child gate definitions keyed by gate name.

        ``population_key`` chooses which output of this strategy owns the children.
        ``"default"`` uses the population already bound by ``parent[key]`` or the
        only population of an ordinary gate. A multi-population gate that is not
        bound requires an explicit key.

        Examples
        ========
        >>> gates = workspace["Experiment"].strategy.children("primary")
        >>> lymphocytes = gates["Lymphocytes"]

        This obtains the direct ``Lymphocytes`` gate definition. Unlike Strategy
        indexing, ``children`` is keyed by gate names rather than output-population
        names.
        '''
        ...

    def __getitem__(self, population_key: str) -> "Strategy":
        '''
        Select a direct child gate population by its flattened population key.

        An ordinary child contributes its gate name, while a multi-population child
        contributes each region name and does not contribute ``"primary"`` or the
        multi-population gate name.

        Examples
        ========
        >>> root = workspace["Experiment"].strategy
        >>> top_left = root["top left"]
        >>> q3 = root["q3"]

        If a direct child quadrant has regions named ``top left``, ``top right``,
        ``q3``, and ``q4``, these expressions bind the requested quadrant output.
        '''
        ...

    def get_population(self, sample: Sample) -> Population:
        '''
        Return this strategy's bound population result for ``sample``.

        Examples
        ========
        >>> root = workspace["Experiment"].strategy
        >>> sample = workspace["Experiment"]["Tube 1"]
        >>> events = root["Lymphocytes"].get_population(sample)

        This resolves the ``Lymphocytes`` gate for ``Tube 1`` and returns a
        Population whose matrices and mask contain only that gate output.
        '''
        ...

    def get_statistics(self, sample: Sample, statistic: StatisticDefinition) -> np.ndarray:
        '''
        Calculate or retrieve one attached statistic for the bound population.

        Examples
        ========
        >>> gate = workspace["Experiment"].strategy["Lymphocytes"]
        >>> definition = gate.statistics["Number of Events"]
        >>> value = gate.get_statistics(workspace["Experiment"]["Tube 1"], definition)

        ``value`` is a one-element NumPy array containing the event count for the
        selected population in ``Tube 1``.
        '''
        ...
    
    def define_statistics(self, kind: str, channel: str = "") -> StatisticDefinition:
        '''
        Add a native statistic and return its definition.

        ``kind`` is case-insensitive and accepts ``Mean``, ``Median``,
        ``GeometricMean``, ``CoefficientOfVariation``, ``StandardDeviation``,
        ``NumberOfEvents``, ``FrequencyOfParent``, and ``FrequencyOfAll``. The
        channel may be empty for count/frequency statistics. Duplicate display
        names receive a numeric suffix so ``statistics`` remains a valid dict.

        Examples
        ========
        >>> gate = workspace["Experiment"].strategy.children()["Lymphocytes"]
        >>> mean = gate.define_statistics("Mean", "CD3")

        This attaches a mean-CD3 statistic to the gate, recalculates samples, and
        makes the definition available under ``gate.statistics["Mean of CD3"]``.
        '''
        ...

    def define_statistics_python(self, source: str, callable_name: str = "entry", display_name: str | None = None, parameters: np.ndarray | None = None) -> StatisticDefinition: 
        '''
        Add a source-backed Python statistic.

        The source must also define ``requires()`` and ``format(value)``. The entry
        callable receives ``(matrix, channels, parameters)`` and returns a scalar
        or NumPy-compatible value. ``display_name`` becomes the dictionary key and
        is made unique within this strategy.

        Examples
        ========
        >>> source = "def entry(matrix, channels, parameters):\n    return matrix[:, channels.index(parameters[0])].max()\n\ndef requires():\n    return []\n\ndef format(value):\n    return f'{value:.2f}'"
        >>> custom = gate.define_statistics_python(
        ...     source, display_name="Maximum CD3", parameters=np.array(["CD3"]))

        This registers a statistic that returns the maximum CD3 value for each
        population evaluated by ``gate``.
        '''
        ...

    def define_gate_polygon(self, name: str, population_key: str, channel1: str, channel2: str, vertices: np.ndarray) -> "Strategy": 
        '''
        Add a polygon gate to one population of this strategy.

        ``vertices`` must be an N x 2 matrix with at least three ``[x, y]`` rows.
        Coordinates are raw values in ``channel1`` and ``channel2`` display space;
        the closing edge from the final row to the first is added automatically.

        Examples
        ========
        >>> polygon = root.define_gate_polygon(
        ...     "Cells", "primary", "FSC-A", "SSC-A",
        ...     np.array([[100, 80], [900, 100], [850, 700], [150, 650]]))

        This creates a four-sided gate named ``Cells`` under the root population.
        Its only output is indexed as ``root["Cells"]``.
        '''
        ...

    def define_gate_rectangle(self, name: str, population_key: str, channel1: str, channel2: str, rectangle: np.ndarray) -> "Strategy": 
        '''
        Add an axis-aligned rectangle gate.

        ``rectangle`` must be a 2 x 2 matrix containing two opposite ``[x, y]``
        corners in raw channel coordinates. Corner order does not matter.

        Examples
        ========
        >>> rect = root.define_gate_rectangle(
        ...     "Singlets", "primary", "FSC-A", "FSC-H",
        ...     np.array([[100, 120], [900, 940]]))

        This selects events whose two channel values fall between the supplied
        corner bounds and exposes them as ``root["Singlets"]``.
        '''
        ...

    def define_gate_quadrant(self, name: str, population_key: str, channel1: str, channel2: str, center: np.ndarray) -> "Strategy": 
        '''
        Add a four-region straight quadrant gate.

        ``center`` must be a 1 x 2 matrix ``[[x_cutoff, y_cutoff]]`` in raw channel
        coordinates. The generated keys are ``top right``, ``top left``,
        ``bottom right``, and ``bottom left`` unless renamed.

        Examples
        ========
        >>> quad = root.define_gate_quadrant(
        ...     "CD4 CD8", "primary", "CD4", "CD8", np.array([[500, 400]]))
        >>> double_positive = root["top right"]

        The first line partitions the parent at CD4=500 and CD8=400; the second
        binds the high-CD4/high-CD8 output.
        '''
        ...

    def define_gate_curly(self, name: str, population_key: str, channel1: str, channel2: str, center: np.ndarray) -> "Strategy": 
        '''
        Add a four-region curved quadrant gate.

        ``center`` is a 1 x 2 ``[[x_anchor, y_anchor]]`` matrix in raw channel
        coordinates. The upper and right boundaries use the application's curved
        log-slope rule; the four output keys match a regular quadrant.

        Examples
        ========
        >>> curved = root.define_gate_curly(
        ...     "Curved split", "primary", "CD4", "CD8", np.array([[500, 400]]))

        This creates curved high/low boundaries anchored at (500, 400), which is
        useful when fluorescence spread makes straight quadrant lines unsuitable.
        '''
        ...

    def define_gate_offset(self, name: str, population_key: str, channel1: str, channel2: str, positions: np.ndarray) -> "Strategy": 
        '''
        Add a quadrant whose vertical boundary differs above and below its center.

        ``positions`` must be a 3 x 2 matrix: ``[center, upper_boundary,
        lower_boundary]``. The center row supplies ``[center_x, center_y]``; only
        the X values of rows two and three are used for the upper and lower
        vertical boundaries, respectively.

        Examples
        ========
        >>> offset = root.define_gate_offset(
        ...     "Offset split", "primary", "CD4", "CD8",
        ...     np.array([[500, 400], [560, 400], [440, 400]]))

        Events above CD8=400 are divided at CD4=560, while events below it are
        divided at CD4=440, producing the standard four quadrant populations.
        '''
        ...

    def define_gate_threshold(self, name: str, population_key: str, channel1: str, position: np.ndarray) -> "Strategy": 
        '''
        Add a one-dimensional two-region threshold gate.

        ``position`` must contain one row whose first value is the raw X cutoff;
        the conventional shape is ``np.array([[cutoff]])``. Outputs are ``more``
        for values at or above the cutoff and ``less`` for values below it.

        Examples
        ========
        >>> threshold = root.define_gate_threshold(
        ...     "CD3 split", "primary", "CD3", np.array([[250]]))
        >>> positive = root["more"]

        This partitions the parent at CD3=250 and binds the positive-side output.
        '''
        ...

    def define_gate_range(self, name: str, population_key: str, channel1: str, positions: np.ndarray) -> "Strategy": 
        '''
        Add a one-dimensional three-region range gate.

        ``positions`` must contain two rows whose first values are the raw lower
        and upper X boundaries; the conventional shape is ``[[lower], [upper]]``.
        Row order does not matter. Outputs are ``in range``, ``below range``, and
        ``above range``.

        Examples
        ========
        >>> band = root.define_gate_range(
        ...     "DNA band", "primary", "DNA", np.array([[300], [700]]))

        This separates DNA values inside 300..700 from values below and above the
        interval and exposes all three outputs through root indexing.
        '''
        ...

    def define_gate_overlap(self, name: str, population_key: str, gate2: str, population2: str) -> "Strategy": 
        '''
        Add the intersection of this gate population and another gate population.

        Examples
        ========
        >>> overlap = gate_a.define_gate_overlap("A and B", "primary", "Gate B", "primary")

        This creates a child containing events present in both selected operands,
        clipped to the current parent. An ambiguous ``gate2`` name raises an error.
        '''
        ...

    def define_gate_exclude(self, name: str, population_key: str, gate2: str, population2: str) -> "Strategy": 
        '''
        Add this gate population minus another gate population.

        Examples
        ========
        >>> clean = gate_a.define_gate_exclude("A without B", "primary", "Gate B", "primary")

        This creates a child containing events in Gate A but not Gate B, clipped
        to the current parent population.
        '''
        ...

    def define_gate_merge(self, name: str, population_key: str, gate2: str, population2: str) -> "Strategy": 
        '''
        Add the union of this gate population and another gate population.

        Examples
        ========
        >>> merged = gate_a.define_gate_merge("A or B", "primary", "Gate B", "primary")

        This creates a child containing distinct events found in either operand,
        clipped to the current parent population.
        '''
        ...

class Grouping:
    '''
    A named collection of compatible samples, gates, statistics, and compensation.

    Attributes
    ==========
    name: readonly str
        Grouping name.

    samples: readonly dict[str, Sample]
        Compatible samples keyed by unique sample name.

    strategy: readonly Strategy
        The single root strategy for gates and grouping-level statistics.

    compensations: readonly dict[str, Compensation]
        Compensation candidates keyed by name.

    current_compensation: readonly str
        Name of the applied compensation, or an empty string.

    channels: readonly list[str]
        Ordered channel names for compatible samples.

    Examples
    ========
    >>> grouping = workspace["Experiment"]
    >>> sample = grouping.samples["Tube 1"]
    >>> root = grouping.strategy

    This retrieves a sample by its dictionary key and the grouping's sole root
    strategy. Sample names are kept unique within the grouping.
    '''
    name: str
    samples: dict[str, Sample]
    strategy: Strategy
    compensations: dict[str, Compensation]
    current_compensation: str
    channels: list[str]
    def add_fcs(self, filename: str) -> Sample: 
        '''
        Read and add a compatible FCS file, returning the new Sample.

        Examples
        ========
        >>> sample = grouping.add_fcs(r"D:\\data\\tube01.fcs")

        This imports the file, makes its name unique within ``grouping.samples``,
        applies the grouping compensation, and recalculates gates and statistics.
        '''
        ...
    def can_accept_fcs(self, filename: str) -> bool: 
        '''
        Return whether an FCS file has a channel profile compatible with the group.

        Examples
        ========
        >>> if grouping.can_accept_fcs(path):
        ...     grouping.add_fcs(path)

        The file is imported only when its ordered channel profile can join the
        grouping without invalidating existing sample calculations.
        '''
        ...
    def set_compensation(self, key: str) -> Compensation: 
        '''
        Apply a registered compensation by name and recalculate the grouping.

        Examples
        ========
        >>> applied = grouping.set_compensation("Spillover")

        This selects ``grouping.compensations["Spillover"]`` as the active matrix
        and refreshes every sample's compensated values and derived results.
        '''
        ...
    def create_compensation(self, key: str, channels: list[str], matrix: np.ndarray) -> Compensation: 
        '''
        Register a square compensation matrix for an ordered channel list.

        Examples
        ========
        >>> identity = grouping.create_compensation(
        ...     "Identity 2", ["FITC-A", "PE-A"], np.eye(2))

        This adds a two-channel identity candidate. It does not replace the active
        matrix until ``set_compensation(identity.name)`` is called.
        '''
        ...
    def __getitem__(self, sample: str) -> Sample: 
        '''
        Return a sample by name.

        Examples
        ========
        >>> tube = grouping["Tube 1"]

        This is equivalent to ``grouping.samples["Tube 1"]`` and raises KeyError
        when the sample name is unavailable.
        '''
        ...

class ViewOptions:
    '''
    Axis/view transformation settings for one channel or embedding.

    Attributes
    ==========
    min: readonly float
        Minimum value on the original data scale.

    max: readonly float
        Maximum value on the original data scale.

    transformed_min: readonly float
        Minimum value after applying the selected transformation.

    transformed_max: readonly float
        Maximum value after applying the selected transformation.

    t: readonly float
        Logicle top-of-scale parameter.

    w: readonly float
        Logicle linear-region width parameter.

    m: readonly float
        Logicle decades parameter.

    a: readonly float
        Logicle additional-negative-decades parameter.

    arcsinh_a: readonly float
        Arcsinh cofactor.

    normalization: readonly str
        Transformation name: linear, signed_log1p, logicle, or arcsinh.

    Examples
    ========
    >>> view = platform.transformations[platform.channels[0]]
    >>> view.normalization, (view.min, view.max)

    This inspects the transformation and original-scale display range used for the
    first selected platform channel. Logicle and arcsinh parameters are available
    from the same readonly object.
    '''
    min: float
    max: float
    transformed_min: float
    transformed_max: float
    t: float
    w: float
    m: float
    a: float
    arcsinh_a: float
    normalization: str

class PlatformPopulation:
    '''
    One population input selected for a platform run.

    Attributes
    ==========
    group: readonly str
        Source grouping name.

    sample: readonly str
        Source sample name.

    name: readonly str
        Population display name.

    population: readonly str
        Population display name.

    group_id: readonly str
        Source grouping identifier.

    sample_id: readonly str
        Source sample identifier.

    gate_id: readonly str
        Source gate identifier.

    region: readonly str
        Selected gate region name.

    selected: readonly bool
        Whether this population is selected for the platform run.

    Examples
    ========
    >>> source = platform.populations[0]
    >>> source.sample, source.population, source.selected

    This identifies the sample and gate region contributing rows to the current
    platform input and reports whether that source is selected.
    '''
    group: str
    sample: str
    name: str
    population: str
    group_id: str
    sample_id: str
    gate_id: str
    region: str
    selected: bool

class Platform:
    '''
    Base platform wrapper. Names intentionally match the C# Platform model.

    Attributes
    ==========
    name: readonly str
        Platform name.

    guid: readonly str
        Stable platform identifier.

    transform: readonly str
        Primary axis transformation name.

    populations: readonly list[PlatformPopulation]
        Population inputs prepared for this platform.

    channels: readonly list[str]
        Selected channel names.

    matrix: readonly np.ndarray
        Original platform input matrix.

    compensated: readonly np.ndarray
        Compensated platform input matrix.

    transformations: readonly dict[str, ViewOptions]
        Per-channel transformation settings.

    transformed: readonly np.ndarray
        Transformed platform input matrix.

    models: readonly dict[str, object]
        Registered aggregate fitted models keyed by model name.

    components: readonly dict[str, list[object]]
        Registered typed model components keyed by component name.

    result: readonly dict[str, object]
        Result tables keyed by result name.

    parameters: readonly dict[str, object]
        Platform option values supplied to the script.

    has_graphics: readonly bool
        Whether fitted graphics are currently available.

    has_data_table: readonly bool
        Whether result tables are currently available.

    row_map: readonly pd.DataFrame
        Mapping from matrix rows to their source samples and populations.

    Examples
    ========
    >>> platform.clear_results()
    >>> platform.set_statistic("Rows analyzed", platform.matrix.shape[0])

    This clears stale fitted output and records the number of prepared input rows.
    Platform scripts use the remaining methods to publish tables, embeddings, and
    typed model components back to the workspace.
    '''
    name: str
    guid: str
    transform: str
    populations: list[PlatformPopulation]
    channels: list[str]
    matrix: np.ndarray
    compensated: np.ndarray
    transformations: dict[str, ViewOptions]
    transformed: np.ndarray
    models: dict[str, object]
    components: dict[str, list[object]]
    result: dict[str, object]
    parameters: dict[str, object]
    has_graphics: bool
    has_data_table: bool
    row_map: pd.DataFrame

    def sample_metadata(self, sample_name: str, column_name: str) -> str:
        '''
        Return one workspace metadata value, or an empty string when unavailable.

        Examples
        ========
        >>> batch = platform.sample_metadata("Tube 1", "Batch")

        This looks up the ``Batch`` cell for ``Tube 1`` without modifying the
        workspace metadata table.
        '''
        ...

    def set_embedding(self, name: str, value: np.ndarray):
        '''
        Writes one value per integration row back into the source samples as a synchronized embedding.

        Numeric arrays create floating-point embeddings. String arrays create categorical embeddings.

        Examples
        ========
        >>> platform.set_embedding("Integrated UMAP 1", embedding[:, 0])

        This maps one value per ``platform.row_map`` row back to its source event,
        creating a synchronized sample embedding across all contributing samples.
        '''
        ...

    def clear_results(self):
        '''
        Clear result tables, model components, fitted plots, and platform statistics.

        Examples
        ========
        >>> platform.clear_results()

        This removes output from the previous run while preserving prepared input
        matrices, population selections, transformations, and script parameters.
        '''
        ...

    def set_result_table(self, key: str, title: str, columns: list[str], rows: list[list[str]]):
        '''
        Create or replace a named result table rendered by project layouts.

        Examples
        ========
        >>> platform.set_result_table("summary", "Summary", ["Metric", "Value"], [["AIC", "12.4"]])

        This publishes a two-column table under key ``summary``; writing the same
        key again replaces that table.
        '''
        ...

    def add_component_gamma(self, key: str, alpha: float, beta: float, amplitude: float, source_id: int = -1, title: str = "", normalizer: float = 1.0, x_label: str = "", y_label: str = ""):
        '''
        Add a gamma component parameterized by shape, rate, and amplitude.

        Examples
        ========
        >>> platform.add_component_gamma("g1", alpha=3.2, beta=0.8, amplitude=120)

        This registers ``g1`` in ``platform.components`` for later aggregation by
        ``set_fit_addition`` and for component rendering.
        '''
        ...

    def add_component_normal(self, key: str, mu: float, sigma: float, amplitude: float, source_id: int = -1, title: str = "", normalizer: float = 1.0, x_label: str = "", y_label: str = ""):
        '''
        Add a normal component with mean, standard deviation, and amplitude.

        Examples
        ========
        >>> platform.add_component_normal("generation 0", mu=0.82, sigma=0.04, amplitude=1.0)

        This registers a Gaussian-shaped generation component that can be plotted
        alone and combined into an additive fitted model.
        '''
        ...

    def add_component_exponential(self, key: str, slope: float, exponent: float, intercept: float, source_id: int = -1, title: str = "", normalizer: float = 1.0, x_label: str = "", y_label: str = ""):
        '''
        Add the component ``intercept + exp(slope*x + exponent)``.

        Examples
        ========
        >>> platform.add_component_exponential("background", -0.5, 2.0, 0.01)

        This registers a decaying exponential background component for subsequent
        additive model construction.
        '''
        ...

    def add_component_linear(self, key: str, slope: float, intercept: float, source_id: int = -1, title: str = "", normalizer: float = 1.0, x_label: str = "", y_label: str = ""):
        '''
        Add the component ``slope*x + intercept``.

        Examples
        ========
        >>> platform.add_component_linear("baseline", slope=0.0, intercept=0.02)

        This registers a constant baseline as a typed linear component.
        '''
        ...

    def add_component_polynomial(self, key: str, coefficients: list[float], minimum: float, maximum: float, source_id: int = -1, title: str = "", normalizer: float = 1.0, x_label: str = "", y_label: str = ""):
        '''
        Add a non-negative polynomial on a closed X interval.

        ``coefficients`` are ordered from constant term upward.

        Examples
        ========
        >>> platform.add_component_polynomial("trend", [0.1, 0.2, -0.05], 0.0, 1.0)

        This registers ``0.1 + 0.2*x - 0.05*x**2`` on 0..1, clipped to
        non-negative model values.
        '''
        ...

    def set_fit_addition(self, key: str, models: list[str], weights: list[float], intercept: float = 0, source_id: int = -1, title: str = "", normalizer: float = 1.0, x_label: str = "", y_label: str = ""):
        '''
        Aggregate existing typed components into one additive fitted model.

        ``models`` and ``weights`` must have equal lengths, and every model key must
        already exist in ``platform.components``.

        Examples
        ========
        >>> platform.set_fit_addition("overall", ["g1", "background"], [1.0, 1.0])

        This creates an overall curve equal to the sum of the two registered
        components and stores it in ``platform.models``.
        '''
        ...

    def set_statistic(self, name: str, value):
        '''
        Create or replace one named scalar platform statistic.

        Examples
        ========
        >>> platform.set_statistic("R squared", 0.973)

        This publishes the formatted value under ``R squared`` for linked layout
        statistic tables; using the same name replaces the previous value.
        '''
        ...

class UnivariatePlatform(Platform):
    '''
    Platform wrapper for one-dimensional distribution analysis.

    Attributes
    ==========
    major: readonly str
        Selected analysis channel.

    histogram: readonly np.ndarray
        Fixed-bin display histogram.

    smoothed: readonly np.ndarray
        Smoothed display histogram.

    smoothing_window: readonly int
        Configured smoothing window size.

    enable_smoothing: readonly bool
        Whether display smoothing is enabled.

    Examples
    ========
    >>> x = platform.histogram[:, 0]
    >>> density = platform.smoothed[:, 1] if platform.enable_smoothing else platform.histogram[:, 1]

    This selects the fixed display-bin X coordinates and the density series used
    by a one-dimensional platform preview.
    '''
    major: str
    histogram: np.ndarray
    smoothed: np.ndarray
    smoothing_window: int
    enable_smoothing: bool

class MultivariatePlatform(Platform):
    '''
    Platform wrapper for multichannel analysis.

    Attributes
    ==========
    normalized: readonly np.ndarray
        Normalized multichannel input matrix.

    Examples
    ========
    >>> rows, features = platform.normalized.shape

    This obtains the analysis-ready multichannel matrix after the platform's
    configured normalization has been applied to each selected feature.
    '''
    normalized: np.ndarray

class Workspace:
    '''
    The main workspace object representing the currently opened workspace in the application.
    All groupings, samples, and further properties, and metadata table are accessible through this object.

    Attributes
    ==========
    metadata: readonly pd.DataFrame
        A typed pandas DataFrame containing the workspace metadata table. Group and Sample are
        read-only identity columns; apply_metadata(dataframe) ignores them as metadata values.
    
    groupings: readonly list[Grouping]
        A list of groupings in the workspace. Each grouping represents a collection of samples that are 
        analyzed together. A grouping can have multiple samples.

    platforms: readonly dict[str, Platform]
        Prepared platforms keyed by platform name.

    storage: dict
        Workspace-scoped Python dictionary shared by all script runs. It is kept in memory only
        and cleared when the workspace is closed or replaced.

    Examples
    ========
    >>> grouping = workspace.add_grouping("Experiment")
    >>> workspace.storage["seed"] = 42

    This creates a uniquely named grouping and stores a session-only value that
    subsequent scripts in the same open workspace can reuse.
    '''

    metadata: pd.DataFrame
    groupings: list[Grouping]
    platforms: dict[str, Platform]
    storage: dict
    
    def add_grouping(self, name: str) -> Grouping:
        '''
        Add and return an empty grouping with a unique workspace-level name.

        Examples
        ========
        >>> replicate = workspace.add_grouping("Replicate")

        This appends a new group. If ``Replicate`` already exists, a numeric suffix
        is added so it remains addressable by name.
        '''
        ...
    def apply_metadata(self, dataframe: pd.DataFrame): 
        '''
        Replaces workspace metadata with the given DataFrame. Group and Sample identify rows and are
        ignored as metadata fields; all other columns become typed metadata columns.

        Examples
        ========
        >>> edited = workspace.metadata.copy()
        >>> edited["Batch"] = ["A"] * len(edited)
        >>> workspace.apply_metadata(edited)

        This writes the ``Batch`` column back to the matching Group/Sample rows and
        infers an appropriate metadata type for the column.
        '''
        ...
    def __getitem__(self, grouping: str) -> Grouping:
        '''
        Return a grouping by name.

        Examples
        ========
        >>> experiment = workspace["Experiment"]

        This retrieves the same object represented in ``workspace.groupings`` and
        raises KeyError when the name does not exist.
        '''
        ...

workspace: Workspace
'''
The current global workspace.

Example: ``workspace["Experiment"]`` retrieves the named grouping and makes its
samples, strategy, compensation matrices, and metadata available to the script.
'''

platform: Platform
'''
Current platform when running an embedded platform resource script.
This object is NOT available in the context of an extension script or statistics definition.
For internal use only. Example: ``platform.clear_results()`` prepares an embedded
platform script to publish a fresh set of tables, statistics, and model curves.
'''

class Application:
    '''
    Application-level utilities for script interaction. The application object is available in
    macro, integration, and Python statistic scripts.

    Examples
    ========
    >>> application.log("Starting analysis")
    >>> application.progress(25, "Preparing data")

    This writes a task-log entry and updates the application status bar to report
    deterministic progress from a running script.
    '''

    def log(self, content):
        '''
        Writes an info-level message to the current task log.

        Examples
        ========
        >>> application.log({"rows": 1200})

        This converts the object to text and appends it as an informational log
        entry without interrupting execution.
        '''
        ...

    def warning(self, content):
        '''
        Writes a warning-level message, asks whether to proceed, and cancels the run if rejected.

        Examples
        ========
        >>> application.warning("Only two control samples were found")

        This logs the warning and shows a proceed/cancel prompt. Choosing cancel
        stops the current Python run.
        '''
        ...

    def error(self, content):
        '''
        Writes an error-level message and interrupts the current run.

        Examples
        ========
        >>> application.error("Model fitting failed")

        This records an error and terminates the current script with an application
        error exception.
        '''
        ...

    def msgbox(self, title: str, content: str, buttons: Literal["ok", "ok-cancel", "proceed-cancel", "yes-no-cancel"] = "ok") -> str:
        '''
        Shows a modal message box and returns the selected button value.

        Returns one of "ok", "cancel", "yes", or "no", depending on the requested button set.

        Examples
        ========
        >>> answer = application.msgbox("Continue?", "Run the model now?", "yes-no-cancel")

        This opens a modal prompt and stores the selected button name in ``answer``.
        '''
        ...

    def input(self, requires: list) -> list:
        '''
        Shows a modal input dialog built from requirement declarations and returns a list of
        user-selected values in the same order as the requirement list.

        Examples
        ========
        >>> requirements = [application.require_channel("Marker"), application.require_option("Smooth", True)]
        >>> marker, smooth = application.input(requirements)

        This opens one input dialog containing a channel selector and checkbox and
        returns their values in declaration order.
        '''
        ...

    def require_channel(self, name: str, default: str | list[str] | None = None, multiple: bool = False):
        '''
        Declares a channel selector requirement.

        Examples
        ========
        >>> requirement = application.require_channel("Markers", ["CD3", "CD4"], multiple=True)

        This creates a requirement descriptor for a multi-channel selector; pass it
        to ``application.input`` to collect the user's selection.
        '''
        ...

    def require_integer(self, name: str, default: int = 0, min = None, max = None):
        '''
        Declares an integer numeric input requirement.

        Examples
        ========
        >>> bins = application.require_integer("Bins", 100, min=10, max=1000)

        This describes a bounded integer editor initialized to 100.
        '''
        ...

    def require_float(self, name: str, default: float = 0.0, min = None, max = None):
        '''
        Declares a floating-point numeric input requirement.

        Examples
        ========
        >>> cutoff = application.require_float("Cutoff", 0.5, min=0.0, max=1.0)

        This describes a bounded floating-point editor initialized to 0.5.
        '''
        ...

    def require_enum(self, name: str, possible_values: list[str], default: str | None = None):
        '''
        Declares a single-choice input requirement.

        Examples
        ========
        >>> method = application.require_enum("Method", ["Linear", "Logicle"], "Logicle")

        This describes a combo-box input restricted to the supplied values.
        '''
        ...

    def require_option(self, name: str, default: bool = False):
        '''
        Declares a checkbox input requirement.

        Examples
        ========
        >>> smoothing = application.require_option("Use smoothing", True)

        This describes a checked Boolean option for ``application.input``.
        '''
        ...

    def progress(self, percentage: float, description: str = ""):
        '''
        Updates the application status bar with deterministic script progress.
        percentage is clamped to 0..100.

        Examples
        ========
        >>> application.progress(80, "Rendering model curves")

        This displays 80 percent completion and the supplied status description.
        '''
        ...

application: Application
'''
Application utility instance.

Example: ``application.log("Done")`` appends an informational message to the
current task log without changing workspace data.
'''
