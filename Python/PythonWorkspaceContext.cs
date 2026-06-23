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

    public void execute(string code, string task_key = "macro:interactive", string task_name = "Interactive macro") =>
        PythonExtensionRuntime.Execute(code, workspace_wrapper, task_key, task_name);
}
