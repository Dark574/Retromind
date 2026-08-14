namespace Retromind.Tests.TestInfrastructure;

internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly List<(string Name, string? OriginalValue)> _originalValues = new();
    private bool _disposed;

    public EnvironmentVariableScope(params (string Name, string? Value)[] variables)
    {
        foreach (var (name, value) in variables)
        {
            _originalValues.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        for (var index = _originalValues.Count - 1; index >= 0; index--)
        {
            var (name, originalValue) = _originalValues[index];
            Environment.SetEnvironmentVariable(name, originalValue);
        }
    }
}
