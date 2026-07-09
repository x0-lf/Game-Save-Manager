using GameSaves.Core.Profiles;

namespace GameSaves.App.Models
{
    public sealed class SteamProfileRowViewModel
    {
        public SteamProfileRowViewModel(SteamProfile profile)
        {
            Profile = profile;
        }

        public SteamProfile Profile { get; }

        public string AccountId => Profile.AccountId;

        public string DisplayName => string.IsNullOrWhiteSpace(Profile.DisplayName)
            ? $"Steam profile {Profile.AccountId}"
            : Profile.DisplayName!;

        public string SteamId64 => string.IsNullOrWhiteSpace(Profile.SteamId64)
            ? "Unknown"
            : Profile.SteamId64!;

        public string UserDataPath => Profile.UserDataPath;

        public int AppFolderCount => Profile.AppFolderCount;

        public bool IsCurrentUser => Profile.IsCurrentUser;

        public string CurrentUserText => IsCurrentUser
            ? "Current"
            : "Unknown";

        public string ComboDisplay =>
            $"{DisplayName} — {AppFolderCount} app folders";

        public override string ToString()
        {
            return ComboDisplay;
        }
    }
}