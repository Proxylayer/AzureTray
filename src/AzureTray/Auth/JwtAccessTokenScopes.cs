using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace AzureTray.Auth;

// Reads the delegated-permission claim ("scp") out of an access token so the
// host can answer one question it otherwise has to guess at: does the token
// we just acquired actually carry the scope that was consented a moment ago?
//
// Access tokens are opaque by contract — Entra may change their format, and
// a resource can opt into an encrypted one — so every failure path returns
// null and callers must read that as "cannot tell", never as "the scope is
// missing". Nothing here is used for authorization; it only decides whether
// to keep waiting for consent to propagate.
internal static class JwtAccessTokenScopes
{
    // Returns the scope names in the token's "scp" claim, or null when the
    // token is not a readable JWT / carries no such claim.
    public static IReadOnlyList<string>? TryRead(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        var parts = accessToken.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            using var document = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            if (!document.RootElement.TryGetProperty("scp", out var scp)) return null;

            // v1 (ARM) and v2 (Graph) tokens both emit a space-delimited
            // string; the array form shows up on some first-party resources.
            switch (scp.ValueKind)
            {
                case JsonValueKind.String:
                    return scp.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                case JsonValueKind.Array:
                    var values = new List<string>(scp.GetArrayLength());
                    foreach (var item in scp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                        {
                            values.Add(value);
                        }
                    }
                    return values;
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = (segment.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("Not a valid base64url segment."),
        };
        return Convert.FromBase64String(padded);
    }
}
