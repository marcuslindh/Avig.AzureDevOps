namespace Avig.AzureDevOps.WorkItems;

/// <summary>
/// The reference names of the work item fields the client reads and writes.
/// </summary>
/// <remarks>
/// The names are Azure DevOps' own and the same in all process templates, except the estimate,
/// which is named differently in Agile, Scrum and CMMI.
/// </remarks>
internal static class FieldNames
{
    /// <summary>The type: Bug, User Story, Task …</summary>
    public const string WorkItemType = "System.WorkItemType";

    /// <summary>The title.</summary>
    public const string Title = "System.Title";

    /// <summary>The state in the process.</summary>
    public const string State = "System.State";

    /// <summary>The person who owns the work item.</summary>
    public const string AssignedTo = "System.AssignedTo";

    /// <summary>The sprint, with the project first.</summary>
    public const string IterationPath = "System.IterationPath";

    /// <summary>The area, with the project first.</summary>
    public const string AreaPath = "System.AreaPath";

    /// <summary>The tags, separated by semicolons.</summary>
    public const string Tags = "System.Tags";

    /// <summary>The parent. Read-only; written as a relation.</summary>
    public const string Parent = "System.Parent";

    /// <summary>When it was last changed.</summary>
    public const string ChangedDate = "System.ChangedDate";

    /// <summary>The column on the kanban board.</summary>
    public const string BoardColumn = "System.BoardColumn";

    /// <summary>The description, as HTML.</summary>
    public const string Description = "System.Description";

    /// <summary>The steps that reproduce the bug. Only bugs have it.</summary>
    public const string ReproSteps = "Microsoft.VSTS.TCM.ReproSteps";

    /// <summary>The acceptance criteria. Only user stories have it.</summary>
    public const string AcceptanceCriteria = "Microsoft.VSTS.Common.AcceptanceCriteria";

    /// <summary>The estimate in Agile.</summary>
    public const string StoryPoints = "Microsoft.VSTS.Scheduling.StoryPoints";

    /// <summary>The estimate in Scrum.</summary>
    public const string Effort = "Microsoft.VSTS.Scheduling.Effort";

    /// <summary>The estimate in CMMI.</summary>
    public const string Size = "Microsoft.VSTS.Scheduling.Size";
}
