using System.Security.Cryptography;
using System.Text;

namespace SharePointToAzureSearch.Core;

/// <summary>
/// Carries a human-readable subscription name in Graph's clientState while authenticating that name
/// with the configured secret. The secret itself never leaves configuration. Existing subscriptions
/// whose clientState is the legacy raw secret remain valid.
/// </summary>
public static class SubscriptionClientState
{
    private const string Prefix = "spas1";
    private const int MaximumLength = 128;

    public static bool TryCreate(string name, string secret, out string clientState)
    {
        var normalizedName = name.Trim();
        var encodedName = Base64UrlEncode(Encoding.UTF8.GetBytes(normalizedName));
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{Prefix}.{encodedName}"));
        clientState = $"{Prefix}.{encodedName}.{Base64UrlEncode(signature)}";
        return normalizedName.Length > 0 && clientState.Length <= MaximumLength;
    }

    public static string Create(string name, string secret) =>
        TryCreate(name, secret, out var clientState)
            ? clientState
            : throw new ArgumentException("The subscription name is too long for Microsoft Graph clientState.", nameof(name));

    public static bool IsValid(string? clientState, string secret) =>
        SecureEquals(clientState, secret) || TryGetName(clientState, secret, out _);

    public static bool TryGetName(string? clientState, string secret, out string name)
    {
        name = "";
        if (string.IsNullOrWhiteSpace(clientState))
        {
            return false;
        }

        var parts = clientState.Split('.');
        if (parts is not [Prefix, var encodedName, var encodedSignature])
        {
            return false;
        }

        try
        {
            var expected = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes($"{Prefix}.{encodedName}"));
            var actual = Base64UrlDecode(encodedSignature);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                return false;
            }

            name = new UTF8Encoding(false, true).GetString(Base64UrlDecode(encodedName));
            return !string.IsNullOrWhiteSpace(name);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool SecureEquals(string? left, string right)
    {
        if (left is null)
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}
