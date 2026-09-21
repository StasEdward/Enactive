namespace Enactive.App.Ui.ViewModels;

using Enactive.App.Ui.Mvvm;

/// <summary>
/// The Remote access pane: whether this computer answers a phone, which gateway it reaches, and the
/// token that says it is this computer.
///
/// <para>Three things, and it used to ask for five. A computer id and a display name were on it and
/// neither was ever read - the hub takes the Host from the authenticated identity, so the token
/// alone says which computer this is. A required box that changes nothing is worse than a missing
/// one: the first person to fill this in put the computer's NAME in the id box, was told nothing
/// was wrong, and got a panel that said Offline.</para>
///
/// <para>The token is shown masked and is never read back out of the box once saved - what the box
/// shows after a reopen is a placeholder, and leaving it alone keeps the stored token. That is the
/// only way to have a token box that can be left alone: a box pre-filled with the real token puts a
/// bearer credential on screen for anyone standing behind you, and a box that empties itself on
/// every open would silently clear the token of anyone who opened the pane to change the address.
/// </para>
/// </summary>
internal sealed partial class SettingsViewModel
{
    private const int SectionRemote = 9;

    /// <summary>
    /// What the token box shows for a token that is already stored. Not a value anybody can type by
    /// accident, and checked on save rather than compared loosely: a token of exactly these
    /// characters would be indistinguishable from "unchanged", so it is refused as a token.
    /// </summary>
    private const string TokenUnchanged = "········ (stored)";

    public bool IsRemote => Section == SectionRemote;

    public RelayCommand ShowRemoteCommand { get; private set; } = null!;

    public RelayCommand TestRemoteCommand { get; private set; } = null!;

    /// <summary>
    /// Asks the window to try these settings against the real gateway and say what happened. Set by
    /// the window, because the check needs the workspace list and that lives in the registry, not
    /// here.
    /// </summary>
    public Func<string, string, CancellationToken, Task<string>>? RemoteCheck { get; set; }

    private bool _remoteEnabled;
    private string _remoteGatewayUrl = string.Empty;
    private string _remoteToken = string.Empty;
    private string _remoteProblem = string.Empty;
    private string _remoteResult = string.Empty;
    private bool _remoteTesting;

    /// <summary>
    /// There is a token on disk that this Windows account cannot decrypt. Remembered because the
    /// box is then empty and looks exactly like "no token was ever set", which would send the user
    /// looking for the wrong problem.
    /// </summary>
    private bool _remoteTokenUnreadable;

    /// <summary>The token as it stood when the pane opened, for a check that ran before Save.</summary>
    private string _remoteStoredToken = string.Empty;

    /// <summary>Whether to connect at all. Everything else stays filled in when this is off.</summary>
    public bool RemoteEnabled
    {
        get => _remoteEnabled;
        set { if (Set(ref _remoteEnabled, value)) Revalidate(); }
    }

    public string RemoteGatewayUrl
    {
        get => _remoteGatewayUrl;
        set { if (Set(ref _remoteGatewayUrl, value)) Revalidate(); }
    }

    public string RemoteToken
    {
        get => _remoteToken;
        set { if (Set(ref _remoteToken, value)) Revalidate(); }
    }

    /// <summary>
    /// What is missing or wrong, in the pane, as it is typed. Shown rather than enforced by
    /// disabling Save: this window saves every section at once, and a disabled Save button would
    /// mean an unfinished remote pane blocking a change to the log retention.
    /// </summary>
    public string RemoteProblem
    {
        get => _remoteProblem;
        private set => Set(ref _remoteProblem, value);
    }

    public bool HasRemoteProblem => !string.IsNullOrEmpty(RemoteProblem);

    /// <summary>What the last check found, in a person's words.</summary>
    public string RemoteResult
    {
        get => _remoteResult;
        private set
        {
            if (Set(ref _remoteResult, value))
                OnPropertyChanged(nameof(HasRemoteResult));
        }
    }

    public bool HasRemoteResult => !string.IsNullOrEmpty(RemoteResult);

    /// <summary>A check is running. The button says so rather than looking like it did nothing.</summary>
    public bool RemoteTesting
    {
        get => _remoteTesting;
        private set
        {
            if (Set(ref _remoteTesting, value))
                OnPropertyChanged(nameof(CanTestRemote));
        }
    }

    public bool CanTestRemote => !RemoteTesting;

    private void InitializeRemote()
    {
        ShowRemoteCommand = new(() => Section = SectionRemote);
        TestRemoteCommand = new(() => _ = TestRemoteAsync());

        var remote = _working.RemoteAccess;
        _remoteEnabled = remote.Enabled;
        _remoteGatewayUrl = remote.GatewayUrl;
        _remoteStoredToken = remote.Token;
        _remoteToken = string.IsNullOrEmpty(remote.Token) ? string.Empty : TokenUnchanged;
        _remoteTokenUnreadable =
            string.IsNullOrEmpty(remote.Token) && !string.IsNullOrEmpty(remote.TokenProtected);

        Revalidate();
    }

    /// <summary>
    /// Tries the settings AS TYPED, without saving them first.
    ///
    /// <para>Without saving on purpose. Somebody checking a token they have just pasted has not
    /// decided to keep it yet, and a check that wrote it down first would make "does this work" and
    /// "use this from now on" the same button.</para>
    /// </summary>
    private async Task TestRemoteAsync()
    {
        if (RemoteCheck is not { } check)
            return;

        RemoteTesting = true;
        RemoteResult = "Connecting…";

        try
        {
            // The placeholder means the stored token, which is the one a check should use: the
            // common case is somebody who changed only the address.
            var token = RemoteToken == TokenUnchanged ? _remoteStoredToken : RemoteToken.Trim();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            RemoteResult = await check(RemoteGatewayUrl.Trim(), token, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            RemoteResult = "The gateway did not answer within thirty seconds. Check the address.";
        }
        catch (Exception failure)
        {
            RemoteResult = "The check could not be run: " + failure.Message;
        }
        finally
        {
            RemoteTesting = false;
        }
    }

    /// <summary>
    /// Every pane's complaint, recomputed. Shared because Save consults them together and a
    /// second method would be a second thing to forget when a pane is added.
    /// </summary>
    private void Revalidate()
    {
        RemoteProblem = RemoteFault() ?? string.Empty;
        OnPropertyChanged(nameof(HasRemoteProblem));

        SmtpProblem = SmtpValidation();
        OnPropertyChanged(nameof(SmtpState));
    }

    /// <summary>
    /// The first thing that would stop this from connecting, in the order somebody fills the pane
    /// in. Only checked when it is turned on - half-filled settings that are switched off are
    /// somebody part-way through, not a mistake to complain about.
    /// </summary>
    private string? RemoteFault()
    {
        if (!RemoteEnabled)
            return null;

        if (string.IsNullOrWhiteSpace(RemoteGatewayUrl))
            return "Enter the gateway address, e.g. https://remote.enactive.dev";

        if (!Uri.TryCreate(RemoteGatewayUrl.Trim(), UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            return "The gateway address must be a full http:// or https:// URL.";

        // Said rather than refused. A gateway on localhost over http is how this is developed, and
        // over the internet it means the token is on the wire in the clear.
        if (address.Scheme == Uri.UriSchemeHttp && !address.IsLoopback)
            return "This address is http, so the device token travels unencrypted. Use https unless "
                 + "the gateway is on this machine.";

        if (string.IsNullOrEmpty(RemoteToken))
            return _remoteTokenUnreadable
                ? "There is a token stored, but this Windows account cannot decrypt it - these "
                + "settings were most likely copied from another computer. Issue a new token and "
                + "paste it here."
                : "Paste the device token the gateway issued.";

        return null;
    }

    /// <summary>
    /// Writes the pane back. Called from <see cref="Save"/>, before the settings are handed over.
    /// </summary>
    private void SaveRemote()
    {
        var remote = _working.RemoteAccess;

        remote.Enabled = RemoteEnabled;
        remote.GatewayUrl = RemoteGatewayUrl.Trim();

        // The placeholder means "leave the stored token alone". Anything else - including an empty
        // box - is what the user meant to have, so clearing the box clears the token.
        if (RemoteToken != TokenUnchanged)
            remote.Token = RemoteToken.Trim();
    }
}
