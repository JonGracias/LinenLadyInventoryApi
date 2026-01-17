/* =========================================================
   LinenLady Intake Stored Procs
   - inv.spCreateIntakeSession
   - inv.spAddIntakePhoto
   - inv.spExpireOldIntakeSessions

   Assumes tables exist:
   - inv.IntakeSession(IntakeSessionId, PublicId, CreatedBy, Source, Status, ExpiresAt, CreatedAt, UpdatedAt, CreatedInventoryId)
   - inv.IntakePhoto(IntakePhotoId, IntakeSessionId, BlobPath, SortOrder, IsPrimary, ContentHash, CreatedAt)

   Notes:
   - Uses SYSUTCDATETIME()
   - Idempotency: spAddIntakePhoto is idempotent via (IntakeSessionId, BlobPath) unique index (from prior table script)
   - “Cleanup job”: you can schedule spExpireOldIntakeSessions via Azure SQL Job Agent (Managed Instance),
     Elastic Jobs, or call it from a timer-trigger function.
   ========================================================= */

SET NOCOUNT ON;
GO

/* =========================
   1) Create intake session
   ========================= */
IF OBJECT_ID('inv.spCreateIntakeSession', 'P') IS NOT NULL
  DROP PROCEDURE inv.spCreateIntakeSession;
GO

CREATE PROCEDURE inv.spCreateIntakeSession
  @CreatedBy NVARCHAR(256) = NULL,
  @Source    NVARCHAR(20)  = N'unknown', -- 'desktop'|'qr'|'mobile'
  @TtlMinutes INT = 60                   -- expiry window
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
  DECLARE @SessionId UNIQUEIDENTIFIER = NEWID();
  DECLARE @PublicId UNIQUEIDENTIFIER  = NEWID();

  INSERT INTO inv.IntakeSession
    (IntakeSessionId, PublicId, CreatedBy, Source, Status, ExpiresAt, CreatedAt, UpdatedAt, CreatedInventoryId)
  VALUES
    (@SessionId, @PublicId, @CreatedBy, @Source, 'Open', DATEADD(MINUTE, @TtlMinutes, @Now), @Now, @Now, NULL);

  SELECT
    @SessionId AS IntakeSessionId,
    @PublicId  AS PublicId,
    DATEADD(MINUTE, @TtlMinutes, @Now) AS ExpiresAt,
    'Open' AS Status;
END
GO

/* =========================
   2) Add intake photo
   ========================= */
IF OBJECT_ID('inv.spAddIntakePhoto', 'P') IS NOT NULL
  DROP PROCEDURE inv.spAddIntakePhoto;
GO

CREATE PROCEDURE inv.spAddIntakePhoto
  @IntakeSessionId UNIQUEIDENTIFIER,
  @BlobPath        NVARCHAR(1024),
  @SortOrder       INT,
  @IsPrimary       BIT = 0,
  @ContentHash     BINARY(32) = NULL      -- optional SHA-256
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

  BEGIN TRAN;

  -- Lock session to prevent adding photos to non-open / consumed sessions
  DECLARE @Status NVARCHAR(20);
  DECLARE @ExpiresAt DATETIME2(7);

  SELECT
    @Status = s.Status,
    @ExpiresAt = s.ExpiresAt
  FROM inv.IntakeSession s WITH (UPDLOCK, HOLDLOCK)
  WHERE s.IntakeSessionId = @IntakeSessionId;

  IF @@ROWCOUNT = 0
  BEGIN
    ROLLBACK;
    THROW 50020, 'Intake session not found.', 1;
  END

  IF (@Status <> 'Open') OR (@ExpiresAt <= @Now)
  BEGIN
    ROLLBACK;
    THROW 50021, 'Intake session is not open or is expired.', 1;
  END

  -- If caller wants primary, clear existing primary (enforces exactly one primary)
  IF @IsPrimary = 1
  BEGIN
    UPDATE inv.IntakePhoto
      SET IsPrimary = 0
    WHERE IntakeSessionId = @IntakeSessionId
      AND IsPrimary = 1;
  END

  -- Idempotent insert: if same BlobPath already exists for this session, just return it
  IF EXISTS (
    SELECT 1
    FROM inv.IntakePhoto
    WHERE IntakeSessionId = @IntakeSessionId
      AND BlobPath = @BlobPath
  )
  BEGIN
    -- Optionally: update SortOrder/IsPrimary if you want “upsert” behavior
    UPDATE inv.IntakePhoto
      SET SortOrder = @SortOrder,
          IsPrimary = CASE WHEN @IsPrimary = 1 THEN 1 ELSE IsPrimary END,
          ContentHash = COALESCE(@ContentHash, ContentHash)
    WHERE IntakeSessionId = @IntakeSessionId
      AND BlobPath = @BlobPath;

    COMMIT;

    SELECT TOP 1
      IntakePhotoId, IntakeSessionId, BlobPath, SortOrder, IsPrimary, ContentHash, CreatedAt
    FROM inv.IntakePhoto
    WHERE IntakeSessionId = @IntakeSessionId
      AND BlobPath = @BlobPath;

    RETURN;
  END

  -- Insert new photo
  INSERT INTO inv.IntakePhoto (IntakeSessionId, BlobPath, SortOrder, IsPrimary, ContentHash, CreatedAt)
  VALUES (@IntakeSessionId, @BlobPath, @SortOrder, @IsPrimary, @ContentHash, @Now);

  -- Touch session UpdatedAt
  UPDATE inv.IntakeSession
    SET UpdatedAt = @Now
  WHERE IntakeSessionId = @IntakeSessionId;

  COMMIT;

  SELECT TOP 1
    IntakePhotoId, IntakeSessionId, BlobPath, SortOrder, IsPrimary, ContentHash, CreatedAt
  FROM inv.IntakePhoto
  WHERE IntakeSessionId = @IntakeSessionId
    AND BlobPath = @BlobPath;
END
GO

/* =========================
   3) Expire old sessions
   ========================= */
IF OBJECT_ID('inv.spExpireOldIntakeSessions', 'P') IS NOT NULL
  DROP PROCEDURE inv.spExpireOldIntakeSessions;
GO

CREATE PROCEDURE inv.spExpireOldIntakeSessions
  @BatchSize INT = 500
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

  ;WITH cte AS (
    SELECT TOP (@BatchSize) s.IntakeSessionId
    FROM inv.IntakeSession s
    WHERE s.Status = 'Open'
      AND s.ExpiresAt <= @Now
    ORDER BY s.ExpiresAt ASC
  )
  UPDATE s
    SET Status = 'Expired',
        UpdatedAt = @Now
  FROM inv.IntakeSession s
  JOIN cte ON cte.IntakeSessionId = s.IntakeSessionId;

  SELECT @@ROWCOUNT AS ExpiredCount;
END
GO
