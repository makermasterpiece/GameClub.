namespace GameClub.Application.Gaming;

public sealed record SessionExtensionQuote(Guid SessionId, int Minutes, string BillingMode,
    decimal Charge, decimal AdditionalReservation, DateTime? ExpectedEndAtUtc);
