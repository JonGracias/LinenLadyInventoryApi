IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Inventory_NotDeleted_InvIdDesc' AND object_id = OBJECT_ID('inv.Inventory'))
BEGIN
  CREATE INDEX IX_Inventory_NotDeleted_InvIdDesc
  ON inv.Inventory (IsDeleted, InventoryId DESC)
  INCLUDE (PublicId, Sku, Name, Description, QuantityOnHand, UnitPriceCents, IsActive, IsDraft, CreatedAt, UpdatedAt);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Inventory_Drafts_InvIdDesc' AND object_id = OBJECT_ID('inv.Inventory'))
BEGIN
  CREATE INDEX IX_Inventory_Drafts_InvIdDesc
  ON inv.Inventory (InventoryId DESC)
  INCLUDE (PublicId, Sku, Name, Description, QuantityOnHand, UnitPriceCents, IsActive, IsDraft, CreatedAt, UpdatedAt)
  WHERE IsDeleted = 0 AND IsDraft = 1;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Inventory_Active_InvIdDesc' AND object_id = OBJECT_ID('inv.Inventory'))
BEGIN
  CREATE INDEX IX_Inventory_Active_InvIdDesc
  ON inv.Inventory (InventoryId DESC)
  INCLUDE (PublicId, Sku, Name, Description, QuantityOnHand, UnitPriceCents, IsActive, IsDraft, CreatedAt, UpdatedAt)
  WHERE IsDeleted = 0 AND IsDraft = 0 AND IsActive = 1;
END;

-- Only if you DON'T already have it:
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_InventoryImage_InventoryId_SortOrder' AND object_id = OBJECT_ID('inv.InventoryImage'))
BEGIN
  CREATE INDEX IX_InventoryImage_InventoryId_SortOrder
  ON inv.InventoryImage (InventoryId, SortOrder)
  INCLUDE (ImageId, ImagePath, IsPrimary);
END;
