using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Server.Auth;

// "Device login" for the editor, the same idea as `gh auth login` or signing
// in to a TV app:
//
//   1. Editor → POST /api/device/start. Gets a secret DeviceCode (kept by the
//      editor) and a short UserCode like "KQ7M-X2PD" plus a URL.
//   2. The editor opens that URL in your browser. You're already signed in to
//      the site there (with GitHub), and you click Approve.
//   3. Meanwhile the editor polls POST /api/device/token with its DeviceCode;
//      once approved it receives an API token and stores it.
//
// The editor never sees your GitHub password or cookie, and the site never
// needs a login form inside the editor. Pending requests live in memory only —
// they expire after 10 minutes anyway, so losing them on restart is fine.
public sealed class DeviceLogin
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    // No 0/O, 1/I/L — codes get read and typed by humans.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public sealed class Request
    {
        public required string DeviceCode { get; init; }
        public required string UserCode { get; init; }
        public DateTime ExpiresAt { get; init; }
        public int? ApprovedUserId { get; set; }
        public bool Denied { get; set; }
    }

    private readonly ConcurrentDictionary<string, Request> _byDevice = new();
    private readonly ConcurrentDictionary<string, Request> _byUser = new();

    public Request Start()
    {
        Prune();
        var request = new Request
        {
            DeviceCode = ApiTokenHandler.Base64Url(RandomNumberGenerator.GetBytes(32)),
            UserCode = RandomCode(4) + "-" + RandomCode(4),
            ExpiresAt = DateTime.UtcNow + Lifetime,
        };
        _byDevice[request.DeviceCode] = request;
        _byUser[request.UserCode] = request;
        return request;
    }

    public Request? FindByUserCode(string userCode) =>
        _byUser.TryGetValue(userCode.Trim().ToUpperInvariant(), out var r) && r.ExpiresAt > DateTime.UtcNow ? r : null;

    public Request? FindByDeviceCode(string deviceCode) =>
        _byDevice.TryGetValue(deviceCode, out var r) && r.ExpiresAt > DateTime.UtcNow ? r : null;

    // A request is single-use: once the editor has collected its token, it's gone.
    public void Complete(Request request)
    {
        _byDevice.TryRemove(request.DeviceCode, out _);
        _byUser.TryRemove(request.UserCode, out _);
    }

    private void Prune()
    {
        foreach (var r in _byDevice.Values.Where(r => r.ExpiresAt <= DateTime.UtcNow).ToList()) Complete(r);
    }

    private static string RandomCode(int length) =>
        new(Enumerable.Range(0, length).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
}
