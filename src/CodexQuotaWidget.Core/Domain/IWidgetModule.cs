namespace CodexQuotaWidget.Core;

/// <summary>
/// Describes a self-contained widget module. Future modules can be added
/// without coupling them to the quota protocol implementation.
/// </summary>
public interface IWidgetModule
{
    string Id { get; }

    string DisplayName { get; }
}
