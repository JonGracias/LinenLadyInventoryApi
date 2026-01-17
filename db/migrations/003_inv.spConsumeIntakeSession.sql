/* =========================================================
   Stored Procedure: Consume an intake session into a draft item

   Behavior:
   - Idempotent: if already consumed, returns existing InventoryId
   - Atomic: creates Inventory + InventoryImage rows in one transaction
   - Safe for retries: uses UPDLOCK/HOLDLOCK on the session row

   Assumes tables exist:
   - inv.IntakeSession (CreatedInventoryId, Status, ExpiresAt, UpdatedAt)
   - inv.IntakePhoto   (IntakeSessionId, BlobPath, SortOrder, IsPrimary)
   - inv.Inventory
   - inv.InventoryImage
   ========================================================= */

IF OBJECT_ID('inv.spConsumeIntakeSession', 'P') IS NOT NULL
  DROP PROCEDURE inv.spConsumeIntakeSession;
GO

CREATE PROCEDURE inv.spConsumeIntakeSession
  @IntakeSessionId UNIQUEIDENTIFIER,
  @CreatedBy       NVARCHAR(256) = NULL,  -- optional: stamp CreatedBy if you want (no-op if you don't use it)
  @DefaultName     NVARCHAR(255) = N'Draft Item',
  @DefaultQty      INT = 1,
  @DefaultPriceCents INT = 0
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @InventoryId INT;
  DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

  BEGIN TRAN;

  /* 1) Lock the session row and read its state */
  DECLARE @ExistingInventoryId INT;
  DECLARE @Status NVARCHAR(20);
  DECLARE @ExpiresAt DATETIME2(7);

  SELECT
    @ExistingInventoryId = s.CreatedInventoryId,
    @Status = s.Status,
    @ExpiresAt = s.ExpiresAt
  FROM inv.IntakeSession s WITH (UPDLOCK, HOLDLOCK)
  WHERE s.IntakeSessionId = @IntakeSessionId;

  IF @@ROWCOUNT = 0
  BEGIN
    ROLLBACK;
    THROW 50010, 'Intake session not found.', 1;
  END

  /* 2) If already consumed, return existing InventoryId (idempotent) */
  IF @ExistingInventoryId IS NOT NULL
  BEGIN
    COMMIT;
    SELECT @ExistingInventoryId AS InventoryId;
    RETURN;
  END

  /* 3) Validate session is open and not expired */
  IF (@Status <> 'Open') OR (@ExpiresAt <= @Now)
  BEGIN
    ROLLBACK;
    THROW 50011, 'Intake session is not open or is expired.', 1;
  END

  /* 4) Validate there is at least 1 photo */
  IF NOT EXISTS (
    SELECT 1
    FROM inv.IntakePhoto p
    WHERE p.IntakeSessionId = @IntakeSessionId
  )
  BEGIN
    ROLLBACK;
    THROW 50012, 'No photos found for intake session.', 1;
  END

  /* 5) Create draft Inventory item */
  INSERT INTO inv.Inventory
    (Sku, Name, Description, QuantityOnHand, IsActive, CreatedAt, UpdatedAt, IsDeleted, IsDraft, UnitPriceCents, PublicId)
  VALUES
    (CONCAT('SKU-', REPLACE(CONVERT(NVARCHAR(36), NEWID()), '-', '')),
     @DefaultName,
     NULL,
     @DefaultQty,
     0,
     @Now,
     @Now,
     0,
     1,
     @DefaultPriceCents,
     NEWID()
    );

  SET @InventoryId = SCOPE_IDENTITY();

  /* 6) Copy photos to InventoryImage */
  INSERT INTO inv.InventoryImage (InventoryId, ImagePath, IsPrimary, SortOrder, CreatedAt)
  SELECT
    @InventoryId,
    p.BlobPath,
    p.IsPrimary,
    p.SortOrder,
    @Now
  FROM inv.IntakePhoto p
  WHERE p.IntakeSessionId = @IntakeSessionId
  ORDER BY p.SortOrder;

  /* Ensure primary image exists; if none, set lowest SortOrder as primary */
  IF NOT EXISTS (
    SELECT 1 FROM inv.InventoryImage WHERE InventoryId = @InventoryId AND IsPrimary = 1
  )
  BEGIN
    ;WITH cte AS (
      SELECT TOP 1 ImageId
      FROM inv.InventoryImage
      WHERE InventoryId = @InventoryId
      ORDER BY SortOrder ASC, ImageId ASC
    )
    UPDATE ii
      SET IsPrimary = 1
    FROM inv.InventoryImage ii
    JOIN cte ON cte.ImageId = ii.ImageId;
  END

  /* 7) Mark session consumed + link created item */
  UPDATE inv.IntakeSession
    SET Status = 'Consumed',
        CreatedInventoryId = @InventoryId,
        UpdatedAt = @Now
  WHERE IntakeSessionId = @IntakeSessionId;

  COMMIT;

  SELECT @InventoryId AS InventoryId;
END
GO
