using GameSaves.Core.Transfers;

namespace GameSaves.App.Models
{
    public sealed class TransferWarningRowViewModel
    {
        public TransferWarningRowViewModel(TransferPreviewWarning warning)
        {
            Warning = warning;
        }

        public TransferPreviewWarning Warning { get; }

        public string Code => Warning.Code;

        public string Message => Warning.Message;

        public TransferWarningSeverity Severity => Warning.Severity;

        public string SeverityText => Warning.Severity.ToString();
    }
}