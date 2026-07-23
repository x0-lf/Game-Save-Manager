using Google;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using System.Net;

namespace GameSaves.Infrastructure.GoogleDrive
{
    internal sealed record GoogleDriveAccountInfo(
        string? DisplayName,
        string? EmailAddress);

    internal interface IGoogleDriveAccountReader
    {
        Task<GoogleDriveAccountInfo> ReadAsync(
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken);
    }

    internal enum GoogleDriveAccountReadFailure
    {
        AuthorizationRevoked,
        Unavailable,
        Failed
    }

    internal sealed class GoogleDriveAccountReadException : Exception
    {
        public GoogleDriveAccountReadException(GoogleDriveAccountReadFailure failure)
            : base("Google Drive account metadata could not be read.") => Failure = failure;

        public GoogleDriveAccountReadFailure Failure { get; }
    }

    internal sealed class GoogleDriveAccountReader : IGoogleDriveAccountReader
    {
        internal const string RequestedFields = "user(displayName,emailAddress)";

        public async Task<GoogleDriveAccountInfo> ReadAsync(
            GoogleAuthorizedCredential credential,
            CancellationToken cancellationToken)
        {
            using var drive = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential.Credential,
                ApplicationName = "Game Save Manager"
            });

            AboutResource.GetRequest request = drive.About.Get();
            request.Fields = RequestedFields;
            Google.Apis.Drive.v3.Data.About about;

            try
            {
                about = await request.ExecuteAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GoogleApiException ex) when (IsConfirmedAuthenticationFailure(ex))
            {
                throw new GoogleDriveAccountReadException(
                    GoogleDriveAccountReadFailure.AuthorizationRevoked);
            }
            catch (GoogleApiException ex) when (
                ex.HttpStatusCode is HttpStatusCode.RequestTimeout or
                    HttpStatusCode.TooManyRequests or
                    HttpStatusCode.InternalServerError or
                    HttpStatusCode.BadGateway or
                    HttpStatusCode.ServiceUnavailable or
                    HttpStatusCode.GatewayTimeout)
            {
                throw new GoogleDriveAccountReadException(
                    GoogleDriveAccountReadFailure.Unavailable);
            }
            catch (HttpRequestException)
            {
                throw new GoogleDriveAccountReadException(
                    GoogleDriveAccountReadFailure.Unavailable);
            }
            catch
            {
                throw new GoogleDriveAccountReadException(
                    GoogleDriveAccountReadFailure.Failed);
            }

            string? displayName = Normalize(about.User?.DisplayName, 200);
            string? email = Normalize(about.User?.EmailAddress, 320);

            if (displayName is null && email is null)
                throw new GoogleDriveAccountReadException(
                    GoogleDriveAccountReadFailure.Failed);

            return new GoogleDriveAccountInfo(displayName, email);
        }

        private static bool IsConfirmedAuthenticationFailure(GoogleApiException exception)
        {
            if (exception.HttpStatusCode == HttpStatusCode.Unauthorized)
                return true;

            if (exception.HttpStatusCode != HttpStatusCode.Forbidden)
                return false;

            return exception.Error?.Errors?.Any(error =>
                string.Equals(error.Reason, "authError", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(error.Reason, "invalidCredentials", StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static string? Normalize(string? value, int maximumLength)
        {
            string? normalized = value?.Trim();
            return string.IsNullOrEmpty(normalized) || normalized.Length > maximumLength
                ? null
                : normalized;
        }
    }
}
