using GameClub.Domain.Pos;
using Xunit;

namespace GameClub.Server.Tests.Pos;

public sealed class PosDomainTests
{
    [Fact]
    public void Product_RejectsInvalidPriceAndStockWithoutNegativeInventory()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Product(Guid.NewGuid(), null, "Drink", 1.001m, 1));
        var product = new Product(Guid.NewGuid(), null, "Drink", 10m, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => product.AdjustStock(-3));
        Assert.Equal(2, product.StockQuantity);
        Assert.Throws<ArgumentOutOfRangeException>(() => product.AdjustStock(int.MaxValue));
        Assert.Equal(2, product.StockQuantity);
    }

    [Fact]
    public void Payment_RequiresCashOrCardAndRefundDirection()
    {
        var now = DateTime.UtcNow;
        Assert.Throws<ArgumentException>(() => new Payment(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), PaymentMethod.Wallet, 1m, now));
        Assert.Throws<ArgumentException>(() => new Payment(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), PaymentMethod.Cash, -1m, now));
        Assert.Throws<ArgumentException>(() => new Payment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PaymentMethod.Card, 1m, now));
    }

    [Fact]
    public void SaleItem_SnapshotsExactLineTotalAndHasNoPublicSetters()
    {
        var item = new SaleItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Drink", 1.25m, 3);
        Assert.Equal(3.75m, item.LineTotal);
        Assert.All(typeof(SaleItem).GetProperties(), p => Assert.False(p.SetMethod?.IsPublic ?? false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SaleItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Drink", 1m, 0));
    }
}
