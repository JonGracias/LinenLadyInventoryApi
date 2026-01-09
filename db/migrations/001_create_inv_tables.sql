/* =========================================================
   inv schema
   ========================================================= */
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'inv')
BEGIN
    EXEC(N'CREATE SCHEMA inv');
END
GO

/* =========================================================
   inv.Inventory: add PublicId (NO DEFAULT)
   ========================================================= */

/* 1) Add column as NULLable first (so existing rows don't break) */
IF COL_LENGTH(N'inv.Inventory', N'PublicId') IS NULL
BEGIN
    ALTER TABLE inv.Inventory
    ADD PublicId UNIQUEIDENTIFIER NULL;
END
GO

/* 2) Backfill existing rows (one-time). This does not create a DEFAULT constraint. */
IF COL_LENGTH(N'inv.Inventory', N'PublicId') IS NOT NULL
BEGIN
    UPDATE inv.Inventory
    SET PublicId = NEWID()
    WHERE PublicId IS NULL;
END
GO

/* 3) Enforce NOT NULL */
IF COL_LENGTH(N'inv.Inventory', N'PublicId') IS NOT NULL
AND EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'inv.Inventory', N'U')
      AND name = N'PublicId'
      AND is_nullable = 1
)
BEGIN
    ALTER TABLE inv.Inventory
    ALTER COLUMN PublicId UNIQUEIDENTIFIER NOT NULL;
END
GO

/* 4) Unique constraint on PublicId */
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UQ_Inventory_PublicId'
      AND object_id = OBJECT_ID(N'inv.Inventory', N'U')
)
BEGIN
    ALTER TABLE inv.Inventory
    ADD CONSTRAINT UQ_Inventory_PublicId UNIQUE (PublicId);
END
GO

/* =========================================================
   inv.Inventory: defaults for everything except PublicId
   (Sku still must be provided because it's UNIQUE)
   ========================================================= */

/* CreatedAt / UpdatedAt */
IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_Inventory_CreatedAt'
      AND parent_object_id = OBJECT_ID(N'inv.Inventory', N'U')
)
BEGIN
    ALTER TABLE inv.Inventory
    ADD CONSTRAINT DF_Inventory_CreatedAt
    DEFAULT (SYSUTCDATETIME()) FOR CreatedAt;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_Inventory_UpdatedAt'
      AND parent_object_id = OBJECT_ID(N'inv.Inventory', N'U')
)
BEGIN
    ALTER TABLE inv.Inventory
    ADD CONSTRAINT DF_Inventory_UpdatedAt
    DEFAULT (SYSUTCDATETIME()) FOR UpdatedAt;
END
GO

/* QuantityOnHand */
IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_Inventory_QuantityOnHand'
      AND parent_object_id = OBJECT_ID(N'inv.Inventory', N'U')
)
BEGIN
    ALTER TABLE inv.Inventory
    ADD CONSTRAINT DF_Inventory_QuantityOnHand
    DEFAULT (1) FOR QuantityOnHand;
END
GO

/* IsActive / IsDeleted / IsDraft */
IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_Inventory_IsActive'
      AND parent_object_id = OBJECT_ID(N'inv.Inventory', N'U')
)
BEGIN
    ALTER TABLE inv.Inventory
    ADD CONSTRAINT DF_Inventory_IsActive
    DEFAULT (0) FOR IsActive;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_Inventory_IsDeleted'
      AND parent_object_id = OBJECT_ID(N'inv.Inventory', N'U')
)
BEGIN
    ALTER TABLE inv.Inventory
    ADD CONSTRAINT DF_Inventory_IsDeleted
    DEFAULT (0) FOR IsDeleted;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_Inventory_IsDraft'
      AND parent_object_id = OBJECT_ID(N'inv.Inventory', N'U')
)
BEGIN
    ALTER TABLE inv.Inventory
    ADD CONSTRAINT DF_Inventory_IsDraft
    DEFAULT (1) FOR IsDraft;
END
GO

/* UnitPriceCents */
IF COL_LENGTH(N'inv.Inventory', N'UnitPriceCents') IS NOT NULL
AND NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_Inventory_UnitPriceCents'
      AND parent_object_id = OBJECT_ID(N'inv.Inventory', N'U')
)
BEGIN
    ALTER TABLE inv.Inventory
    ADD CONSTRAINT DF_Inventory_UnitPriceCents
    DEFAULT (0) FOR UnitPriceCents;
END
GO

IF COL_LENGTH(N'inv.Inventory', N'UnitPriceCents') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.check_constraints
    WHERE name = N'CK_Inventory_UnitPriceCents_NonNegative'
      AND parent_object_id = OBJECT_ID(N'inv.Inventory', N'U')
)
BEGIN
    ALTER TABLE inv.Inventory
    ADD CONSTRAINT CK_Inventory_UnitPriceCents_NonNegative
    CHECK (UnitPriceCents >= 0);
END
GO

/* =========================================================
   inv.InventoryImage: defaults for everything except ImagePath
   ========================================================= */

/* CreatedAt */
IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_InventoryImage_CreatedAt'
      AND parent_object_id = OBJECT_ID(N'inv.InventoryImage', N'U')
)
BEGIN
    ALTER TABLE inv.InventoryImage
    ADD CONSTRAINT DF_InventoryImage_CreatedAt
    DEFAULT (SYSUTCDATETIME()) FOR CreatedAt;
END
GO

/* IsPrimary */
IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_InventoryImage_IsPrimary'
      AND parent_object_id = OBJECT_ID(N'inv.InventoryImage', N'U')
)
BEGIN
    ALTER TABLE inv.InventoryImage
    ADD CONSTRAINT DF_InventoryImage_IsPrimary
    DEFAULT (0) FOR IsPrimary;
END
GO

/* SortOrder (default exists, but app should still set explicit values) */
IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_InventoryImage_SortOrder'
      AND parent_object_id = OBJECT_ID(N'inv.InventoryImage', N'U')
)
BEGIN
    ALTER TABLE inv.InventoryImage
    ADD CONSTRAINT DF_InventoryImage_SortOrder
    DEFAULT (1) FOR SortOrder;
END
GO
