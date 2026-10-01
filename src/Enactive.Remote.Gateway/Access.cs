namespace Enactive.Remote.Gateway;

/// <summary>
/// Who is asking, proven: a signed-in person and the session that proves it. Every private read and
/// write on a person's behalf takes one of these, so a method that forgot to say whose data it wants
/// does not compile, instead of quietly answering for everyone.
/// </summary>
public sealed record UserAccess(string UserId, string SessionId);

/// <summary>
/// A paired computer asking, and the person it belongs to. The owner is read from the host's own row
/// when its token is checked, never from the request, so a computer cannot name another person's
/// account as its own.
/// </summary>
public sealed record HostAccess(string HostId, string OwnerId);
