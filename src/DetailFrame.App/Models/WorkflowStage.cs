namespace DetailFrame.App.Models;

public enum WorkflowStage
{
    ChooseImage,
    Configure,
    Enhance,
    Compare,
    Save,
}

public enum StageStatus
{
    Blocked,
    Available,
    Completed,
}

public static class WorkflowStages
{
    public static IReadOnlyList<WorkflowStage> All { get; } = Enum.GetValues<WorkflowStage>();

    public static string Title(WorkflowStage stage) => stage switch
    {
        WorkflowStage.ChooseImage => "Choose image",
        WorkflowStage.Configure => "Configure",
        WorkflowStage.Enhance => "Enhance",
        WorkflowStage.Compare => "Compare",
        WorkflowStage.Save => "Save",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    public static bool IsAvailable(WorkflowState state, WorkflowStage stage) => stage switch
    {
        WorkflowStage.ChooseImage => true,
        WorkflowStage.Configure => state.CanConfigure,
        WorkflowStage.Enhance => state.CanEnhance,
        WorkflowStage.Compare => state.CanCompare,
        WorkflowStage.Save => state.CanSave,
        _ => false,
    };

    public static StageStatus GetStatus(WorkflowState state, WorkflowStage stage)
    {
        if (!IsAvailable(state, stage))
        {
            return StageStatus.Blocked;
        }

        var completed = stage switch
        {
            WorkflowStage.ChooseImage => state.CanConfigure,
            WorkflowStage.Enhance => state.CanCompare,
            WorkflowStage.Configure => state.CanCompare,
            _ => false,
        };
        return completed ? StageStatus.Completed : StageStatus.Available;
    }
}
