namespace GameClub.Server.Security;

public interface IStationTokenService
{
    string Create(Guid stationId);

    bool IsValid(string? token, Guid stationId);
}
