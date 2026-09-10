using GameClub.Domain.Pos;
using GameClub.Domain.Employees;
using GameClub.Domain.Users;
using GameClub.Domain.Stations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GameClub.Infrastructure.Persistence.Configurations;

public sealed class ProductCategoryConfiguration : IEntityTypeConfiguration<ProductCategory>
{
    public void Configure(EntityTypeBuilder<ProductCategory> b) { b.ToTable("product_categories"); b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(100); }
}
public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("products", t => { t.HasCheckConstraint("CK_products_price", "\"Price\" > 0"); t.HasCheckConstraint("CK_products_stock", "\"StockQuantity\" BETWEEN 0 AND 1000000"); }); b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200); b.Property(x => x.Price).HasPrecision(18, 2);
        b.HasOne<ProductCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class EmployeeShiftConfiguration : IEntityTypeConfiguration<EmployeeShift>
{
    public void Configure(EntityTypeBuilder<EmployeeShift> b)
    {
        b.ToTable("employee_shifts"); b.HasKey(x => x.Id);
        b.Property(x => x.OpeningCash).HasPrecision(18, 2); b.Property(x => x.ClosingCash).HasPrecision(18, 2); b.Property(x => x.GamingSalesSnapshot).HasPrecision(18, 2); b.Property(x => x.CloseFingerprint).HasMaxLength(64);
        b.HasIndex(x => x.EmployeeId).IsUnique().HasFilter("\"ClosedAtUtc\" IS NULL"); b.HasIndex(x => new { x.EmployeeId, x.OpenedAtUtc }); b.HasIndex(x => x.CloseOperationId).IsUnique();
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> b)
    {
        b.ToTable("sales"); b.HasKey(x => x.Id); b.Property(x => x.CapturedTotal).HasPrecision(18, 2); b.Property(x => x.PaymentMethod).HasConversion<string>().HasMaxLength(20); b.Property(x => x.RequestFingerprint).HasMaxLength(64);
        b.HasOne<EmployeeShift>().WithMany().HasForeignKey(x => x.EmployeeShiftId).OnDelete(DeleteBehavior.Restrict); b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict); b.HasOne<Station>().WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.Restrict); b.HasIndex(x => new { x.EmployeeShiftId, x.CreatedAtUtc });
    }
}
public sealed class SaleItemConfiguration : IEntityTypeConfiguration<SaleItem>
{
    public void Configure(EntityTypeBuilder<SaleItem> b)
    {
        b.ToTable("sale_items"); b.HasKey(x => x.Id); b.Property(x => x.NameSnapshot).HasMaxLength(200); b.Property(x => x.UnitPrice).HasPrecision(18, 2); b.Property(x => x.LineTotal).HasPrecision(18, 2);
        b.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict); b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict); b.HasIndex(x => new { x.SaleId, x.ProductId }).IsUnique();
    }
}
public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments"); b.HasKey(x => x.Id); b.Property(x => x.Amount).HasPrecision(18, 2); b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
        b.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict); b.HasOne<SaleRefund>().WithMany().HasForeignKey(x => x.RefundId).OnDelete(DeleteBehavior.Restrict); b.HasOne<EmployeeShift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict); b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SaleId).IsUnique().HasFilter("\"RefundId\" IS NULL"); b.HasIndex(x => x.RefundId).IsUnique();
    }
}
public sealed class SaleRefundConfiguration : IEntityTypeConfiguration<SaleRefund>
{
    public void Configure(EntityTypeBuilder<SaleRefund> b)
    {
        b.ToTable("sale_refunds"); b.HasKey(x => x.Id); b.Property(x => x.Amount).HasPrecision(18, 2); b.Property(x => x.Reason).HasMaxLength(500); b.Property(x => x.RequestFingerprint).HasMaxLength(64);
        b.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict); b.HasIndex(x => x.SaleId).IsUnique(); b.HasOne<EmployeeShift>().WithMany().HasForeignKey(x => x.EmployeeShiftId).OnDelete(DeleteBehavior.Restrict); b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class PosOperationConfiguration : IEntityTypeConfiguration<PosOperation>
{
    public void Configure(EntityTypeBuilder<PosOperation> b) { b.ToTable("pos_operations"); b.HasKey(x => x.Id); b.Property(x => x.Kind).HasMaxLength(40); b.Property(x => x.Fingerprint).HasMaxLength(64); }
}
public sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.ToTable("stock_movements"); b.HasKey(x => x.Id); b.Property(x => x.Reason).HasMaxLength(500); b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict); b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict); b.HasIndex(x => new { x.ProductId, x.CreatedAtUtc });
    }
}
