DECLARE @maxId INT = (SELECT ISNULL(MAX(InventoryId), 0) FROM inv.Inventory);
DBCC CHECKIDENT ('inv.Inventory', RESEED, @maxId);