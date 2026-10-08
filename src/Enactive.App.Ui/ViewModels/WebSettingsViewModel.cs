namespace Enactive.App.Ui.ViewModels;

using Enactive.App.Ui.Mvvm;
using Enactive.Tools.Web;
using Enactive.Settings;

/// <summary>
/// The Web pane: whether tasks may read the web (fetch_url), where search goes (web_search, a SearXNG server),
/// and whether either asks first. Off unless a person turns it on - see <c>WebAccess</c>.
/// </summary>
internal sealed partial class SettingsViewModel
{
    private const int SectionWeb = 12;

    public bool IsWeb => Section == SectionWeb;

    public RelayCommand ShowWebCommand { get; private set; } = null!;

    private bool _webEnabled;
    private string _webSearchUrl = string.Empty;
    private bool _webUseWithoutAsking;

    public bool WebEnabled
    {
        get => _webEnabled;
        set { if (Set(ref _webEnabled, value)) WebChanged(); }
    }

    public string WebSearchUrl
    {
        get => _webSearchUrl;
        set { if (Set(ref _webSearchUrl, value)) WebChanged(); }
    }

    public bool WebUseWithoutAsking
    {
        get => _webUseWithoutAsking;
        set { if (Set(ref _webUseWithoutAsking, value)) WebChanged(); }
    }

    /// <summary>What is wrong with the search address, or blank. A typo here is a search that is never there.</summary>
    public string WebProblem
        => WebSearchUrl.Trim().Length > 0
           && !(Uri.TryCreate(WebSearchUrl.Trim(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            ? "The search server's address must be a full http:// or https:// address, e.g. http://localhost:8888."
            : string.Empty;

    public bool HasWebProblem => WebProblem.Length > 0;

    public string WebAsking
        => WebUseWithoutAsking
            ? "Pages are read and searches made without a question. A task nobody is watching - a scheduled one - may use them too."
            : "Each page and each search asks first, naming the address or the query. A task nobody is watching is not offered them.";

    /// <summary>
    /// What the tools will be, in one line - and whether any role has them, which is the step people miss: the
    /// tools are not in the default roles, and a tool no role names is offered to no task.
    /// </summary>
    public string WebState
    {
        get
        {
            if (!WebEnabled) return "Off - tasks cannot read the web.";
            var tools = WebSearchUrl.Trim().Length > 0 && !HasWebProblem
                ? $"{FetchUrlTool.Name} and {WebSearchTool.Name}"
                : $"{FetchUrlTool.Name} (no search server, so no {WebSearchTool.Name})";
            // The role gate's own rule (WebRoles): this read the lists with case, and said nobody could read the
            // web while the engine was letting a role naming Fetch_Url through.
            var roles = WebRoles.Reaching(_working.Workers)
                .Select(w => string.IsNullOrWhiteSpace(w.Role) ? w.Id : w.Role)
                .ToArray();
            return roles.Length == 0
                ? $"On: {tools}. No role has them yet - give them to one under AI · Team: open the role and tick them."
                : $"On: {tools}, for {string.Join(", ", roles)}.";
        }
    }

    private void WebChanged()
    {
        OnPropertyChanged(nameof(WebProblem));
        OnPropertyChanged(nameof(HasWebProblem));
        OnPropertyChanged(nameof(WebAsking));
        OnPropertyChanged(nameof(WebState));
    }

    private void InitializeWeb()
    {
        ShowWebCommand = new(() => Section = SectionWeb);
        _webEnabled = _working.Web.Enabled;
        _webSearchUrl = _working.Web.SearchUrl;
        _webUseWithoutAsking = _working.Web.UseWithoutAsking;
    }

    private void SaveWeb()
    {
        _working.Web.Enabled = WebEnabled;
        _working.Web.SearchUrl = WebSearchUrl.Trim();
        _working.Web.UseWithoutAsking = WebUseWithoutAsking;
    }
}
