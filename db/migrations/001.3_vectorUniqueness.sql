/* ===== inv.InventoryVector hardening ===== */

-- Fast lookup by InventoryId
IF NOT EXISTS (
  SELECT 1 FROM sys.indexes
  WHERE name = 'IX_InventoryVector_InventoryId' AND object_id = OBJECT_ID('inv.InventoryVector')
)
BEGIN
  CREATE INDEX IX_InventoryVector_InventoryId
    ON inv.InventoryVector(InventoryId)
    INCLUDE (VectorId, VectorPurpose, Model, Dimensions, ContentHash, CreatedAt, UpdatedAt);
END

-- Most common fetch pattern: "give me the latest vector for this purpose"
IF NOT EXISTS (
  SELECT 1 FROM sys.indexes
  WHERE name = 'IX_InventoryVector_Purpose' AND object_id = OBJECT_ID('inv.InventoryVector')
)
BEGIN
  CREATE INDEX IX_InventoryVector_Purpose
    ON inv.InventoryVector(InventoryId, VectorPurpose, UpdatedAt DESC)
    INCLUDE (Model, Dimensions, ContentHash);
END

-- Prevent exact duplicates (same item + purpose + model + dimensions + content)
IF NOT EXISTS (
  SELECT 1 FROM sys.indexes
  WHERE name = 'UX_InventoryVector_Dedup' AND object_id = OBJECT_ID('inv.InventoryVector')
)
BEGIN
  CREATE UNIQUE INDEX UX_InventoryVector_Dedup
    ON inv.InventoryVector(InventoryId, VectorPurpose, Model, Dimensions, ContentHash);
END
