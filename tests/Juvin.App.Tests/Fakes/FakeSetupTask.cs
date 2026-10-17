using Juvin.Platform.Setup;

namespace Juvin.App.Tests.Fakes;

/// <summary>
///     A setup task whose state the test sets directly. Its action applies <see cref="StateAfterAction" />.
///     <see cref="Evaluation" /> and <see cref="ActionGate" /> let a test hold a check or an action open.
/// </summary>
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

    /// <summary>Answers the numbered evaluation, counting from 1, in place of the current state.</summary>
    public Func<int, Task<SetupTaskState>>? Evaluation { get; init; }

    public Task? ActionGate { get; init; }
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
        return Evaluation?.Invoke(EvaluateCount) ?? Task.FromResult(State);
    }

    public async Task<SetupActionOutcome> RunActionAsync(CancellationToken ct)
    {
        ActionCount++;
        if (ActionGate is not null)
        {
            await ActionGate;
        }

        if (StateAfterAction is not null)
        {
            State = StateAfterAction;
        }

        return new SetupActionOutcome(true, "Done");
    }
}
