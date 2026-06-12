using gated.Models;

namespace gated.Python;

public sealed class PythonWorkspaceContext
{
    public PythonWorkspaceContext(FlowWorkspace workspace)
    {
        workspace_wrapper = new Workspace(workspace);
    }

    private readonly Workspace workspace_wrapper;

    public Workspace workspace => workspace_wrapper;

    public void execute(string code) => PythonExtensionRuntime.Execute(code, workspace_wrapper);
}
