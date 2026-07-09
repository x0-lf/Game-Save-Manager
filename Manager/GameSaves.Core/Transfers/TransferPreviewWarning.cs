namespace GameSaves.Core.Transfers
{
    public sealed record TransferPreviewWarning(
        string Code,
        string Message,
        TransferWarningSeverity Severity);
}