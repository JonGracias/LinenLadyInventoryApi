-- Delete specific IDs
-- @suppress-warnings
EXEC inv.HardDeleteItems @Ids = '1052,1051';

-- Delete a single ID
-- EXEC inv.HardDeleteItems @Ids = '48';

-- Delete a range (use a subquery to generate the list first)
/* DECLARE @ids NVARCHAR(MAX);
SELECT @ids = STRING_AGG(CAST(InventoryId AS NVARCHAR), ',')
FROM inv.Inventory
WHERE InventoryId BETWEEN 1009 AND 2010;

EXEC inv.HardDeleteItems @Ids = @ids; */

-- After cleaning up, reseed the identity:
DECLARE @maxId INT = (SELECT ISNULL(MAX(InventoryId), 0) FROM inv.Inventory);
DBCC CHECKIDENT ('inv.Inventory', RESEED, @maxId);