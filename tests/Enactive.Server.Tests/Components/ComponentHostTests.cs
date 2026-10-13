namespace Enactive.Server.Tests.Components;

using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;

/// <summary>
/// The component host the page tests stand on: a page rendered in it sees the account that signed in and its role, as a
/// page on the service does - so a later test of "a Viewer sees no Start button" is about the page, not the host.
/// </summary>
public sealed class ComponentHostTests : ComponentHost
{
    /// <summary>What a page reads of the signed-in person: the name, and the role claim.</summary>
    private sealed class WhoAmI : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<AuthorizeView>(0);
            builder.AddAttribute(1, nameof(AuthorizeView.Authorized), (RenderFragment<AuthenticationState>)(state => inner =>
                inner.AddContent(2, $"{state.User.Identity!.Name}/{state.User.FindFirstValue(ClaimTypes.Role)}")));
            builder.AddAttribute(3, nameof(AuthorizeView.NotAuthorized), (RenderFragment<AuthenticationState>)(_ => inner =>
                inner.AddContent(4, "nobody")));
            builder.CloseComponent();
        }
    }

    /// <summary>Shown only to those the server's own policy of this name lets in.</summary>
    private sealed class Gated : ComponentBase
    {
        [Parameter, EditorRequired] public string Policy { get; set; } = "";

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<AuthorizeView>(0);
            builder.AddAttribute(1, nameof(AuthorizeView.Policy), Policy);
            builder.AddAttribute(2, nameof(AuthorizeView.ChildContent), (RenderFragment<AuthenticationState>)(_ => inner => inner.AddContent(3, "in")));
            builder.AddAttribute(4, nameof(AuthorizeView.NotAuthorized), (RenderFragment<AuthenticationState>)(_ => inner => inner.AddContent(5, "out")));
            builder.CloseComponent();
        }
    }

    [Theory]
    [InlineData("ada", "Admin")]
    [InlineData("alice", "Operator")]
    [InlineData("vera", "Viewer")]
    public void A_component_sees_the_signed_in_account_and_role(string name, string role)
    {
        SignIn(name);

        Assert.Equal($"{name}/{role}", Render<WhoAmI>().Markup);
    }

    [Fact]
    public void A_component_sees_nobody_until_someone_signs_in()
        => Assert.Equal("nobody", Render<WhoAmI>().Markup);

    /// <summary>
    /// The server's policies decide, not a stand-in that lets everybody in or throws: what a Viewer is not shown on a
    /// page is tested against the rule the server applies.
    /// </summary>
    [Theory]
    [InlineData("vera", "Viewer", "in")]
    [InlineData("vera", "Operator", "out")]
    [InlineData("alice", "Operator", "in")]
    [InlineData("alice", "Admin", "out")]
    [InlineData("ada", "Admin", "in")]
    public void A_policy_lets_in_the_roles_the_server_lets_in(string name, string policy, string shown)
    {
        SignIn(name);

        Assert.Equal(shown, Render<Gated>(p => p.Add(g => g.Policy, policy)).Markup);
    }
}
