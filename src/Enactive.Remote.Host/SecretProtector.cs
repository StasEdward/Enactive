namespace Enactive.Remote.Host;

using System.Text;
using Enactive.Secrets;

/// <summary>
/// How <see cref="HostKeyStore"/> keeps a secret at rest: the epoch keys, the signing key, an open
/// invitation's secret.
///
/// <para>A seam because DPAPI exists only on Windows, and <c>Secret.Protect</c> refuses everywhere else.
/// The store called it directly, so no computer could be run on Linux at all - not the end-to-end tests
/// on the gateway's CI job, which has to be Linux for its MySQL service, and not the harness the
/// browser tests pair with. The desktop always uses <see cref="SecretProtector.Dpapi"/>, the default.</para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>The secret as it is written down.</summary>
    string Protect(string clear);

    /// <summary>Whether <paramref name="stored"/> is something this protector wrote.</summary>
    bool IsProtected(string stored);

    /// <summary>The secret, or an empty string when this protector cannot read it.</summary>
    string Unprotect(string stored);
}

/// <summary>The protectors there are.</summary>
public static class SecretProtector
{
    /// <summary>Windows DPAPI for this user, through <see cref="Secret"/>: what the desktop uses.</summary>
    public static ISecretProtector Dpapi { get; } = new DpapiProtector();

    /// <summary>
    /// <b>Stores secrets in the clear</b>, base64 behind a <c>clear:</c> marker. It exists only for tests
    /// and the browser-test harness, which run a computer where there is no DPAPI, and it is internal so
    /// that nothing the desktop is built from can reach it. The marker keeps the two apart: DPAPI refuses
    /// a store this wrote, and this refuses one DPAPI wrote, as each refuses a value it did not make.
    /// </summary>
    internal static ISecretProtector ForTestsOnly { get; } = new ClearForTests();

    private sealed class DpapiProtector : ISecretProtector
    {
        public string Protect(string clear) => Secret.Protect(clear);

        public bool IsProtected(string stored) => Secret.IsProtected(stored);

        public string Unprotect(string stored) => Secret.Unprotect(stored);
    }

    private sealed class ClearForTests : ISecretProtector
    {
        private const string Marker = "clear:";

        public string Protect(string clear) => Marker + Convert.ToBase64String(Encoding.UTF8.GetBytes(clear));

        public bool IsProtected(string stored) => stored.StartsWith(Marker, StringComparison.Ordinal);

        public string Unprotect(string stored)
        {
            if (!IsProtected(stored)) return string.Empty;
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[Marker.Length..]));
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }
    }
}
