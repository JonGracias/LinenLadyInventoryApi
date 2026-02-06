CREATE UNIQUE INDEX UX_InventoryImage_InventoryId_Primary
ON inv.InventoryImage (InventoryId)
WHERE IsPrimary = 1;

-- helps ORDER BY InventoryId DESC with IsDeleted filter
CREATE INDEX IX_Inventory_IsDeleted_InventoryId
ON inv.Inventory(IsDeleted, InventoryId DESC);

-- All (not deleted)
CREATE INDEX IX_Inventory_NotDeleted
ON inv.Inventory(InventoryId)
WHERE IsDeleted = 0;

-- Drafts
CREATE INDEX IX_Inventory_Drafts
ON inv.Inventory(InventoryId)
WHERE IsDeleted = 0 AND IsDraft = 1;

-- Published
CREATE INDEX IX_Inventory_Published
ON inv.Inventory(InventoryId)
WHERE IsDeleted = 0 AND IsDraft = 0 AND IsActive = 1;


