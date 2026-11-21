namespace RentalServer.Core.TabletAdmin;

public sealed record TabletAdminConfig(
    string Algorithm,
    string PasswordSalt,
    string PasswordHash);
