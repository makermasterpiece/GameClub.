namespace GameClub.Domain.Billing;

public static class Money
{
    public const decimal Maximum = 9999999999999999.99m;

    public static decimal Round(decimal amount)
    {
        var result = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (result is > Maximum or < -Maximum)
            throw new ArgumentOutOfRangeException(nameof(amount), "Money must fit decimal(18,2).");
        return result;
    }

    public static decimal RequireExactScale(decimal amount)
    {
        if (Round(amount) != amount)
            throw new ArgumentOutOfRangeException(nameof(amount), "Money must have at most two decimal places.");
        return amount;
    }

    public static decimal Charge(decimal hourly, long elapsedTicks)
    {
        RequireExactScale(hourly);
        if (hourly < 0) throw new ArgumentOutOfRangeException(nameof(hourly));
        if (elapsedTicks < 0) throw new ArgumentOutOfRangeException(nameof(elapsedTicks));
        try
        {
            // Divide only after multiplication: a repeating fractional hour must not move an exact half-cent below its midpoint.
            return Round(hourly * elapsedTicks / (decimal)TimeSpan.TicksPerHour);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedTicks), "The calculated charge exceeds the supported monetary range.");
        }
    }

    public static int MaxAffordableSeconds(decimal amount, decimal hourly)
    {
        RequireExactScale(amount);
        RequireExactScale(hourly);
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (hourly <= 0) throw new ArgumentOutOfRangeException(nameof(hourly));
        // Multiply first: dividing a repeating decimal before multiplying can lose a whole second.
        var seconds = decimal.Floor(amount * 3600m / hourly);
        return seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
    }
}
