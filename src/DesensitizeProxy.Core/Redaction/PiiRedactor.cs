using System.Text;
using System.Text.RegularExpressions;
using DesensitizeProxy.Core.Abstractions;
using DesensitizeProxy.Core.Engine;
using DesensitizeProxy.Core.Models;
using Microsoft.Extensions.Options;

namespace DesensitizeProxy.Core.Redaction;

public sealed class PiiRedactor : IPiiRedactor
{
    private static readonly (string Keyword, string Label, bool Strict)[] ChineseRules =
    [
        ("密码", "PASSWORD", false),
        ("私钥", "PRIVATE_KEY", true),
        ("取件码", "DELIVERY", false),
        ("验证码", "VERIFICATION", false),
        ("银行卡号", "CARD", true),
        ("身份证号", "ID", true)
    ];

    private readonly IOptionsMonitor<PrivacyConfig> _options;
    private readonly DynamicRegexes _dynamicRegexes;

    public PiiRedactor(IOptionsMonitor<PrivacyConfig> options, DynamicRegexes? dynamicRegexes = null)
    {
        _options = options;
        _dynamicRegexes = dynamicRegexes ?? new DynamicRegexes();
    }

    public string Redact(string content)
    {
        return RedactWithHits(content).Content;
    }

    public RedactionResult RedactWithHits(string content)
    {
        var phases = new HashSet<string>(StringComparer.Ordinal);
        var hits = new List<RedactionHit>();
        var redacted = ApplyPhase(content, RedactPhase1WithHits, "phase1", phases, hits);
        redacted = ApplyPhase(redacted, RedactPhase2WithHits, "phase2", phases, hits);
        redacted = ApplyPhase(redacted, RedactPhase3WithHits, "phase3", phases, hits);
        return new RedactionResult(redacted, phases, hits);
    }

    public string RedactPhase1Only(string content)
    {
        return RedactPhase1WithHits(content, "phase1", hits: null);
    }

    public string RedactSystem(string content, SystemMessageRedactionConfig config)
    {
        return RedactSystemWithHits(content, config).Content;
    }

    public RedactionResult RedactSystemWithHits(string content, SystemMessageRedactionConfig config)
    {
        var redacted = content;
        var phases = new HashSet<string>(StringComparer.Ordinal);
        var hits = new List<RedactionHit>();
        if (config.Phase1)
        {
            redacted = ApplyPhase(redacted, RedactPhase1WithHits, "phase1", phases, hits);
        }

        if (config.Phase2)
        {
            redacted = ApplyPhase(redacted, RedactPhase2WithHits, "phase2", phases, hits);
        }

        if (config.Phase3)
        {
            redacted = ApplyPhase(redacted, RedactPhase3WithHits, "phase3", phases, hits);
        }

        return new RedactionResult(redacted, phases, hits);
    }

    public bool HasAnyHit(string content) => Redact(content) != content;

    private static string ApplyPhase(
        string content,
        Func<string, string, ICollection<RedactionHit>?, string> redactor,
        string phase,
        ISet<string> phases,
        ICollection<RedactionHit> hits)
    {
        var redacted = redactor(content, phase, hits);
        if (redacted != content)
        {
            phases.Add(phase);
        }

        return redacted;
    }

    private static string RedactPhase1WithHits(string content, string phase, ICollection<RedactionHit>? hits)
    {
        var redacted = ReplaceWithHit(content, GeneratedRegexes.PrivateKey(), "PRIVATE_KEY", "[REDACTED:PRIVATE_KEY]", phase, hits);
        redacted = ReplaceWithHit(redacted, GeneratedRegexes.AwsKey(), "AWS_KEY", "[REDACTED:AWS_KEY]", phase, hits);
        redacted = ReplaceWithHit(redacted, GeneratedRegexes.DbConnection(), "DB_CONNECTION", "[REDACTED:DB_CONNECTION]", phase, hits);
        redacted = ReplaceWithHit(redacted, GeneratedRegexes.DeliveryCode(), "DELIVERY", "[REDACTED:DELIVERY]", phase, hits);
        redacted = ReplaceWithHit(redacted, GeneratedRegexes.AccessCode(), "ACCESS_CODE", "[REDACTED:ACCESS_CODE]", phase, hits);
        return redacted;
    }

    private string RedactPhase2WithHits(string content, string phase, ICollection<RedactionHit>? hits)
    {
        var config = _options.CurrentValue.Redaction;
        var redacted = content;
        if (config.ChinesePhone)
        {
            redacted = ReplaceWithHit(redacted, GeneratedRegexes.ChinesePhone(), "PHONE", "[REDACTED:PHONE]", phase, hits);
        }

        if (config.ChineseId)
        {
            redacted = ReplaceWithHit(redacted, GeneratedRegexes.ChineseId(), "ID", "[REDACTED:ID]", phase, hits);
        }

        if (config.Email)
        {
            redacted = ReplaceWithHit(redacted, GeneratedRegexes.Email(), "EMAIL", "[REDACTED:EMAIL]", phase, hits);
        }

        if (config.InternalIp)
        {
            redacted = ReplaceWithHit(redacted, GeneratedRegexes.InternalIp(), "IP", "[REDACTED:IP]", phase, hits);
        }

        if (config.CreditCard)
        {
            redacted = ReplaceWithHit(redacted, GeneratedRegexes.CreditCardCandidate(), "CARD", "[REDACTED:CARD]", phase, hits);
        }

        if (config.EnvVar)
        {
            redacted = ReplaceWithHit(redacted, GeneratedRegexes.EnvVarSecret(), "SECRET", "[REDACTED:SECRET]", phase, hits);
        }

        if (config.ChineseAddress)
        {
            redacted = ReplaceWithHit(redacted, GeneratedRegexes.ChineseAddress(), "ADDRESS", "[REDACTED:ADDRESS]", phase, hits);
        }

        return redacted;
    }

    private string RedactPhase3WithHits(string content, string phase, ICollection<RedactionHit>? hits)
    {
        var segments = SplitByCodeFence(content);
        var builder = new StringBuilder(content.Length);
        foreach (var segment in segments)
        {
            builder.Append(segment.IsCode ? segment.Text : RedactContextKeywords(segment.Text, phase, hits));
        }

        return builder.ToString();
    }

    private string RedactContextKeywords(string content, string phase, ICollection<RedactionHit>? hits)
    {
        var redacted = content;
        redacted = ReplaceContextValue(redacted, _dynamicRegexes.Get(@"(?i)(credit\s*card|card\s*(?:number|no\.?))\s+(?:is|are|was|were)(?:\s+(?:in|at|on|of|for))*\s*[""']?([^\s""']{4,})[""']?"), "CARD", phase, hits);
        redacted = ReplaceContextValue(redacted, _dynamicRegexes.Get(@"(?i)(ssn|social\s*security(?:\s*(?:number|no\.?))?)\s+(?:is|are|was|were)\s*[""']?([^\s""']{4,})[""']?"), "SSN", phase, hits);
        redacted = ReplaceContextValue(redacted, _dynamicRegexes.Get(@"(?i)\b(password|passwd|pwd|passcode)\b(?:\s*[:=]\s*|\s+is\s+)[""']?([^\s""']{4,})[""']?"), "PASSWORD", phase, hits);
        redacted = ReplaceContextValue(redacted, _dynamicRegexes.Get(@"(?i)\b(api[_\s]?key|access[_\s]?key|secret[_\s]?key)\b\s*[:=]\s*[""']?([^\s""']{6,})[""']?"), "API_KEY", phase, hits);
        redacted = ReplaceContextValue(redacted, _dynamicRegexes.Get(@"(?i)\b((?:auth[_\s]?)?token)\b\s*[:=]\s*[""']?([^\s""']{6,})[""']?"), "TOKEN", phase, hits);
        redacted = ReplaceContextValue(redacted, _dynamicRegexes.Get(@"(?i)\b(bearer)\b\s+[""']?([^\s""']{8,})[""']?"), "TOKEN", phase, hits);

        foreach (var rule in ChineseRules)
        {
            redacted = RedactChineseKeyword(redacted, rule.Keyword, rule.Label, rule.Strict, phase, hits);
        }

        return redacted;
    }

    private static string ReplaceWithHit(
        string content,
        Regex regex,
        string label,
        string replacement,
        string phase,
        ICollection<RedactionHit>? hits)
    {
        return regex.Replace(content, match =>
        {
            hits?.Add(new RedactionHit(phase, label, match.Value, replacement, Count: 1));
            return replacement;
        });
    }

    private static string ReplaceContextValue(
        string content,
        Regex regex,
        string label,
        string phase,
        ICollection<RedactionHit>? hits)
    {
        return regex.Replace(content, match =>
        {
            var (originalValue, trailingDelimiter) = SplitTrailingContextDelimiter(match.Groups[2].Value);
            if (!IsPlausibleContextValue(originalValue, label))
            {
                return match.Value;
            }

            var redactedValue = $"[REDACTED:{label}]";
            hits?.Add(new RedactionHit(phase, label, originalValue, redactedValue, Count: 1));
            return $"{match.Groups[1].Value} {redactedValue}{trailingDelimiter}";
        });
    }

    private static (string Value, string TrailingDelimiter) SplitTrailingContextDelimiter(string value)
    {
        var end = value.Length;
        while (end > 0 && IsTrailingContextDelimiter(value[end - 1]))
        {
            end--;
        }

        return (value[..end], value[end..]);
    }

    private static bool IsTrailingContextDelimiter(char c) => c is ',' or ';' or ')' or ']' or '}';

    private static bool IsPlausibleContextValue(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("[REDACTED", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.Contains("\\n", StringComparison.Ordinal) || value.EndsWith('\\'))
        {
            return false;
        }

        if (!value.Any(char.IsLetterOrDigit))
        {
            return false;
        }

        if (label is "TOKEN" or "API_KEY" or "PASSWORD" && ContainsContextNoise(value))
        {
            return false;
        }

        if (label is "TOKEN" or "API_KEY" && !value.All(IsTokenLikeCharacter))
        {
            return false;
        }

        return label switch
        {
            "TOKEN" => value.Length >= 6,
            "API_KEY" => value.Length >= 6,
            "PASSWORD" => value.Length >= 4,
            _ => value.Length >= 4
        };
    }

    private static bool ContainsContextNoise(string value) => value.Any(c =>
        c is '`' or ':' or '、' or '，' or '。' or '；' or '：' or '“' or '”' or '‘' or '’' ||
        c >= '\u4e00' && c <= '\u9fff');

    private static bool IsTokenLikeCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or '+' or '=';

    private static string RedactChineseKeyword(
        string content,
        string keyword,
        string label,
        bool strict,
        string phase,
        ICollection<RedactionHit>? hits)
    {
        var index = 0;
        var result = new StringBuilder(content.Length);
        while (index < content.Length)
        {
            var hit = content.IndexOf(keyword, index, StringComparison.Ordinal);
            if (hit < 0)
            {
                result.Append(content.AsSpan(index));
                break;
            }

            result.Append(content.AsSpan(index, hit - index));
            result.Append(keyword);

            var valueStart = hit + keyword.Length;
            var connectorLength = FindChineseConnector(content, valueStart, strict);
            if (connectorLength < 0)
            {
                index = valueStart;
                continue;
            }

            result.Append(content.AsSpan(valueStart, connectorLength));
            valueStart += connectorLength;
            while (valueStart < content.Length && char.IsWhiteSpace(content[valueStart]))
            {
                result.Append(content[valueStart]);
                valueStart++;
            }

            var valueEnd = valueStart;
            while (valueEnd < content.Length && !char.IsWhiteSpace(content[valueEnd]) && !IsChineseDelimiter(content[valueEnd]))
            {
                valueEnd++;
            }

            if (valueEnd > valueStart)
            {
                var redactedValue = $"[REDACTED:{label}]";
                var originalValue = content[valueStart..valueEnd];
                if (IsPlausibleContextValue(originalValue, label))
                {
                    hits?.Add(new RedactionHit(phase, label, originalValue, redactedValue, Count: 1));
                    result.Append(redactedValue);
                }
                else
                {
                    result.Append(originalValue);
                }

                index = valueEnd;
            }
            else
            {
                index = valueStart;
            }
        }

        return result.ToString();
    }

    private static int FindChineseConnector(string content, int start, bool strict)
    {
        var i = start;
        while (i < content.Length && char.IsWhiteSpace(content[i]))
        {
            i++;
        }

        if (i >= content.Length)
        {
            return -1;
        }

        if (content[i] is '：' or ':' or '=' or '是' or '为')
        {
            return i - start + 1;
        }

        return strict || i == start ? -1 : i - start;
    }

    private static bool IsChineseDelimiter(char c) => c is '，' or ',' or '。' or ';' or '；' or '、' or ')' or '）' or '(' or '（' or '"' or '\'' or '“' or '”' or '‘' or '’';

    private static IEnumerable<(string Text, bool IsCode)> SplitByCodeFence(string content)
    {
        var index = 0;
        var isCode = false;
        while (index < content.Length)
        {
            var fence = content.IndexOf("```", index, StringComparison.Ordinal);
            if (fence < 0)
            {
                yield return (content[index..], isCode);
                break;
            }

            if (fence > index)
            {
                yield return (content[index..fence], isCode);
            }

            var end = fence + 3;
            yield return (content[fence..end], isCode);
            index = end;
            isCode = !isCode;
        }
    }
}
