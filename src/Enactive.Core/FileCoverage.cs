namespace Enactive.Core.Tools;

/// <summary>
/// What a read showed of one file: how many lines it has, and how many of them - from line 1, without
/// a gap - were shown WHOLE. A file shown only as an excerpt has seen none of its lines whole in that
/// sense, whatever the excerpt contained, because an excerpt is not a basis for rewriting the file.
/// </summary>
public sealed record FileCoverage(string Path, int TotalLines, int LinesShownWhole);
