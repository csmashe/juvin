using Juvin.Core.Diagnostics;

namespace Juvin.Core.Tests;

public sealed class LogFileTraceListenerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void WriteLine_AppendsTimestampedLine()
    {
        using (var listener = new LogFileTraceListener(_temp.Path))
        {
            listener.Write("[Test] first ");
            listener.WriteLine("half");
            listener.WriteLine("[Test] second");
        }

        var lines = File.ReadAllLines(_temp.File(LogFileTraceListener.FileName));
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[Test\] first half$", lines[0]);
        Assert.EndsWith(" [Test] second", lines[1]);
    }

    [Fact]
    public void Open_MovesTheLastLogAside()
    {
        File.WriteAllText(_temp.File(LogFileTraceListener.PreviousFileName), "oldest");
        File.WriteAllText(_temp.File(LogFileTraceListener.FileName), "last launch");

        using (var listener = new LogFileTraceListener(_temp.Path))
        {
            listener.WriteLine("this launch");
        }

        Assert.Equal("last launch", File.ReadAllText(_temp.File(LogFileTraceListener.PreviousFileName)));
        Assert.EndsWith("this launch" + Environment.NewLine, File.ReadAllText(_temp.File(LogFileTraceListener.FileName)));
        Assert.Equal(2, Directory.GetFiles(_temp.Path).Length);
    }

    [Fact]
    public void WriteLine_StartsANewFileAtTheSizeLimit()
    {
        using (var listener = new LogFileTraceListener(_temp.Path, maxBytes: 100))
        {
            listener.WriteLine(new string('a', 120));
            listener.WriteLine("after the limit");
        }

        Assert.Contains(new string('a', 120), File.ReadAllText(_temp.File(LogFileTraceListener.PreviousFileName)));
        var current = File.ReadAllLines(_temp.File(LogFileTraceListener.FileName));
        Assert.EndsWith(" after the limit", Assert.Single(current));
    }

    [Fact]
    public void Open_ThrowsWhenTheFolderIsMissing()
    {
        Assert.ThrowsAny<IOException>(() => new LogFileTraceListener(_temp.File("missing")));
    }
}
