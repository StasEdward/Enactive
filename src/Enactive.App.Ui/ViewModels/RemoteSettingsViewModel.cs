namespace Enactive.App.Ui.ViewModels;

using Enactive.App.Ui.Mvvm;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Host;

/// <summary>
/// The Remote access pane: whether this computer answers a phone, and the connection code that says
/// which gateway it reaches, which computer it is there, and which browser it trusts first.
///
/// <para>One box, where there used to be an address box and a token box. The code carries both and
/// more - the computer's id, the first browser's key and the pairing secret that lets that browser
/// trust the first key it is granted - and copying five values across by hand is five chances to get
/// one wrong. The first person to fill in an earlier version of this pane put the computer's NAME in
/// its id box, was told nothing was wrong, and got a panel that said Offline.</para>
///
/// <para>Connect applies the code at once rather than on Save: it changes this computer's keys, and
/// a key store changed by a code whose settings were then cancelled would hold a trusted device and a
/// grant for a computer the settings do not name. What was stored is shown read only afterwards; the
/// token itself never is, and neither is the code, which holds the token and the pairing secret.</para>
/// </summary>
internal sealed partial class SettingsViewModel
{
    private const int SectionRemote = 9;

    public bool IsRemote => Section == SectionRemote;

    public RelayCommand ShowRemoteCommand { get; private set; } = null!;

    public RelayCommand TestRemoteCommand { get; private set; } = null!;

    public RelayCommand ConnectRemoteCommand { get; private set; } = null!;

    /// <summary>
    /// Asks the window to try the stored connection against the real gateway and say what happened.
    /// Set by the window, because the check seals the workspace list with this computer's keys, and
    /// both belong to the running service, not to this pane.
    /// </summary>
    public Func<CancellationToken, Task<string>>? RemoteCheck { get; set; }

    /// <summary>
    /// Asks the window to apply a connection code: the keys, then the settings, saved. The second
    /// argument puts a yes/no question to the person - before keys are replaced, before the computer
    /// moves to another gateway, before a device is admitted. Answers whether the code was applied and
    /// a sentence saying what happened.
    /// </summary>
    public Func<ConnectionCode, Func<string, Task<bool>>, Task<(bool Connected, string Detail)>>? RemoteConnect { get; set; }

    /// <summary>Puts a question about a connection code to the person. No handler means no.</summary>
    public event Func<string, Task<bool>>? RemoteQuestionRequested;

    private bool _remoteEnabled;
    private string _remoteCode = string.Empty;
    private string _remoteCodeProblem = string.Empty;
    private string _remoteProblem = string.Empty;
    private string _remoteResult = string.Empty;
    private bool _remoteBusy;

    /// <summary>Whether to connect at all. What the code stored stays when this is off.</summary>
    public bool RemoteEnabled
    {
        get => _remoteEnabled;
        set { if (Set(ref _remoteEnabled, value)) Revalidate(); }
    }

    /// <summary>
    /// The connection code as pasted. Held only until Connect: it carries the token and the pairing
    /// secret, so it is never written to the settings and is emptied once it has been applied.
    /// </summary>
    public string RemoteCode
    {
        get => _remoteCode;
        set
        {
            if (Set(ref _remoteCode, value))
                RemoteCodeProblem = string.Empty;
        }
    }

    /// <summary>What is wrong with the code, in the parser's own words: what is wrong and what to do.</summary>
    public string RemoteCodeProblem
    {
        get => _remoteCodeProblem;
        private set
        {
            if (Set(ref _remoteCodeProblem, value))
                OnPropertyChanged(nameof(HasRemoteCodeProblem));
        }
    }

    public bool HasRemoteCodeProblem => !string.IsNullOrEmpty(RemoteCodeProblem);

    /// <summary>The stored gateway address, read only.</summary>
    public string RemoteStoredGateway => Shown(_working.RemoteAccess.GatewayUrl);

    /// <summary>The stored computer id, read only.</summary>
    public string RemoteStoredHostId => Shown(_working.RemoteAccess.HostId);

    /// <summary>Whether a token is stored - never the token. A bearer credential on screen is one anyone behind you can copy.</summary>
    public string RemoteTokenState
    {
        get
        {
            var remote = _working.RemoteAccess;

            if (!string.IsNullOrEmpty(remote.Token))
                return "token stored";

            // Encrypted on disk and unreadable here: the settings were copied from another computer
            // or the Windows account was rebuilt. Said, because "no token" would send the person
            // looking for the wrong problem.
            return string.IsNullOrEmpty(remote.TokenProtected)
                ? "no token"
                : "a token is stored that this Windows account cannot decrypt - connect again with a new code";
        }
    }

    /// <summary>
    /// What would stop this from connecting, in the pane. Shown rather than enforced by disabling
    /// Save: this window saves every section at once, and a disabled Save button would mean an
    /// unfinished remote pane blocking a change to the log retention.
    /// </summary>
    public string RemoteProblem
    {
        get => _remoteProblem;
        private set => Set(ref _remoteProblem, value);
    }

    public bool HasRemoteProblem => !string.IsNullOrEmpty(RemoteProblem);

    /// <summary>What the last check or connect found, in a person's words.</summary>
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

    /// <summary>
    /// A check or a connect is running. The buttons say so rather than looking like they did nothing,
    /// and Save waits: this window is not modal, and a Save while a code was being applied could write
    /// the old computer's settings over the new one's keys - settings and keys for different computers.
    /// </summary>
    public bool RemoteBusy
    {
        get => _remoteBusy;
        private set
        {
            if (Set(ref _remoteBusy, value))
            {
                OnPropertyChanged(nameof(CanUseRemote));
                SaveCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanUseRemote => !RemoteBusy;

    private void InitializeRemote()
    {
        ShowRemoteCommand = new(() => Section = SectionRemote);
        TestRemoteCommand = new(() => _ = TestRemoteAsync());
        ConnectRemoteCommand = new(() => _ = ConnectRemoteAsync());

        _remoteEnabled = _working.RemoteAccess.Enabled;

        Revalidate();
    }

    /// <summary>
    /// Reads the pasted code and has the window apply it.
    ///
    /// <para>On success the working copy takes what was stored, so a Save pressed afterwards writes the
    /// same values back instead of the ones this pane opened with - which would quietly undo the
    /// connection the person just made.</para>
    /// </summary>
    private async Task ConnectRemoteAsync()
    {
        if (Pairing.TryRead(RemoteCode, out var problem) is not { } code)
        {
            RemoteCodeProblem = problem;
            return;
        }

        if (RemoteConnect is not { } connect)
            return;

        RemoteBusy = true;
        RemoteResult = "Connecting…";

        try
        {
            var (connected, detail) = await connect(code, AskAsync);
            RemoteResult = detail;

            if (!connected)
                return;

            RemoteAccessService.Remember(code, _working.RemoteAccess);

            RemoteCode = string.Empty;
            _remoteEnabled = true;
            OnPropertyChanged(nameof(RemoteEnabled));
            OnPropertyChanged(nameof(RemoteStoredGateway));
            OnPropertyChanged(nameof(RemoteStoredHostId));
            OnPropertyChanged(nameof(RemoteTokenState));
            Revalidate();
        }
        catch (Exception failure)
        {
            RemoteResult = "The code could not be applied: " + failure.Message;
        }
        finally
        {
            RemoteBusy = false;
        }
    }

    private async Task<bool> AskAsync(string question)
        => RemoteQuestionRequested is { } ask && await ask(question);

    /// <summary>Tries the stored connection: hello, and one sync with the sealed workspace list.</summary>
    private async Task TestRemoteAsync()
    {
        if (RemoteCheck is not { } check)
            return;

        RemoteBusy = true;
        RemoteResult = "Connecting…";

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            RemoteResult = await check(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            RemoteResult = "The gateway did not answer within thirty seconds. Check that it is up and reachable.";
        }
        catch (Exception failure)
        {
            RemoteResult = "The check could not be run: " + failure.Message;
        }
        finally
        {
            RemoteBusy = false;
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
    /// What would stop this from connecting. Only said when it is turned on - settings that are
    /// switched off are somebody part-way through, not a mistake to complain about.
    /// </summary>
    private string? RemoteFault()
    {
        if (!RemoteEnabled)
            return null;

        var remote = _working.RemoteAccess;

        if (string.IsNullOrEmpty(remote.HostId) || string.IsNullOrEmpty(remote.GatewayUrl))
            return "Paste the connection code the browser shows when you register this computer, and press Connect.";

        if (string.IsNullOrEmpty(remote.Token))
            return "This computer's token cannot be read here. Make a new connection code in the browser and connect with it.";

        return null;
    }

    /// <summary>
    /// Writes the pane back. Called from <see cref="Save"/>, before the settings are handed over. Only
    /// the switch: the address, the computer id and the token are written by Connect, from a code.
    /// </summary>
    private void SaveRemote() => _working.RemoteAccess.Enabled = RemoteEnabled;

    private static string Shown(string value) => string.IsNullOrEmpty(value) ? "not connected" : value;
}
