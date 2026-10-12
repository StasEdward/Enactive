namespace Enactive.Application;

using Enactive.Workspace;

/// <summary>What a template's edge says: where it comes from, or that it is the one being edited.</summary>
internal enum TemplateEdge { Builtin, Global, Workspace, Selected }

internal static class TemplateEdges
{
    /// <summary>
    /// Where a template comes from, as its edge says - every origin named. One added later is a failure here, as it is
    /// in the palette; it was drawn as a workspace's own without a word.
    /// </summary>
    public static TemplateEdge Of(TemplateOrigin origin) => origin switch
    {
        TemplateOrigin.Builtin => TemplateEdge.Builtin,
        TemplateOrigin.Global => TemplateEdge.Global,
        TemplateOrigin.Workspace => TemplateEdge.Workspace,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "A template's origin with no edge.")
    };
}
