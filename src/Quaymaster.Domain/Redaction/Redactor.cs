using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Quaymaster.Domain.Redaction;

/// <summary>Options for a redaction pass.</summary>
/// <param name="RedactAllEnv">Mask every environment value (<c>QM_REDACT_ALL_ENV</c>).</param>
/// <param name="Sink">Receives every secret value found (for known-value scrubbing).</param>
public sealed record RedactOptions(bool RedactAllEnv = false, Action<string>? Sink = null);

/// <summary>
/// Redaction (spec 6.3 as amended by RT-04). Everything that leaves the process - API responses, SSE frames, persisted
/// events and operation output, logs - goes through here. There is no per-section list: JSON is walked at every depth,
/// and known secret values are additionally scrubbed from free text (see <see cref="Scrubber"/>).
/// Behaviour port of the original <c>redaction/redact.ts</c>.
/// </summary>
public static partial class Redactor
{
    public const string Mask = "••••••";

    private const int MaxDepth = 40;

    private static readonly RegexOptions Opts = RegexOptions.CultureInvariant;

    /// <summary>Key names (case-insensitive substring match) whose values are treated as secrets.</summary>
    private static readonly Regex SecretKeyRe = new(
        "pass|pwd|secret|token|key|credential|auth|private|cert|dsn|connection|cookie|session|salt|signature|webhook|psk|seed|mnemonic|passphrase|otp|bearer|apikey|access|license|encrypt|hash|hmac",
        Opts | RegexOptions.IgnoreCase);

    private static readonly Regex SafeKeyRe = new(
        @"^(sandboxkey|confighash|planhash|approvedplanhash|fromimageid|toimageid|com\.docker\.compose\.(config-hash|image)|org\.opencontainers\.image\..+)$",
        Opts | RegexOptions.IgnoreCase);

    private static readonly Regex QuerySecretRe = new(
        SecretKeyRe.ToString() + "|^sig$|^s?sig(nature)?$|^code$",
        Opts | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> EnvKeys = new(StringComparer.Ordinal) { "env", "environment", "labels", "label" };
    private static readonly HashSet<string> AllEnvKeys = new(StringComparer.Ordinal) { "env", "environment" };

    public static bool IsSecretKey(string key) => !SafeKeyRe.IsMatch(key) && SecretKeyRe.IsMatch(key);

    // ---------------------------------------------------------------------------------------------------------
    // Value heuristics
    // ---------------------------------------------------------------------------------------------------------

    private static readonly Regex JwtRe = new(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", Opts);
    private static readonly Regex PemRe = new(@"-----BEGIN [A-Z0-9 ]+-----[\s\S]*?(?:-----END [A-Z0-9 ]+-----|$)", Opts);

    private static readonly Regex[] PrefixRes =
    [
        new(@"\bgh[pousr]_[A-Za-z0-9]{20,}\b", Opts),
        new(@"\bgithub_pat_[A-Za-z0-9_]{20,}\b", Opts),
        new(@"\bglpat-[A-Za-z0-9_-]{16,}\b", Opts),
        new(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", Opts),
        new(@"\bsk-[A-Za-z0-9_-]{16,}\b", Opts),
        new(@"\b[sr]k_(?:live|test)_[A-Za-z0-9]{16,}\b", Opts),
        new(@"\bAKIA[0-9A-Z]{16}\b", Opts),
        new(@"\bAIza[0-9A-Za-z_-]{35}\b", Opts),
        new(@"\bnpm_[A-Za-z0-9]{36}\b", Opts),
        new(@"\bdckr_pat_[A-Za-z0-9_-]{16,}\b", Opts),
        new(@"\bhf_[A-Za-z0-9]{30,}\b", Opts),
        new(@"\bSG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}\b", Opts),
        new(@"\b\d{6,}:[A-Za-z0-9_-]{30,}\b", Opts), // telegram bot token
    ];

    private static readonly Regex BearerRe = new(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}", Opts | RegexOptions.IgnoreCase);
    private static readonly Regex UuidRe = new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", Opts | RegexOptions.IgnoreCase);
    private static readonly Regex TokenCharsRe = new(@"^[A-Za-z0-9+/_=-]+$", Opts);

    private static double Shannon(string s)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in s)
        {
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        var h = 0.0;
        foreach (var n in counts.Values)
        {
            var p = (double)n / s.Length;
            h -= p * Math.Log2(p);
        }

        return h;
    }

    /// <summary>
    /// True for values that look like a secret regardless of their key: JWTs, PEM blocks, known token prefixes, or a
    /// long high-entropy string. Digest/image-ID references and UUIDs are never considered secrets.
    /// </summary>
    public static bool LooksLikeSecretValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var v = value.Trim();
        if (v.Length < 8 || v == Mask)
        {
            return false;
        }

        if (v.StartsWith("sha256:", StringComparison.Ordinal) || UuidRe.IsMatch(v) || v.StartsWith('/'))
        {
            return false;
        }

        if (v.Contains("-----BEGIN ", StringComparison.Ordinal))
        {
            return true;
        }

        if (JwtRe.IsMatch(v) || PrefixRes.Any(r => r.IsMatch(v)))
        {
            return true;
        }

        if (TokenCharsRe.IsMatch(v) && v.Length >= 20)
        {
            var classes =
                (v.Any(char.IsAsciiLetterLower) ? 1 : 0)
                + (v.Any(char.IsAsciiLetterUpper) ? 1 : 0)
                + (v.Any(char.IsAsciiDigit) ? 1 : 0)
                + (v.Any(c => "+/_=-".Contains(c, StringComparison.Ordinal)) ? 1 : 0);
            var h = Shannon(v);
            if (classes >= 3 && h >= 3.7)
            {
                return true;
            }

            if (v.Length >= 32 && classes >= 2 && h >= 3.5)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------------------------
    // URLs
    // ---------------------------------------------------------------------------------------------------------

    private static readonly Regex UrlPartsRe = new(@"^([a-z][a-z0-9+.-]*://)([^/?#]*@)?([^/?#]*)([^?#]*)(\?[^#]*)?(#.*)?$", Opts | RegexOptions.IgnoreCase);
    private static readonly Regex PortSuffixRe = new(@":\d+$", Opts);
    private static readonly Regex DiscordHostRe = new(@"(^|\.)(discord(app)?\.com)$", Opts);
    private static readonly Regex TelegramBotRe = new(@"^bot(.+)$", Opts);
    private static readonly Regex OpaqueSegRe = new(@"^[A-Za-z0-9_-]+$", Opts);
    private static readonly Regex HttpSchemeRe = new(@"^https?:", Opts | RegexOptions.IgnoreCase);

    private static string MaskPath(string host, string pathPart, Action<string>? sink)
    {
        var h = PortSuffixRe.Replace(host.ToLowerInvariant(), string.Empty);
        var segs = pathPart.Split('/');

        void MaskSeg(int i)
        {
            if (i < 0 || i >= segs.Length)
            {
                return;
            }

            var s = segs[i];
            if (s.Length == 0 || s == Mask)
            {
                return;
            }

            sink?.Invoke(s);
            segs[i] = Mask;
        }

        if (DiscordHostRe.IsMatch(h))
        {
            var i = Array.IndexOf(segs, "webhooks");
            if (i >= 0)
            {
                MaskSeg(i + 2);
                return string.Join('/', segs);
            }
        }

        if (h == "hooks.slack.com")
        {
            var i = Array.IndexOf(segs, "services");
            if (i >= 0)
            {
                for (var j = i + 1; j < segs.Length; j++)
                {
                    MaskSeg(j);
                }
            }

            return string.Join('/', segs);
        }

        if (h == "api.telegram.org")
        {
            for (var j = 0; j < segs.Length; j++)
            {
                var m = TelegramBotRe.Match(segs[j]);
                if (m.Success && m.Groups[1].Value.Length > 0 && m.Groups[1].Value != Mask)
                {
                    sink?.Invoke(m.Groups[1].Value);
                    segs[j] = "bot" + Mask;
                }
            }

            return string.Join('/', segs);
        }

        for (var j = 0; j < segs.Length; j++)
        {
            var s = segs[j];
            if (s.Length >= 20 && OpaqueSegRe.IsMatch(s) && LooksLikeSecretValue(s))
            {
                MaskSeg(j);
            }
        }

        return string.Join('/', segs);
    }

    /// <summary>Masks userinfo, secret-named query parameters and secrets embedded in well-known webhook paths.</summary>
    public static string RedactUrl(string url, Action<string>? sink = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        var m = UrlPartsRe.Match(url);
        if (!m.Success)
        {
            return url;
        }

        var scheme = m.Groups[1].Value;
        var host = m.Groups[3].Value;
        var pathPart = m.Groups[4].Value;
        var sb = new StringBuilder(scheme);
        if (m.Groups[2].Success && m.Groups[2].Value.Length > 0)
        {
            var raw = m.Groups[2].Value[..^1];
            if (raw != Mask)
            {
                var parts = raw.Split(':');
                var user = parts[0];
                if (parts.Length > 1)
                {
                    sink?.Invoke(string.Join(':', parts.Skip(1)));
                }
                else if (user.Length > 0)
                {
                    sink?.Invoke(user);
                }

                if (user.Length > 0 && parts.Length > 1)
                {
                    sink?.Invoke(user);
                }
            }

            sb.Append(Mask).Append('@');
        }

        sb.Append(host);
        sb.Append(HttpSchemeRe.IsMatch(scheme) ? MaskPath(host, pathPart, sink) : pathPart);
        if (m.Groups[5].Success && m.Groups[5].Value.Length > 0)
        {
            var query = m.Groups[5].Value[1..];
            var rebuilt = query.Split('&').Select(p =>
            {
                var eq = p.IndexOf('=', StringComparison.Ordinal);
                if (eq < 0)
                {
                    return p;
                }

                var k = p[..eq];
                var v = p[(eq + 1)..];
                string key;
                try
                {
                    key = Uri.UnescapeDataString(k);
                }
                catch (UriFormatException)
                {
                    key = k;
                }

                if (v.Length > 0 && v != Mask && QuerySecretRe.IsMatch(key) && !SafeKeyRe.IsMatch(key))
                {
                    sink?.Invoke(v);
                    return $"{k}={Mask}";
                }

                return p;
            });
            sb.Append('?').Append(string.Join('&', rebuilt));
        }

        sb.Append(m.Groups[6].Value);
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Free text
    // ---------------------------------------------------------------------------------------------------------

    private static readonly Regex UrlInTextRe = new(@"\b[a-zA-Z][a-zA-Z0-9+.-]*://[^\s'""<>`]+", Opts);
    private static readonly Regex FlagRe = new(@"(--?[A-Za-z][\w.-]*)(=|[ \t]+)(?!-)(""[^""]*""|'[^']*'|\S+)", Opts);
    private static readonly Regex AssignRe = new(@"(^|[\s;&|(,{""'])([A-Za-z_][\w.-]*)=(""[^""]*""|'[^']*'|[^\s;&|)'"",}]+)", Opts);
    private static readonly Regex UserFlagRe = new(@"(^|\s)(-u|--user|--proxy-user)([ =])([^\s:'""]+):(""[^""]*""|'[^']*'|\S+)", Opts);
    private static readonly Regex HeaderRe = new(@"(^|[\s{""',(])([A-Za-z][\w.-]*)(:[ \t]+)(?!Bearer\b|Basic\b)(""[^""]*""|'[^']*'|[^\s""',;)}]+)", Opts);
    private static readonly Regex JsonPairRe = new(@"([""'])([A-Za-z_][\w.-]*)\1\s*:\s*([""'])((?:(?!\3).)*)\3", Opts);
    private static readonly Regex TrailPunctRe = new(@"[.,;:)\]]+$", Opts);
    private static readonly Regex QuotedRe = new(@"^([""']).*\1$", Opts | RegexOptions.Singleline);
    private static readonly Regex WhitespaceRe = new(@"\s+", Opts);

    private static string Unquote(string v) => QuotedRe.IsMatch(v) ? v[1..^1] : v;

    /// <summary>Redacts secrets in an arbitrary string (command lines, error messages, CLI output).</summary>
    public static string RedactText(string text, Action<string>? sink = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var o = text;
        o = PemRe.Replace(o, m =>
        {
            sink?.Invoke(m.Value);
            return Mask;
        });
        o = UrlInTextRe.Replace(o, m =>
        {
            var u = m.Value;
            var trail = TrailPunctRe.Match(u).Value;
            return RedactUrl(trail.Length > 0 ? u[..^trail.Length] : u, sink) + trail;
        });
        o = JwtRe.Replace(o, m =>
        {
            sink?.Invoke(m.Value);
            return Mask;
        });
        foreach (var re in PrefixRes)
        {
            o = re.Replace(o, m =>
            {
                sink?.Invoke(m.Value);
                return Mask;
            });
        }

        o = BearerRe.Replace(o, m =>
        {
            var word = WhitespaceRe.Split(m.Value)[0];
            sink?.Invoke(m.Value[word.Length..].Trim());
            return $"{word} {Mask}";
        });
        o = UserFlagRe.Replace(o, m =>
        {
            var pw = m.Groups[5].Value;
            if (Unquote(pw) == Mask)
            {
                return m.Value;
            }

            sink?.Invoke(Unquote(pw));
            return $"{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value}{m.Groups[4].Value}:{Mask}";
        });
        o = HeaderRe.Replace(o, m =>
        {
            var name = m.Groups[2].Value;
            var val = m.Groups[4].Value;
            if (!IsSecretKey(name) || Unquote(val) == Mask)
            {
                return m.Value;
            }

            sink?.Invoke(Unquote(val));
            return $"{m.Groups[1].Value}{name}{m.Groups[3].Value}{Mask}";
        });
        o = FlagRe.Replace(o, m =>
        {
            var flag = m.Groups[1].Value;
            var val = m.Groups[3].Value;
            if (!IsSecretKey(flag.TrimStart('-')) || Unquote(val) == Mask)
            {
                return m.Value;
            }

            sink?.Invoke(Unquote(val));
            return $"{flag}{m.Groups[2].Value}{Mask}";
        });
        o = AssignRe.Replace(o, m =>
        {
            var name = m.Groups[2].Value;
            var val = m.Groups[3].Value;
            if (!IsSecretKey(name) || Unquote(val) == Mask)
            {
                return m.Value;
            }

            sink?.Invoke(Unquote(val));
            return $"{m.Groups[1].Value}{name}={Mask}";
        });
        o = JsonPairRe.Replace(o, m =>
        {
            var name = m.Groups[2].Value;
            var val = m.Groups[4].Value;
            if (!IsSecretKey(name) || val == Mask || val.Length == 0)
            {
                return m.Value;
            }

            sink?.Invoke(val);
            var q1 = m.Groups[1].Value;
            var q2 = m.Groups[3].Value;
            return $"{q1}{name}{q1}: {q2}{Mask}{q2}";
        });
        return o;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Structured data
    // ---------------------------------------------------------------------------------------------------------

    private static readonly Regex EnvEntryRe = new(@"^([A-Za-z_][\w.-]*)=([\s\S]*)$", Opts);
    private static readonly Regex InlineFlagRe = new(@"^(--?[A-Za-z][\w.-]*)=([\s\S]*)$", Opts);
    private static readonly Regex BareFlagRe = new(@"^--?[A-Za-z][\w.-]*$", Opts);
    private static readonly Regex UserShortRe = new(@"^(-u|--user|--proxy-user)$", Opts);
    private static readonly Regex NumericSafeKeyRe = new(@"(port|count|size|length|limit|max|min|timeout|interval|retries|num|age)$", Opts | RegexOptions.IgnoreCase);

    /// <summary>Redacts one <c>NAME=value</c> environment entry.</summary>
    public static string RedactEnvEntry(string entry, RedactOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        options ??= new RedactOptions();
        var m = EnvEntryRe.Match(entry);
        if (!m.Success)
        {
            return RedactText(entry, options.Sink);
        }

        var name = m.Groups[1].Value;
        var value = m.Groups[2].Value;
        if (value.Length == 0 || value == Mask)
        {
            return entry;
        }

        if (options.RedactAllEnv || IsSecretKey(name) || LooksLikeSecretValue(value))
        {
            options.Sink?.Invoke(value);
            return $"{name}={Mask}";
        }

        return $"{name}={RedactText(value, options.Sink)}";
    }

    /// <summary>Redacts an argv/command array: secret-named flags mask their value (inline or next token).</summary>
    public static IReadOnlyList<string> RedactArgv(IReadOnlyList<string> argv, RedactOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(argv);
        options ??= new RedactOptions();
        var sink = options.Sink;
        var output = new List<string>(argv.Count);
        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            var eq = InlineFlagRe.Match(a);
            if (eq.Success)
            {
                var flag = eq.Groups[1].Value;
                var val = eq.Groups[2].Value;
                if (IsSecretKey(flag.TrimStart('-')) && val != Mask && val.Length > 0)
                {
                    sink?.Invoke(val);
                    output.Add($"{flag}={Mask}");
                }
                else
                {
                    output.Add(RedactText(a, sink));
                }

                continue;
            }

            if (UserShortRe.IsMatch(a))
            {
                output.Add(a);
                if (i + 1 < argv.Count && !argv[i + 1].StartsWith('-'))
                {
                    var next = argv[i + 1];
                    var parts = next.Split(':');
                    if (parts.Length > 1)
                    {
                        sink?.Invoke(string.Join(':', parts.Skip(1)));
                        output.Add($"{parts[0]}:{Mask}");
                    }
                    else
                    {
                        output.Add(next);
                    }

                    i++;
                }

                continue;
            }

            if (BareFlagRe.IsMatch(a) && IsSecretKey(a.TrimStart('-')))
            {
                output.Add(a);
                if (i + 1 < argv.Count && !argv[i + 1].StartsWith('-'))
                {
                    sink?.Invoke(argv[i + 1]);
                    output.Add(Mask);
                    i++;
                }

                continue;
            }

            if (EnvEntryRe.IsMatch(a))
            {
                output.Add(RedactEnvEntry(a, options));
                continue;
            }

            if (LooksLikeSecretValue(a))
            {
                sink?.Invoke(a);
                output.Add(Mask);
                continue;
            }

            output.Add(RedactText(a, sink));
        }

        return output;
    }

    /// <summary>
    /// Walks any JSON value at every depth and returns a redacted deep copy. Strings under secret-named keys are
    /// masked; children of secret-named containers are masked wholesale; strings elsewhere get the text rules.
    /// </summary>
    public static JsonNode? RedactDeep(JsonNode? value, RedactOptions? options = null) =>
        Walk(value, null, false, options ?? new RedactOptions(), 0, []);

    private static JsonNode? Walk(JsonNode? v, string? key, bool forceMask, RedactOptions opts, int depth, IReadOnlyList<string> ancestors)
    {
        if (depth > MaxDepth)
        {
            return JsonValue.Create(Mask);
        }

        var lk = key?.ToLowerInvariant();
        var secretKey = key is not null && IsSecretKey(key);

        switch (v)
        {
            case null:
                return null;
            case JsonValue jv:
                return WalkValue(jv, key, lk, secretKey, forceMask, opts, ancestors);
            case JsonArray arr:
                return WalkArray(arr, lk, secretKey, forceMask, opts, depth, ancestors);
            case JsonObject obj:
                return WalkObject(obj, lk, secretKey, forceMask, opts, depth, ancestors);
            default:
                return JsonValue.Create(Mask);
        }
    }

    private static JsonNode? WalkValue(JsonValue jv, string? key, string? lk, bool secretKey, bool forceMask, RedactOptions opts, IReadOnlyList<string> ancestors)
    {
        if (jv.TryGetValue<string>(out var s))
        {
            if (s.Length == 0 || s == Mask)
            {
                return JsonValue.Create(s);
            }

            var inlineContent = lk == "content" && ancestors.Any(a => a is "secrets" or "configs");
            if (forceMask || secretKey || inlineContent)
            {
                opts.Sink?.Invoke(s);
                return JsonValue.Create(Mask);
            }

            if (lk is not null && AllEnvKeys.Contains(lk) && opts.RedactAllEnv)
            {
                opts.Sink?.Invoke(s);
                return JsonValue.Create(Mask);
            }

            return JsonValue.Create(RedactText(s, opts.Sink));
        }

        var kind = jv.GetValueKind();
        if (kind is System.Text.Json.JsonValueKind.Number)
        {
            if (forceMask || (secretKey && !NumericSafeKeyRe.IsMatch(key ?? string.Empty)))
            {
                opts.Sink?.Invoke(jv.ToJsonString());
                return JsonValue.Create(Mask);
            }

            return jv.DeepClone();
        }

        // booleans, null
        return jv.DeepClone();
    }

    private static JsonArray WalkArray(JsonArray arr, string? lk, bool secretKey, bool forceMask, RedactOptions opts, int depth, IReadOnlyList<string> ancestors)
    {
        var isDefinitionMap = lk is "secrets" or "configs";
        var childForce = forceMask || (secretKey && !isDefinitionMap);
        var childAncestors = lk is null ? ancestors : [.. ancestors, lk];
        var envish = lk is not null && EnvKeys.Contains(lk);

        if (!childForce && arr.All(x => x is JsonValue xv && xv.TryGetValue<string>(out _)))
        {
            var strs = arr.Select(x => x!.GetValue<string>()).ToList();
            IReadOnlyList<string> redacted;
            if (envish)
            {
                var o = AllEnvKeys.Contains(lk!) ? opts : opts with { RedactAllEnv = false };
                redacted = strs.Select(s => RedactEnvEntry(s, o)).ToList();
            }
            else
            {
                redacted = RedactArgv(strs, opts);
            }

            var result = new JsonArray();
            foreach (var r in redacted)
            {
                result.Add(JsonValue.Create(r));
            }

            return result;
        }

        var outArr = new JsonArray();
        foreach (var x in arr)
        {
            outArr.Add(Walk(x, null, childForce, opts, depth + 1, childAncestors));
        }

        return outArr;
    }

    private static JsonObject WalkObject(JsonObject obj, string? lk, bool secretKey, bool forceMask, RedactOptions opts, int depth, IReadOnlyList<string> ancestors)
    {
        var isDefinitionMap = lk is "secrets" or "configs";
        var childForce = forceMask || (secretKey && !isDefinitionMap);
        var childAncestors = lk is null ? ancestors : [.. ancestors, lk];
        var envish = lk is not null && EnvKeys.Contains(lk);
        var result = new JsonObject();
        foreach (var (k, val) in obj)
        {
            var str = val is JsonValue sv && sv.TryGetValue<string>(out var sx) ? sx : null;
            if (envish && str is not null && opts.RedactAllEnv && AllEnvKeys.Contains(lk!) && str.Length > 0)
            {
                opts.Sink?.Invoke(str);
                result[k] = JsonValue.Create(Mask);
            }
            else if (envish && str is not null && !childForce && !IsSecretKey(k) && !SafeKeyRe.IsMatch(k) && LooksLikeSecretValue(str))
            {
                opts.Sink?.Invoke(str);
                result[k] = JsonValue.Create(Mask);
            }
            else if (isDefinitionMap && val is JsonObject)
            {
                // entries of top-level `secrets:` / `configs:` are named after the secret; the name is not one
                result[k] = Walk(val, null, false, opts, depth + 1, childAncestors);
            }
            else
            {
                result[k] = Walk(val, k, childForce, opts, depth + 1, childAncestors);
            }
        }

        return result;
    }

    /// <summary>Redacts the values of a labels map (entropy rule applies: labels are free-form).</summary>
    public static Dictionary<string, string> RedactLabels(IReadOnlyDictionary<string, string> labels, RedactOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var sink = options?.Sink;
        var output = new Dictionary<string, string>(labels.Count, StringComparer.Ordinal);
        foreach (var (k, v) in labels)
        {
            if (v.Length == 0 || v == Mask)
            {
                output[k] = v;
            }
            else if (SafeKeyRe.IsMatch(k))
            {
                output[k] = v; // Docker/Compose bookkeeping (config hash, image ID): not secrets
            }
            else if (IsSecretKey(k) || LooksLikeSecretValue(v))
            {
                sink?.Invoke(v);
                output[k] = Mask;
            }
            else
            {
                output[k] = RedactText(v, sink);
            }
        }

        return output;
    }

    /// <summary>Collects every secret value the redaction rules find in <paramref name="value"/>.</summary>
    public static IReadOnlyList<string> CollectSecretValues(JsonNode? value, bool redactAllEnv = false)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        _ = RedactDeep(value, new RedactOptions(redactAllEnv, s => found.Add(s)));
        return [.. found];
    }

    internal static string FormatNumber(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
