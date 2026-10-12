namespace GameClientTestHarness;

/// <summary>Prints each step of a run as it passes or fails, and tracks the exit code.</summary>
internal sealed class Steps
{
    private int _number;
    private bool _failed;

    public void Ok(string message)
    {
        Console.WriteLine($"  [{++_number}] ✓ {message}");
    }

    public void Fail(string message)
    {
        Console.WriteLine($"  [{++_number}] ✗ {message}");
        _failed = true;
    }

    public void Info(string message) => Console.WriteLine($"      {message}");

    public int Finish(string what)
    {
        Console.WriteLine();
        Console.WriteLine(_failed ? $"FAILED: {what}" : $"PASSED: {what}");
        return _failed ? 1 : 0;
    }
}
