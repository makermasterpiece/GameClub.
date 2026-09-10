using System.Globalization;
using GameClub.Domain.Billing;
using Xunit;

namespace GameClub.Server.Tests.Billing;

public sealed class WalletTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("1.005", "1.01")]
    [InlineData("-1.005", "-1.01")]
    [InlineData("1.004", "1.00")]
    [InlineData("0.005", "0.01")]
    public void MoneyRound_UsesTwoDecimalsAwayFromZero(string input, string expected) =>
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture),
            Money.Round(decimal.Parse(input, CultureInfo.InvariantCulture)));

    [Fact]
    public void Charge_UsesActiveTicksWithoutPrematureRounding()
    {
        Assert.Equal(0.01m, Money.Charge(1m, TimeSpan.FromSeconds(18).Ticks));
        Assert.Equal(0.02m, Money.Charge(1m, TimeSpan.FromSeconds(54).Ticks));
        Assert.Equal(45m, Money.Charge(60m, TimeSpan.FromMinutes(45).Ticks));
        Assert.Equal(0m, Money.Charge(0m, TimeSpan.FromHours(2).Ticks));
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Charge(1m, -1));
    }

    [Fact]
    public void Charge_PreservesExactHalfCentForRepeatingFractionalHours()
    {
        Assert.Equal(0.01m, Money.Charge(6m, TimeSpan.FromSeconds(3).Ticks));
        Assert.Equal(0.01m, Money.Charge(60m, TimeSpan.FromMilliseconds(300).Ticks));
        Assert.Equal(0m, Money.Charge(6m, TimeSpan.FromSeconds(3).Ticks - 1));
        Assert.Equal(0.01m, Money.Charge(6m, TimeSpan.FromSeconds(3).Ticks + 1));
    }

    [Fact]
    public void Charge_RejectsArithmeticOverflowAsAnInvalidDomainValue() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Charge(Money.Maximum, long.MaxValue));

    [Fact]
    public void AffordableSeconds_FloorsAndCapsWithoutOverflow()
    {
        Assert.Equal(12, Money.MaxAffordableSeconds(0.01m, 3m));
        Assert.Equal(0, Money.MaxAffordableSeconds(0m, 60m));
        Assert.Equal(1196, Money.MaxAffordableSeconds(1m, 3.01m));
        Assert.Equal(int.MaxValue, Money.MaxAffordableSeconds(Money.Maximum, 0.01m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.MaxAffordableSeconds(10m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.MaxAffordableSeconds(-1m, 1m));
    }

    [Fact]
    public void Wallet_NegativeBalanceRequiresExplicitPolicyAndFailedDebitDoesNotMutate()
    {
        var wallet = new Wallet(Guid.NewGuid(), Guid.NewGuid(), Now);
        wallet.Apply(10m);
        Assert.Throws<InvalidOperationException>(() => wallet.Apply(-10.01m));
        Assert.Equal(10m, wallet.Balance);
        wallet.Apply(-10.01m, allowNegative: true);
        Assert.Equal(-0.01m, wallet.Balance);
    }

    [Fact]
    public void Wallet_RejectsFractionalCentsAndStorageOverflow()
    {
        var wallet = new Wallet(Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.Throws<ArgumentOutOfRangeException>(() => wallet.Apply(1.001m));
        Assert.Equal(0m, wallet.Balance);
        wallet.Apply(Money.Maximum);
        Assert.Throws<ArgumentOutOfRangeException>(() => wallet.Apply(0.01m));
        Assert.Equal(Money.Maximum, wallet.Balance);
    }

    [Fact]
    public void DisablingCredit_PreservesDebtAndAllowsRepaymentsButRejectsFurtherDebt()
    {
        var wallet = new Wallet(Guid.NewGuid(), Guid.NewGuid(), Now);
        wallet.Apply(-20m, allowNegative: true);
        wallet.Apply(5m);
        Assert.Equal(-15m, wallet.Balance);
        Assert.Throws<InvalidOperationException>(() => wallet.Apply(-0.01m));
        Assert.Equal(-15m, wallet.Balance);
        wallet.Apply(15m);
        Assert.Equal(0m, wallet.Balance);
    }

    [Theory]
    [InlineData(WalletTransactionType.Deposit, -1)]
    [InlineData(WalletTransactionType.Deposit, 0)]
    [InlineData(WalletTransactionType.Refund, -1)]
    [InlineData(WalletTransactionType.Bonus, 0)]
    [InlineData(WalletTransactionType.GamingCharge, 1)]
    [InlineData(WalletTransactionType.ProductPurchase, 1)]
    [InlineData(WalletTransactionType.Adjustment, 0)]
    public void Ledger_RejectsAmountsWithInconsistentTransactionType(WalletTransactionType type, decimal amount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NewTransaction(type, amount));

    [Fact]
    public void Ledger_AcceptsZeroCostGamingAndHasNoPublicMutation()
    {
        var transaction = NewTransaction(WalletTransactionType.GamingCharge, 0m);
        Assert.Equal(0m, transaction.Amount);
        Assert.All(typeof(WalletTransaction).GetProperties(), property => Assert.False(property.SetMethod?.IsPublic ?? false));
    }

    [Fact]
    public void Reservation_ReleaseIsIdempotentAndTimestampCannotMoveBackwards()
    {
        var reservation = new WalletReservation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 25m, Now);
        Assert.Throws<ArgumentException>(() => reservation.Release(Now.AddSeconds(-1)));
        Assert.Null(reservation.ReleasedAtUtc);
        Assert.True(reservation.Release(Now.AddMinutes(1)));
        Assert.False(reservation.Release(Now.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(1), reservation.ReleasedAtUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WalletReservation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), -1m, Now));
    }

    private static WalletTransaction NewTransaction(WalletTransactionType type, decimal amount) =>
        new(Guid.NewGuid(), Guid.NewGuid(), type, amount, 100m, Now, "Test", null, Guid.NewGuid());
}
