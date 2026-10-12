namespace Enactive.Settings;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Enactive.Secrets;

/// <summary>
/// One secret the settings hold: what it is called, where its encrypted form is kept, and what it is in memory.
/// </summary>
/// <param name="Name">What it is, for a sentence: "the SMTP password".</param>
/// <param name="Stored">The form that reaches disk - encrypted, or what an older build left there.</param>
/// <param name="Plain">The form used in memory; "" when there is none.</param>
/// <param name="Use">Puts a decrypted value to use; false when it is not one this secret can use.</param>
/// <param name="Unreadable">Said when it is encrypted and cannot be decrypted here.</param>
/// <param name="Unencrypted">Said when the file holds it in the clear.</param>
/// <param name="Unavailable">
/// For a secret whose owner says itself that it could not be read (an MCP server, which is then off): whether it
/// is. Such a secret keeps its stored form untouched while it is, and is otherwise written exactly as it stands -
/// an empty one removes it - because the person editing that server is the one who decides.
/// </param>
/// <param name="Blank">Removes the plaintext, for a copy that must hold none (<see cref="AppSettings.WithoutSecrets"/>).</param>
/// <param name="FillBlank">
/// Puts back, from the decrypted value of the secret it was made from, what <see cref="Blank"/> removed and nobody has
/// typed over since (<see cref="AppSettings.RestoreSecrets"/>).
/// </param>
internal sealed record SettingsSecret(
    string Name,
    Func<string> Stored,
    Action<string> Store,
    Func<string> Plain,
    Func<string, bool> Use,
    string Unreadable,
    string Unencrypted,
    Action Blank,
    Action<string> FillBlank,
    Func<bool>? Unavailable = null,
    Action? CannotRead = null);

public sealed partial class AppSettings
{
    /// <summary>
    /// Every secret these settings hold, in one list that loading, saving and the startup report all go through.
    ///
    /// <para><b>Why a list.</b> Each kind of secret - provider keys, the legacy Anthropic key, the remote device
    /// token, the SMTP password, MCP credentials - used to be decrypted, encrypted, reported unreadable and
    /// reported unencrypted by hand, four places for each. The copies drifted: a password left in the clear
    /// was reported for a provider and not for SMTP, and MCP credentials that could not be decrypted were
    /// never reported at all - the server was quietly off. A new kind of secret is one entry here, and
    /// SettingsDeclaredOnceTests.Every_protected_field_is_one_of_the_settings_secrets fails until every field named
    /// "...Protected" is one.</para>
    /// </summary>
    internal IReadOnlyList<SettingsSecret> Secrets()
    {
        var secrets = new List<SettingsSecret>();

        foreach (var provider in Providers)
            secrets.Add(Text($"the API key for provider '{provider.Id}'",
                () => provider.ApiKeyProtected, v => provider.ApiKeyProtected = v,
                () => provider.ApiKey, v => provider.ApiKey = v,
                $"The API key for provider '{provider.Id}' cannot be decrypted. Its encrypted value will be preserved "
                + "on save; enter a replacement key to use it here.",
                $"The API key for provider '{provider.Id}' is stored UNENCRYPTED in settings.json. It will be "
                + "encrypted the next time settings are saved."));

        secrets.Add(Text("the legacy Anthropic key",
            () => AnthropicApiKeyProtected, v => AnthropicApiKeyProtected = v,
            () => AnthropicApiKey, v => AnthropicApiKey = v,
            "The legacy Anthropic key cannot be decrypted. Its encrypted value will be preserved on save.",
            "The stored Anthropic API key is UNENCRYPTED in settings.json. It will be encrypted the next time "
            + "settings are saved."));

        secrets.Add(Text("the remote access device token",
            () => RemoteAccess.TokenProtected, v => RemoteAccess.TokenProtected = v,
            () => RemoteAccess.Token, v => RemoteAccess.Token = v,
            // DPAPI ciphertext bound to a user and machine that no longer exist will never open again, so this is
            // not something to keep and retry - it is a token that has to be reissued. Said out loud, because
            // otherwise remote access simply stops working while the pane still shows a token stored.
            "The remote access device token cannot be decrypted by this Windows account - these settings were most "
            + "likely copied from another computer. Make a new connection code in the browser and connect with it "
            + "under Remote access; this computer cannot connect until you do.",
            "The remote access device token is stored UNENCRYPTED in settings.json. It will be encrypted the next "
            + "time settings are saved. Anyone who can read that file can connect as this computer, so consider "
            + "revoking it and connecting again with a new connection code."));

        secrets.Add(Text("the SMTP password",
            () => Smtp.PasswordProtected, v => Smtp.PasswordProtected = v,
            () => Smtp.Password, v => Smtp.Password = v,
            "The SMTP password cannot be decrypted. Its encrypted value will be preserved on save; enter a "
            + "replacement password to use it here.",
            "The SMTP password is stored UNENCRYPTED in settings.json. It will be encrypted the next time settings "
            + "are saved."));

        foreach (var server in McpServers)
            secrets.Add(new SettingsSecret($"the credentials for MCP server '{server.Id}'",
                () => server.SecretsProtected, v => server.SecretsProtected = v,
                // Its environment and headers together, as one value: they are encrypted and lost together.
                () => server.Environment.Count == 0 && server.Headers.Count == 0
                    ? ""
                    : JsonSerializer.Serialize(new McpSecrets(server.Environment, server.Headers)),
                plain =>
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<McpSecrets>(plain) is not { Environment: { } environment, Headers: { } headers })
                            return false;
                        server.Environment = environment;
                        server.Headers = headers;
                        return true;
                    }
                    catch (JsonException) { return false; }
                },
                $"The credentials for MCP server '{server.Id}' cannot be decrypted, so the server is off until they "
                + "are entered again: edit it under MCP and re-enter its environment and headers, or remove it.",
                $"The credentials for MCP server '{server.Id}' are stored UNENCRYPTED in settings.json. They will be "
                + "encrypted the next time settings are saved.",
                // The names stay - a page says which are set - and only the values go.
                Blank: () =>
                {
                    server.Environment = server.Environment.ToDictionary(p => p.Key, _ => "");
                    server.Headers = server.Headers.ToDictionary(p => p.Key, _ => "");
                },
                // Each of the two on its own: new headers typed over a server whose environment nobody touched must
                // not take the environment with them, and the other way round.
                FillBlank: plain =>
                {
                    McpSecrets? kept;
                    try { kept = JsonSerializer.Deserialize<McpSecrets>(plain); }
                    catch (JsonException) { return; }
                    if (kept is null)
                        return;
                    if (kept.Environment is { } environment && server.Environment.Values.All(v => v.Length == 0))
                        server.Environment = new(environment);
                    if (kept.Headers is { } headers && server.Headers.Values.All(v => v.Length == 0))
                        server.Headers = new(headers);
                },
                Unavailable: () => server.CredentialsUnavailable,
                CannotRead: () => server.CredentialsUnavailable = true));

        return secrets;

        static SettingsSecret Text(string name, Func<string> stored, Action<string> store, Func<string> plain,
            Action<string> use, string unreadable, string unencrypted)
            => new(name, stored, store, plain, value => { use(value); return value.Length > 0; }, unreadable, unencrypted,
                Blank: () => use(""),
                FillBlank: value =>
                {
                    if (plain().Length == 0)
                        use(value);
                });
    }

    /// <summary>What a stored form is called in a copy that holds no secret (<see cref="WithoutSecrets"/>).</summary>
    private const string KeptPrefix = "kept:";

    // A key of this process's own: a handle names a stored secret to the settings it was made from and says nothing
    // about it to anybody else - not even whether two copies opened at different times hold the same one.
    private static readonly byte[] HandleKey = RandomNumberGenerator.GetBytes(32);

    private static string Handle(string stored)
        => KeptPrefix + Convert.ToHexString(HMACSHA256.HashData(HandleKey, Encoding.UTF8.GetBytes(stored)));

    /// <summary>
    /// A copy that holds no secret at all, for a host whose pages edit the settings - the service's, where the copy a
    /// page edits lives as long as the page does. Every plaintext is removed, and every stored form, which is the
    /// ciphertext or - from an older build - the secret in the clear, is replaced by a handle that names it.
    ///
    /// <para>The page says whether a secret is kept (<see cref="IsKept"/>), takes a new one typed over the blank, and
    /// removes one by emptying its stored form too. <see cref="RestoreSecrets"/> puts back every one that was left
    /// alone, from the settings the copy was made of. Handed the decrypted settings, as it used to be, the page held
    /// every key and password in the clear for as long as it was open.</para>
    /// </summary>
    public AppSettings WithoutSecrets()
    {
        var copy = Clone();
        foreach (var secret in copy.Secrets())
        {
            secret.Blank();
            if (secret.Stored() is { Length: > 0 } stored)
                secret.Store(Handle(stored));
        }
        copy._unreadableSecrets = new HashSet<string>(_unreadableSecrets.Select(Handle), StringComparer.Ordinal);
        return copy;
    }

    /// <summary>
    /// Puts back into a copy made by <see cref="WithoutSecrets"/> the secrets it left alone, from <paramref name="from"/>
    /// - the settings it was made of, read again: every handle becomes the stored form it names, and every plaintext
    /// left blank the secret decrypted from it. One typed over the blank stays as typed; one whose stored form was
    /// emptied stays removed.
    /// </summary>
    /// <returns>
    /// The secrets whose handle names nothing in <paramref name="from"/>, which are left removed - the settings were
    /// changed since the copy was made. Empty when every one was found.
    /// </returns>
    public IReadOnlyList<string> RestoreSecrets(AppSettings from)
    {
        var kept = new Dictionary<string, SettingsSecret>(StringComparer.Ordinal);
        foreach (var secret in from.Secrets())
            if (secret.Stored() is { Length: > 0 } stored)
                kept.TryAdd(Handle(stored), secret);

        var missing = new List<string>();
        foreach (var secret in Secrets())
        {
            if (!secret.Stored().StartsWith(KeptPrefix, StringComparison.Ordinal))
                continue;
            if (!kept.TryGetValue(secret.Stored(), out var original))
            {
                // Never written as it is: a handle on disk would be a secret replaced by a name for it.
                secret.Store("");
                missing.Add(secret.Name);
                continue;
            }
            secret.Store(original.Stored());
            secret.FillBlank(original.Plain());
        }
        // One that cannot be decrypted is kept as it was on save (ProtectOrPreserve), as it is for settings loaded.
        _unreadableSecrets.UnionWith(from._unreadableSecrets);
        return missing;
    }

    /// <summary>Whether a secret is kept in this stored form - the ciphertext, or a handle for it: what a page says instead.</summary>
    public static bool IsKept(string stored) => !string.IsNullOrEmpty(stored);

    /// <summary>
    /// Whether the secret kept in this stored form could not be decrypted when the settings were read (the file came
    /// from another computer, or the account was rebuilt). Asked by the form and not by an empty plaintext beside a
    /// stored one, which is also what every secret of a copy made by <see cref="WithoutSecrets"/> looks like.
    /// </summary>
    public bool CannotDecrypt(string stored) => _unreadableSecrets.Contains(stored);

    /// <summary>Decrypts every secret into memory, and notes the ones that cannot be.</summary>
    private void UnprotectSecrets()
    {
        foreach (var secret in Secrets())
        {
            var stored = secret.Stored();
            if (string.IsNullOrEmpty(stored))
                continue;

            if (!secret.Use(Secret.Unprotect(stored)))
            {
                secret.CannotRead?.Invoke();
                _unreadableSecrets.Add(stored);
            }
        }
    }

    /// <summary>
    /// Encrypts every secret for disk. One that could not be decrypted is kept as it was rather than replaced by
    /// nothing - see <see cref="ProtectOrPreserve"/> - and one that cannot be encrypted stops the save, named.
    /// </summary>
    private void ProtectSecrets()
    {
        foreach (var secret in Secrets())
        {
            if (secret.Unavailable?.Invoke() == true)
                continue;

            try
            {
                secret.Store(secret.Unavailable is null
                    ? ProtectOrPreserve(secret.Plain(), secret.Stored())
                    : Secret.Protect(secret.Plain()));
            }
            catch (SecretProtectionException ex)
            {
                // Protect throws rather than handing back anything unencrypted; this says WHICH secret it was,
                // since the save takes the whole file down with it.
                throw new InvalidOperationException($"{char.ToUpperInvariant(secret.Name[0])}{secret.Name[1..]} could not be "
                    + "encrypted, so nothing was saved. " + ex.Message, ex);
            }
        }
    }

    /// <summary>What the startup report says about the secrets: each one in the clear, then each one that cannot be read.</summary>
    private IEnumerable<string> SecretProblems()
    {
        var secrets = Secrets();

        // Left working - locking someone out of their own settings would be worse - but never left unsaid. This
        // is the residue of a build whose encryption failed quietly and stored the plaintext in the "protected"
        // field; the next successful save encrypts them.
        foreach (var secret in secrets)
            if (secret.Stored() is { Length: > 0 } stored && !Secret.IsProtected(stored))
                yield return secret.Unencrypted;

        foreach (var secret in secrets)
            if (secret.Unavailable?.Invoke()
                ?? (Secret.IsProtected(secret.Stored()) && string.IsNullOrEmpty(secret.Plain())))
                yield return secret.Unreadable;
    }
}
