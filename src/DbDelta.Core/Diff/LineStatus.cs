namespace DbDelta.Core.Diff;

public enum LineStatus
{
    Unchanged,
    Added,    // present only on the target side
    Removed,  // present only on the source side
    Modified  // both sides have content on this aligned row but text differs
}
