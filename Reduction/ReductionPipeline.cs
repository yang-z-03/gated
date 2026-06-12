using System;

namespace gated.Reduction;

public interface IReductionPipelineState
{
    float[,] Input { get; }
    float[,]? LogicleNormalized { get; }
    CytoNormFitResult? CytoNormModel { get; }
    float[,]? CytoNormNormalized { get; }
}

public sealed class ReductionPipelineState : IReductionPipelineState
{
    public ReductionPipelineState(float[,] input)
    {
        MatrixUtilities.Validate(input, nameof(input));
        Input = MatrixUtilities.Copy(input);
    }

    public float[,] Input { get; }
    public float[,]? LogicleNormalized { get; internal set; }
    public CytoNormFitResult? CytoNormModel { get; internal set; }
    public float[,]? CytoNormNormalized { get; internal set; }

    public float[,] CurrentMatrix => CytoNormNormalized ?? LogicleNormalized ?? Input;
}

public sealed class ReductionPipeline
{
    private readonly ReductionPipelineState state;

    public ReductionPipeline(float[,] input)
    {
        state = new ReductionPipelineState(input);
    }

    public IReductionPipelineState State => state;

    public ReductionPipeline ApplyLogicle(LogicleNormalizationOptions? options = null, bool force = false)
    {
        if (force || state.LogicleNormalized is null)
            state.LogicleNormalized = LogicleNormalization.Transform(state.CurrentMatrix, options);
        return this;
    }

    public ReductionPipeline FitCytoNorm(float[,] reference_data, int[] reference_batches, CytoNormOptions? options = null, bool force = false)
    {
        if (force || state.CytoNormModel is null)
            state.CytoNormModel = CytoNorm.Fit(reference_data, reference_batches, options);
        return this;
    }

    public ReductionPipeline ApplyCytoNorm(int[] batches, bool force = false)
    {
        if (state.CytoNormModel is null)
            throw new InvalidOperationException("CytoNorm must be fit before it can be applied.");
        if (force || state.CytoNormNormalized is null)
            state.CytoNormNormalized = CytoNorm.Normalize(state.CytoNormModel, state.CurrentMatrix, batches);
        return this;
    }
}
