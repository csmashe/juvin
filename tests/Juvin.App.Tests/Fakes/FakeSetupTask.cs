using Juvin.Platform.Setup;

namespace Juvin.App.Tests.Fakes;

/// <summary>A setup task whose state the test sets directly. Its action applies <see cref="StateAfterAction" />.</summary>
public sealed class FakeSetupTask : ISetupTask
{
    public FakeSetupTask(string id, SetupTaskSeverity severity, SetupTaskStatusKind initial)
    {
        Id = id;
        Severity = severity;
        State = new SetupTaskState(initial, id, ActionLabel: "Fix");
    }

    private SetupTaskState State { get; set; }
    public SetupTaskState? StateAfterAction { get; init; }
    public bool Applies { get; init; } = true;
    public int EvaluateCount { get; private set; }
    public int ActionCount { get; private set; }

    public string Id { get; }
    public string Title => Id;
    public SetupTaskSeverity Severity { get; }

    public bool AppliesToThisMachine() => Applies;

    public Task<SetupTaskState> EvaluateAsync(CancellationToken ct)
    {
        EvaluateCount++;
        return Task.FromResult(State);
    }

    public Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        ActionCount++;
        if (StateAfterAction is not null)
        {
            State = StateAfterAction;
        }

        return Task.FromResult(new SetupActionOutcome(true, "Done"));
    }
}
