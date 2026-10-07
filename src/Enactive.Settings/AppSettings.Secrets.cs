namespace Enactive.Settings;

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
internal sealed record SettingsSecret(
    string Name,
    Func<string> Stored,
    Action<string> Store,
    Func<string> Plain,
    Func<string, bool> Use,
    string Unreadable,
    string Unencrypted,
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
    /// SettingsSecretsTests fails until every field named "...Protected" is one.</para>
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
                Unavailable: () => server.CredentialsUnavailable,
                CannotRead: () => server.CredentialsUnavailable = true));

        return secrets;

        static SettingsSecret Text(string name, Func<string> stored, Action<string> store, Func<string> plain,
            Action<string> use, string unreadable, string unencrypted)
            => new(name, stored, store, plain, value => { use(value); return value.Length > 0; }, unreadable, unencrypted);
    }

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
