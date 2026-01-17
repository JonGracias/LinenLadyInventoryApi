/* ===== inv.Inventory hardening ===== */

-- 1) PublicId should be unique (it’s your stable external identifier)
IF NOT EXISTS (
  SELECT 1 FROM sys.indexes
  WHERE name = 'UX_Inventory_PublicId' AND object_id = OBJECT_ID('inv.Inventory')
)
BEGIN
  CREATE UNIQUE INDEX UX_Inventory_PublicId ON inv.Inventory(PublicId);
END

-- 2) SKU should be unique (if you intend SKU as unique)
IF NOT EXISTS (
  SELECT 1 FROM sys.indexes
  WHERE name = 'UX_Inventory_Sku' AND object_id = OBJECT_ID('inv.Inventory')
)
BEGIN
  CREATE UNIQUE INDEX UX_Inventory_Sku ON inv.Inventory(Sku);
END

-- 3) Defaults (safe even if you already set them elsewhere; adjust if needed)
-- CreatedAt default
IF NOT EXISTS (
  SELECT 1 FROM sys.default_constraints dc
  JOIN sys.columns c ON c.default_object_id = dc.object_id
  WHERE dc.parent_object_id = OBJECT_ID('inv.Inventory') AND c.name = 'CreatedAt'
)
BEGIN
  ALTER TABLE inv.Inventory ADD CONSTRAINT DF_Inventory_CreatedAt DEFAULT (SYSUTCDATETIME()) FOR CreatedAt;
END

-- UpdatedAt default
IF NOT EXISTS (
  SELECT 1 FROM sys.default_constraints dc
  JOIN sys.columns c ON c.default_object_id = dc.object_id
  WHERE dc.parent_object_id = OBJECT_ID('inv.Inventory') AND c.name = 'UpdatedAt'
)
BEGIN
  ALTER TABLE inv.Inventory ADD CONSTRAINT DF_Inventory_UpdatedAt DEFAULT (SYSUTCDATETIME()) FOR UpdatedAt;
END

-- IsDraft/IsActive/IsDeleted defaults (tune to your workflow)
IF NOT EXISTS (
  SELECT 1 FROM sys.default_constraints dc
  JOIN sys.columns c ON c.default_object_id = dc.object_id
  WHERE dc.parent_object_id = OBJECT_ID('inv.Inventory') AND c.name = 'IsDraft'
)
BEGIN
  ALTER TABLE inv.Inventory ADD CONSTRAINT DF_Inventory_IsDraft DEFAULT (1) FOR IsDraft;
END

IF NOT EXISTS (
  SELECT 1 FROM sys.default_constraints dc
  JOIN sys.columns c ON c.default_object_id = dc.object_id
  WHERE dc.parent_object_id = OBJECT_ID('inv.Inventory') AND c.name = 'IsActive'
)
BEGIN
  ALTER TABLE inv.Inventory ADD CONSTRAINT DF_Inventory_IsActive DEFAULT (0) FOR IsActive;
END

IF NOT EXISTS (
  SELECT 1 FROM sys.default_constraints dc
  JOIN sys.columns c ON c.default_object_id = dc.object_id
  WHERE dc.parent_object_id = OBJECT_ID('inv.Inventory') AND c.name = 'IsDeleted'
)
BEGIN
  ALTER TABLE inv.Inventory ADD CONSTRAINT DF_Inventory_IsDeleted DEFAULT (0) FOR IsDeleted;
END

-- 4) Query indexes (draft lists, published lists, sort by UpdatedAt)
IF NOT EXISTS (
  SELECT 1 FROM sys.indexes
  WHERE name = 'IX_Inventory_Drafts' AND object_id = OBJECT_ID('inv.Inventory')
)
BEGIN
  CREATE INDEX IX_Inventory_Drafts
    ON inv.Inventory(IsDraft, IsDeleted, UpdatedAt DESC)
    INCLUDE (InventoryId, PublicId, Name, QuantityOnHand, UnitPriceCents, Sku, IsActive, CreatedAt);
END

IF NOT EXISTS (
  SELECT 1 FROM sys.indexes
  WHERE name = 'IX_Inventory_Published' AND object_id = OBJECT_ID('inv.Inventory')
)
BEGIN
  CREATE INDEX IX_Inventory_Published
    ON inv.Inventory(IsActive, IsDeleted, IsDraft, UpdatedAt DESC)
    INCLUDE (InventoryId, PublicId, Name, QuantityOnHand, UnitPriceCents, Sku, CreatedAt);
END
