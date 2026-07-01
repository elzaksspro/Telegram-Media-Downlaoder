namespace TelegramMedia.Core.Enums;

public enum AuthState
{
    NotAuthenticated,
    WaitingForPhoneNumber,
    WaitingForCode,
    WaitingForPassword,
    Authenticated
}
