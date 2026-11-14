using Quaymaster.Application.Errors;

namespace Quaymaster.Application.Security;

/// <summary>Login throttling thresholds (identical to the original server defaults).</summary>
public sealed record LoginLimitOptions
{
    /// <summary>Attempts allowed per client IP inside <see cref="IpWindow"/> (checked before any hashing).</summary>
    public int IpMaxAttempts { get; init; } = 20;
    public TimeSpan IpWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Consecutive failures for one (account, ip) pair before a hard lock.</summary>
    public int PairLockAfter { get; init; } = 5;
    public TimeSpan PairLockBase { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PairLockMax { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Progressive per-account delay applied to FAILED responses only (never blocks a correct login).</summary>
    public int AccountDelayAfter { get; init; } = 2;
    public TimeSpan AccountDelayMax { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan AccountDecay { get; init; } = TimeSpan.FromMinutes(15);

    public int MaxEntries { get; init; } = 10_000;
}

public interface ILoginThrottle
{
    /// <summary>Throws a 429 <see cref="AppException"/> when the caller must not even be allowed to try. Records the attempt.</summary>
    void BeforeAttempt(string account, string ip);

    /// <summary>Records a failure and returns the delay to apply before answering.</summary>
    TimeSpan OnFailure(string account, string ip);

    void OnSuccess(string account, string ip);
}

/// <summary>
/// Login throttling: per-IP sliding-window budget evaluated BEFORE the expensive password hash;
/// per-(account, ip) hard lock with exponential back-off; per-account only a capped progressive delay on failed
/// responses, so a remote attacker can slow guesses but can never lock the real admin out.
/// State is in memory (a restart clears it), same as the original.
/// </summary>
public sealed class LoginLimiter(TimeProvider time, LoginLimitOptions? options = null) : ILoginThrottle
{
    private readonly LoginLimitOptions _o = options ?? new LoginLimitOptions();
    private readonly Lock _gate = new();
    private readonly System.Collections.Generic.OrderedDictionary<string, List<long>> _ips = new(StringComparer.Ordinal);
    private readonly System.Collections.Generic.OrderedDictionary<string, PairState> _pairs = new(StringComparer.Ordinal);
    private readonly System.Collections.Generic.OrderedDictionary<string, AccountState> _accounts = new(StringComparer.Ordinal);

    private sealed class PairState
    {
        public int Fails;
        public long LockedUntil;
    }

    private sealed class AccountState
    {
        public int Fails;
        public long Last;
    }

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    private void Evict<T>(System.Collections.Generic.OrderedDictionary<string, T> map)
    {
        while (map.Count > _o.MaxEntries)
        {
            map.RemoveAt(0);
        }
    }

    public void BeforeAttempt(string account, string ip)
    {
        lock (_gate)
        {
            var now = Now();
            var windowMs = (long)_o.IpWindow.TotalMilliseconds;
            var hits = _ips.TryGetValue(ip, out var existing) ? existing.Where(t => t > now - windowMs).ToList() : [];
            if (hits.Count >= _o.IpMaxAttempts)
            {
                var retry = TimeSpan.FromMilliseconds((hits[0] + windowMs) - now);
                throw AppException.RateLimited("Too many attempts from this address", retry);
            }

            hits.Add(now);
            _ips[ip] = hits;
            Evict(_ips);

            if (_pairs.TryGetValue(PairKey(account, ip), out var pair) && pair.LockedUntil > now)
            {
                throw AppException.RateLimited("Too many failed attempts; try again later", TimeSpan.FromMilliseconds(pair.LockedUntil - now));
            }
        }
    }

    public TimeSpan OnFailure(string account, string ip)
    {
        lock (_gate)
        {
            var now = Now();
            var pk = PairKey(account, ip);
            if (!_pairs.TryGetValue(pk, out var pair))
            {
                pair = new PairState();
            }
            pair.Fails++;
            if (pair.Fails >= _o.PairLockAfter)
            {
                var exp = Math.Min(pair.Fails - _o.PairLockAfter, 30);
                var lockMs = Math.Min(_o.PairLockMax.TotalMilliseconds, _o.PairLockBase.TotalMilliseconds * Math.Pow(2, exp));
                pair.LockedUntil = now + (long)lockMs;
            }

            _pairs[pk] = pair;
            Evict(_pairs);

            var ak = account.ToLowerInvariant();
            if (!_accounts.TryGetValue(ak, out var acc) || now - acc.Last > (long)_o.AccountDecay.TotalMilliseconds)
            {
                acc = new AccountState();
            }

            acc.Fails++;
            acc.Last = now;
            _accounts[ak] = acc;
            Evict(_accounts);

            if (acc.Fails <= _o.AccountDelayAfter)
            {
                return TimeSpan.Zero;
            }
            var exponent = Math.Min(acc.Fails - _o.AccountDelayAfter - 1, 30);
            return TimeSpan.FromMilliseconds(Math.Min(_o.AccountDelayMax.TotalMilliseconds, 500d * Math.Pow(2, exponent)));
        }
    }

    public void OnSuccess(string account, string ip)
    {
        lock (_gate)
        {
            _pairs.Remove(PairKey(account, ip));
            _accounts.Remove(account.ToLowerInvariant());
        }
    }

    private static string PairKey(string account, string ip) => account.ToLowerInvariant() + "\0" + ip;
}

/// <summary>Fixed attempts-per-key budget, used for the setup token (5 attempts per IP per hour).</summary>
public sealed class AttemptBudget(int max, TimeSpan window, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly System.Collections.Generic.OrderedDictionary<string, List<long>> _hits = new(StringComparer.Ordinal);

    public void Take(string key)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            var windowMs = (long)window.TotalMilliseconds;
            var hits = _hits.TryGetValue(key, out var existing) ? existing.Where(t => t > now - windowMs).ToList() : [];
            if (hits.Count >= max)
            {
                throw AppException.RateLimited("Too many attempts", window);
            }
            hits.Add(now);
            _hits[key] = hits;
            while (_hits.Count > 10_000)
            {
                _hits.RemoveAt(0);
            }
        }
    }
}
