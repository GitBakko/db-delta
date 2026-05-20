namespace DbDelta.Core.ObjectModel;

/// <summary>
/// One column participating in an <see cref="Index"/>'s key list.
/// </summary>
public sealed record IndexColumn(string Name, bool IsDescending);
