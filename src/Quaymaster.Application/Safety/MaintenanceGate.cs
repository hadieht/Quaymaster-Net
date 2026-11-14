using Quaymaster.Application.Errors;

namespace Quaymaster.Application.Safety;

/// <summary>
/// The maintenance gate (audit H-03): while a run (or a restore) owns a Compose project, manual mutations of it are refused with a
/// 409 <c>conflict</c> - except from the owning run's own async flow. Enforced in <see cref="MutationGateway"/>, the single door
/// for engine and Compose mutations.
/// </summary>
public interface IMaintenanceGate
{
    /// <summary>Marks <paramref name="project"/> as owned by the calling async flow until the result is disposed.</summary>
    IDisposable Own(string project);

    /// <summary>Throws 409 <c>conflict</c> when a run owns <paramref name="project"/> and the caller is not that run.</summary>
    void Assert(string? project);

    bool IsOwned(string project);
}

public sealed class MaintenanceGate : IMaintenanceGate
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _owned = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _current = new();

    public IDisposable Own(string project)
    {
        ArgumentNullException.ThrowIfNull(project);
        lock (_gate)
        {
            _owned[project] = _owned.GetValueOrDefault(project) + 1;
        }

        _current.Value = project;
        return new Release(this, project);
    }

    public void Assert(string? project)
    {
        if (project is null || !IsOwned(project) || string.Equals(_current.Value, project, StringComparison.Ordinal))
        {
            return;
        }

        throw AppException.Conflict($"A maintenance run is updating project \"{project}\"; wait for it to finish");
    }

    public bool IsOwned(string project)
    {
        lock (_gate)
        {
            return _owned.ContainsKey(project);
        }
    }

    private void Drop(string project)
    {
        lock (_gate)
        {
            var n = _owned.GetValueOrDefault(project) - 1;
            if (n <= 0)
            {
                _owned.Remove(project);
            }
            else
            {
                _owned[project] = n;
            }
        }

        _current.Value = null;
    }

    private sealed class Release(MaintenanceGate gate, string project) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                gate.Drop(project);
            }
        }
    }
}
