using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PolicyPilot.Core.Security;

public sealed record AppUser(string Username, string Role, string PasswordHash);

/// <summary>PBKDF2-SHA256 password hashing (210k iterations, per-user salt).</summary>
public static class PasswordHasher
{
    private const int Iter = 210_000;
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iter, HashAlgorithmName.SHA256, 32);
        return $"{Iter}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(h)}";
    }
    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('.');
        if (p.Length != 3) return false;
        var h = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[1]), int.Parse(p[0]), HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(p[2]));
    }
}

/// <summary>Minimal HS256 JWT. In production use Microsoft.AspNetCore.Authentication.JwtBearer + Entra ID; this shows what that does under the hood.</summary>
public sealed class JwtService(string secret, TimeSpan? lifetime = null)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(secret.Length >= 32 ? secret : throw new ArgumentException("JWT secret must be >= 32 chars"));
    private readonly TimeSpan _life = lifetime ?? TimeSpan.FromHours(1);

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] UnB64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }

    public string Issue(AppUser u, DateTimeOffset? now = null)
    {
        var n = now ?? DateTimeOffset.UtcNow;
        var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["sub"] = u.Username, ["role"] = u.Role, ["iat"] = n.ToUnixTimeSeconds(), ["exp"] = n.Add(_life).ToUnixTimeSeconds() }));
        return $"{header}.{payload}.{B64(Sign($"{header}.{payload}"))}";
    }

    public UserContext? Validate(string? token, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var p = token.Split('.');
        if (p.Length != 3) return null;
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(Sign($"{p[0]}.{p[1]}"), UnB64(p[2]))) return null;
            var hdr = JsonDocument.Parse(UnB64(p[0])).RootElement;
            if (hdr.GetProperty("alg").GetString() != "HS256") return null;   // blocks alg=none attacks
            var body = JsonDocument.Parse(UnB64(p[1])).RootElement;
            if ((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() >= body.GetProperty("exp").GetInt64()) return null;
            return new UserContext(body.GetProperty("sub").GetString()!, body.GetProperty("role").GetString()!);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private byte[] Sign(string data) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(data));
}

public sealed class UserStore
{
    private readonly Dictionary<string, AppUser> _users = new(StringComparer.OrdinalIgnoreCase);
    public void Add(string username, string password, string role) => _users[username] = new AppUser(username, role, PasswordHasher.Hash(password));
    public AppUser? Authenticate(string username, string password) =>
        _users.TryGetValue(username, out var u) && PasswordHasher.Verify(password, u.PasswordHash) ? u : null;
}

/// <summary>Fixed-window per-key limiter (sufficient for a demo; use a sliding window / Redis for multi-instance).</summary>
public sealed class RateLimiter(int limit, TimeSpan window)
{
    private readonly Dictionary<string, (DateTimeOffset start, int count)> _hits = new();
    public bool Allow(string key, DateTimeOffset? now = null)
    {
        var n = now ?? DateTimeOffset.UtcNow;
        lock (_hits)
        {
            if (!_hits.TryGetValue(key, out var h) || n - h.start >= window) h = (n, 0);
            if (h.count >= limit) { _hits[key] = h; return false; }
            _hits[key] = (h.start, h.count + 1);
            return true;
        }
    }
}
