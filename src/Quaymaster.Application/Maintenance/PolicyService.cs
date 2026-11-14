using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Maintenance;
using Quaymaster.Domain.Registry;

namespace Quaymaster.Application.Maintenance;

/// <summary>Effective-policy lookup used by the planner (and, in T4b, by the executor and the scheduler).</summary>
public interface IPolicyResolver
{
    Task<EffectivePolicy> ResolveAsync(string? project, string? service, string? container, CancellationToken ct);
}

/// <summary>An HTTP probe is only saved once it passes against the CURRENT version (RT-10). Throws 400 <c>validation</c> otherwise.</summary>
public interface IProbeVerifier
{
    Task VerifyAsync(string url, int expectStatus, CancellationToken ct);
}

/// <summary>Policies per scope (global / project / service / container) and their merge into the effective policy.</summary>
public sealed partial class PolicyService(IPolicyStore store, IMutationGateway gateway, IProbeVerifier probes, TimeProvider time) : IPolicyResolver
{
    private static readonly string[] Kinds = ["global", "project", "service", "container"];

    public async Task<PolicyListDto> ListAsync(CancellationToken ct) =>
        new([.. (await store.ListAsync(ct)).Select(p => new PolicyDto(p.Kind, p.Key, PolicyRules.ToJson(p.Policy), ImageReferences.Iso(p.UpdatedAt)))]);

    public async Task<EffectivePolicy> ResolveAsync(string? project, string? service, string? container, CancellationToken ct)
    {
        var levels = new List<PolicyLevel>();
        foreach (var (kind, key) in PolicyRules.ChainFor(project, service, container))
        {
            if (await store.GetAsync(kind, key, ct) is { } rec)
            {
                levels.Add(new PolicyLevel($"{kind}:{key}", rec.Policy));
            }
        }

        return PolicyRules.Resolve(levels);
    }

    public async Task<PolicyPutDto> PutAsync(string kind, string rawKey, JsonNode? body, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var key = Key(kind, rawKey);

        // RT-03: a policy for something outside QM_MANAGED_SCOPE could only ever be dead or misleading config
        var scopeName = kind == "service" ? key.Split('/')[0] : kind == "global" ? null : key;
        if (scopeName is not null && !gateway.Policy.InScope(scopeName))
        {
            throw new AppException(409, ErrorCode.OutOfScope, $"Refused: \"{scopeName}\" is outside QM_MANAGED_SCOPE, so no policy can be set for it");
        }

        PolicyInput policy;
        try
        {
            policy = PolicyRules.Parse(body);
        }
        catch (PolicyValidationException e)
        {
            throw AppException.BadRequest(e.Message);
        }

        var json = PolicyRules.ToJson(policy);
        return await gateway.AuditedAsync(new AuditScope(actor, "maintenance.policy.put", $"{kind}:{key}", json), async () =>
        {
            if (policy.Health is { ProbeSet: true, Probe: { } p })
            {
                await probes.VerifyAsync(p.Url, p.ExpectStatus, ct);
            }

            await store.PutAsync(kind, key, policy, time.GetUtcNow().ToUnixTimeMilliseconds(), ct);
            return new PolicyPutDto(kind, key, json);
        }, ct);
    }

    public async Task<OkDto> DeleteAsync(string kind, string rawKey, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var key = Key(kind, rawKey);
        return await gateway.AuditedAsync(new AuditScope(actor, "maintenance.policy.delete", $"{kind}:{key}", null), async () =>
        {
            if (!await store.DeleteAsync(kind, key, ct))
            {
                throw AppException.NotFound("Policy");
            }

            return new OkDto(true);
        }, ct);
    }

    /// <summary>Port of <c>policyKey()</c>: unknown kind and malformed keys are 400; a global policy is always keyed <c>*</c>.</summary>
    private static string Key(string kind, string key)
    {
        key = Uri.UnescapeDataString(key); // "shop%2Fweb" reaches the route unescaped-for-slash only; the original decodeURIComponent()s
        if (!Kinds.Contains(kind, StringComparer.Ordinal))
        {
            throw AppException.BadRequest("Unknown policy scope");
        }

        if (kind == "global")
        {
            return "*";
        }

        if (kind == "service" && !ServiceKeyRe().IsMatch(key))
        {
            throw AppException.BadRequest("service policies are keyed \"project/service\"");
        }

        if (kind == "project" && !ProjectKeyRe().IsMatch(key))
        {
            throw AppException.BadRequest("invalid project name");
        }

        if (string.IsNullOrEmpty(key) || key.Length > 200)
        {
            throw AppException.BadRequest("invalid key");
        }

        return key;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ServiceKeyRe();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ProjectKeyRe();
}
