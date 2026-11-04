namespace DetailFrame.App.Models;

public sealed record WorkflowState
{
    public static WorkflowState Empty { get; } = new();

    public string? InputPath { get; init; }
    public string? SelectedModelId { get; init; }
    public int? SelectedScale { get; init; }
    public string? SelectedDevice { get; init; }
    public string? ActiveJobId { get; init; }
    public string? ResultPath { get; init; }

    public bool CanEnhance =>
        !string.IsNullOrWhiteSpace(InputPath)
        && !string.IsNullOrWhiteSpace(SelectedModelId)
        && SelectedScale is > 0
        && !string.IsNullOrWhiteSpace(SelectedDevice)
        && string.IsNullOrWhiteSpace(ActiveJobId);

    public bool CanConfigure => !string.IsNullOrWhiteSpace(InputPath);
    public bool CanCompare => !string.IsNullOrWhiteSpace(ResultPath);
    public bool CanSave => !string.IsNullOrWhiteSpace(ResultPath);
}
