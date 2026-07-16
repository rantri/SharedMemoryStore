using System.Diagnostics;

internal sealed class ProbeProcessRegistry
{
    private readonly object _gate = new();
    private readonly List<Process> _processes = [];
    private bool _accepting = true;

    internal Process Start(Func<Process> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        lock (_gate)
        {
            if (!_accepting)
            {
                throw new InvalidOperationException("The probe trial is already terminating.");
            }

            Process process = start();
            _processes.Add(process);
            return process;
        }
    }

    internal Process[] StopAcceptingAndSnapshot()
    {
        lock (_gate)
        {
            _accepting = false;
            return _processes.ToArray();
        }
    }
}
