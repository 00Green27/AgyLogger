using System.Text;

namespace AgyLogger.Cli.Services.Proxy;

/// <summary>
/// Provides high-performance, non-allocating or low-allocating redaction of sensitive
/// authentication headers and URL query parameters to prevent credential leakage into logs.
/// </summary>
public static class SensitiveDataRedactor
{
    /// <summary>
    /// The replacement token used for redacted values.
    /// </summary>
    public const string RedactedValue = "[REDACTED]";

    /// <summary>
    /// Case-insensitive set of HTTP headers containing credentials, secrets, or session data.
    /// </summary>
    public static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "x-api-key",
        "api-key",
        "x-goog-api-key",
        "cookie",
        "set-cookie",
        "proxy-authorization"
    };

    /// <summary>
    /// Case-insensitive set of URL query parameters containing keys, tokens, or credentials.
    /// </summary>
    public static readonly HashSet<string> SensitiveQueryParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "key",
        "api_key",
        "apikey",
        "api-key",
        "x-goog-api-key",
        "token",
        "access_token",
        "id_token",
        "auth",
        "authorization",
        "signature",
        "sig",
        "secret",
        "client_secret",
        "password",
        "session",
        "bearer"
    };

    /// <summary>
    /// Checks whether the specified HTTP header is considered sensitive.
    /// </summary>
    public static bool IsSensitiveHeader(string? headerName)
    {
        return !string.IsNullOrWhiteSpace(headerName) && SensitiveHeaders.Contains(headerName.Trim());
    }

    /// <summary>
    /// Checks whether the specified URL query parameter name is considered sensitive.
    /// </summary>
    public static bool IsSensitiveQueryParam(string? paramName)
    {
        if (string.IsNullOrWhiteSpace(paramName))
        {
            return false;
        }

        var trimmed = paramName.Trim();
        if (SensitiveQueryParams.Contains(trimmed))
        {
            return true;
        }

        // Handle URL-encoded parameter names (e.g., api%5Fkey -> api_key)
        if (trimmed.Contains('%'))
        {
            try
            {
                var unescaped = Uri.UnescapeDataString(trimmed).Trim();
                if (!string.Equals(unescaped, trimmed, StringComparison.Ordinal) &&
                    SensitiveQueryParams.Contains(unescaped))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore malformed unescaping attempts and treat as non-match
            }
        }

        return false;
    }

    /// <summary>
    /// Redacts sensitive headers from a multi-value header collection (e.g. HttpHeaders).
    /// Preserves original casing of header names, joins non-sensitive multi-value headers with ", ",
    /// and replaces sensitive values with "[REDACTED]".
    /// </summary>
    public static Dictionary<string, string> RedactHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result;
        }

        foreach (var (key, values) in headers)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var trimmedKey = key.Trim();
            var isSensitive = IsSensitiveHeader(trimmedKey);
            string formattedValue;

            if (isSensitive)
            {
                formattedValue = RedactedValue;
            }
            else if (values is null)
            {
                formattedValue = string.Empty;
            }
            else
            {
                formattedValue = string.Join(", ", values);
            }

            if (result.TryGetValue(trimmedKey, out var existing))
            {
                if (isSensitive)
                {
                    // Keep single [REDACTED]
                    result[trimmedKey] = RedactedValue;
                }
                else
                {
                    result[trimmedKey] = string.IsNullOrEmpty(existing)
                        ? formattedValue
                        : $"{existing}, {formattedValue}";
                }
            }
            else
            {
                result[trimmedKey] = formattedValue;
            }
        }

        return result;
    }

    /// <summary>
    /// Overload for key-value collections of single-string headers.
    /// </summary>
    public static Dictionary<string, string> RedactHeaders(
        IEnumerable<KeyValuePair<string, string>>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result;
        }

        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var trimmedKey = key.Trim();
            var isSensitive = IsSensitiveHeader(trimmedKey);
            var formattedValue = isSensitive ? RedactedValue : (value ?? string.Empty);

            if (result.TryGetValue(trimmedKey, out var existing))
            {
                if (isSensitive)
                {
                    result[trimmedKey] = RedactedValue;
                }
                else
                {
                    result[trimmedKey] = string.IsNullOrEmpty(existing)
                        ? formattedValue
                        : $"{existing}, {formattedValue}";
                }
            }
            else
            {
                result[trimmedKey] = formattedValue;
            }
        }

        return result;
    }

    /// <summary>
    /// Redacts sensitive query parameters (e.g. key, api_key, token) from absolute or relative URLs.
    /// Preserves URL paths, colons, parameter order, non-sensitive parameters, and fragments.
    /// </summary>
    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url ?? string.Empty;
        }

        var questionMarkIndex = url.IndexOf('?');
        if (questionMarkIndex < 0)
        {
            return url;
        }

        var pathAndPrefix = url[..(questionMarkIndex + 1)]; // includes '?'
        var rest = url[(questionMarkIndex + 1)..];

        string query;
        string fragment = string.Empty;

        var hashIndex = rest.IndexOf('#');
        if (hashIndex >= 0)
        {
            query = rest[..hashIndex];
            fragment = rest[hashIndex..];
        }
        else
        {
            query = rest;
        }

        if (string.IsNullOrEmpty(query))
        {
            return url;
        }

        var pairs = query.Split('&');
        var redactedPairs = new string[pairs.Length];

        for (var i = 0; i < pairs.Length; i++)
        {
            var pair = pairs[i];
            if (string.IsNullOrEmpty(pair))
            {
                redactedPairs[i] = pair;
                continue;
            }

            var eqIndex = pair.IndexOf('=');
            string name;

            if (eqIndex >= 0)
            {
                name = pair[..eqIndex];
            }
            else
            {
                name = pair;
            }

            if (IsSensitiveQueryParam(name))
            {
                redactedPairs[i] = $"{name}={RedactedValue}";
            }
            else
            {
                redactedPairs[i] = pair;
            }
        }

        return $"{pathAndPrefix}{string.Join('&', redactedPairs)}{fragment}";
    }

    /// <summary>
    /// Formats a collection of redacted headers into the standard fenced Markdown block
    /// required by ai-coding-crash-course/request-logger.
    /// </summary>
    public static string RenderHeaders(IEnumerable<KeyValuePair<string, string>>? redactedHeaders)
    {
        var sb = new StringBuilder();
        sb.Append("<headers>\n\n```\n");

        if (redactedHeaders is not null)
        {
            var sorted = redactedHeaders
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                .OrderBy(kv => kv.Key.Trim(), StringComparer.OrdinalIgnoreCase);

            foreach (var (key, value) in sorted)
            {
                var safeKey = key.Trim().ToLowerInvariant().Replace("```", "'''");
                var safeValue = (value ?? string.Empty).Replace("```", "'''");
                sb.Append($"{safeKey}: {safeValue}\n");
            }
        }

        sb.Append("```\n\n</headers>");
        return sb.ToString();
    }

    /// <summary>
    /// Helper to directly render raw headers into the standard fenced Markdown block.
    /// </summary>
    public static string RenderHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        var redacted = RedactHeaders(headers);
        return RenderHeaders(redacted);
    }
}