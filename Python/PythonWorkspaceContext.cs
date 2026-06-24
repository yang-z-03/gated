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

    public void execute(string code, string task_key = "code:interactive", string task_name = "Interactive code") =>
        PythonExtensionRuntime.Execute(code, workspace_wrapper, task_key, task_name);
}
