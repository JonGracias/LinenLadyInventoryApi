-- Migration: Add IsFeatured and Category to inv.Inventory
-- Run once against your database

ALTER TABLE inv.Inventory
    ADD IsFeatured BIT NOT NULL DEFAULT 0;

ALTER TABLE inv.Inventory
    ADD Category NVARCHAR(100) NULL;

-- Optional: index for fast featured queries on the storefront
CREATE NONCLUSTERED INDEX IX_Inventory_IsFeatured
    ON inv.Inventory (IsFeatured, IsActive, IsDraft, IsDeleted)
    INCLUDE (InventoryId, Sku, Name, UnitPriceCents);

-- Optional: index for category filtering
CREATE NONCLUSTERED INDEX IX_Inventory_Category
    ON inv.Inventory (Category, IsActive, IsDraft, IsDeleted)
    INCLUDE (InventoryId, Sku, Name, UnitPriceCents);