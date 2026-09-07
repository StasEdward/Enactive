namespace Enactive.App.Ui;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Enactive.App.Ui.ViewModels;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;

/// <summary>
/// The template library: pick a saved task, fill in what it asks for, run it.
///
/// <para>This is the first point at which the feature is usable without typing a prompt, which is
/// the sentence the whole thing was designed from.</para>
///
/// <para>It does not start the run. It resolves a specification and hands it back, because starting
/// one needs the providers, the tool registry and the artifact store - none of which a library has
/// any business holding, and all of which the main window already has.</para>
/// </summary>
internal sealed partial class TemplatesWindow : Window
{
    private readonly TemplatesViewModel _viewModel;
    private readonly Action<ResolvedTaskSpec> _run;

    public TemplatesWindow(string? workspaceRoot, PermissionPolicy workspacePolicy, Action<ResolvedTaskSpec> run)
    {
        _viewModel = new TemplatesViewModel(workspaceRoot, workspacePolicy);
        _run = run;
        DataContext = _viewModel;
        InitializeComponent();
    }

    /// <summary>
    /// Points an already-open library at another workspace.
    ///
    /// <para>The library is per-workspace: the built-ins, your global templates, and the ones that
    /// live in THIS project's .enactive folder. Left open across a workspace switch it would go on
    /// offering the previous project's Release Check while the run resolved against the folder you
    /// are actually in - the list and the truth disagreeing, silently, which is the one failure this
    /// codebase keeps coming back to.</para>
    /// </summary>
    public void FollowWorkspace(string? workspaceRoot, PermissionPolicy workspacePolicy)
        => _viewModel.SetWorkspace(workspaceRoot, workspacePolicy);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRun(object? sender, RoutedEventArgs e)
    {
        // Resolved once more on the way out rather than trusting the last validation pass: the
        // button's enabled state is a view of what was true a keystroke ago, and the specification
        // is what the run is actually built from.
        if (_viewModel.Resolve() is not { } spec)
            return;

        Close();
        _run(spec);
    }
}
