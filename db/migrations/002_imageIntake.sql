/* =========================================================
   LinenLady: Intake session tables (minimal, plugs into existing schema)
   - inv.IntakeSession: one “picture session” per intake flow (desktop or QR)
   - inv.IntakePhoto:   uploaded photos for that session (blob paths)
   - Optional link:     IntakeSession can store CreatedInventoryId once consumed
   ========================================================= */

-- Ensure schema exists
IF SCHEMA_ID('inv') IS NULL
BEGIN
  EXEC('CREATE SCHEMA inv');
END
GO

/* =========================
   1) inv.IntakeSession
   ========================= */
IF OBJECT_ID('inv.IntakeSession', 'U') IS NULL
BEGIN
  CREATE TABLE inv.IntakeSession (
    IntakeSessionId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_IntakeSession PRIMARY KEY,
    PublicId UNIQUEIDENTIFIER NOT NULL,  -- stable external id for URLs/QR (can equal IntakeSessionId, but kept explicit)
    CreatedBy NVARCHAR(256) NULL,         -- user id/email/subject from auth (optional)
    Source NVARCHAR(20) NOT NULL,         -- 'desktop' | 'qr' | 'mobile' | 'unknown'
    Status NVARCHAR(20) NOT NULL,         -- 'Open' | 'Consumed' | 'Expired' | 'Abandoned'
    ExpiresAt DATETIME2(7) NOT NULL,
    CreatedAt DATETIME2(7) NOT NULL,
    UpdatedAt DATETIME2(7) NOT NULL,

    -- When the session is “consumed” into an Inventory item, store it
    CreatedInventoryId INT NULL
  );

  -- Defaults
  ALTER TABLE inv.IntakeSession ADD CONSTRAINT DF_IntakeSession_PublicId  DEFAULT (NEWID())         FOR PublicId;
  ALTER TABLE inv.IntakeSession ADD CONSTRAINT DF_IntakeSession_Source    DEFAULT ('unknown')       FOR Source;
  ALTER TABLE inv.IntakeSession ADD CONSTRAINT DF_IntakeSession_Status    DEFAULT ('Open')          FOR Status;
  ALTER TABLE inv.IntakeSession ADD CONSTRAINT DF_IntakeSession_CreatedAt DEFAULT (SYSUTCDATETIME()) FOR CreatedAt;
  ALTER TABLE inv.IntakeSession ADD CONSTRAINT DF_IntakeSession_UpdatedAt DEFAULT (SYSUTCDATETIME()) FOR UpdatedAt;

  -- Default expiry: 1 hour (tune as needed)
  ALTER TABLE inv.IntakeSession ADD CONSTRAINT DF_IntakeSession_ExpiresAt DEFAULT (DATEADD(HOUR, 1, SYSUTCDATETIME())) FOR ExpiresAt;

  -- Uniqueness + lookup
  CREATE UNIQUE INDEX UX_IntakeSession_PublicId ON inv.IntakeSession(PublicId);
  CREATE INDEX IX_IntakeSession_Status_ExpiresAt ON inv.IntakeSession(Status, ExpiresAt);
  CREATE INDEX IX_IntakeSession_CreatedAt ON inv.IntakeSession(CreatedAt DESC);

  -- Optional FK to Inventory (soft link; keep nullable)
  ALTER TABLE inv.IntakeSession
    ADD CONSTRAINT FK_IntakeSession_Inventory
    FOREIGN KEY (CreatedInventoryId) REFERENCES inv.Inventory(InventoryId);
END
GO

/* =========================
   2) inv.IntakePhoto
   ========================= */
IF OBJECT_ID('inv.IntakePhoto', 'U') IS NULL
BEGIN
  CREATE TABLE inv.IntakePhoto (
    IntakePhotoId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_IntakePhoto PRIMARY KEY,
    IntakeSessionId UNIQUEIDENTIFIER NOT NULL,
    BlobPath NVARCHAR(1024) NOT NULL,     -- same idea as InventoryImage.ImagePath
    SortOrder INT NOT NULL,
    IsPrimary BIT NOT NULL,
    ContentHash BINARY(32) NULL,          -- optional dedupe/corruption check (SHA-256 recommended)
    CreatedAt DATETIME2(7) NOT NULL
  );

  ALTER TABLE inv.IntakePhoto ADD CONSTRAINT DF_IntakePhoto_IsPrimary DEFAULT (0) FOR IsPrimary;
  ALTER TABLE inv.IntakePhoto ADD CONSTRAINT DF_IntakePhoto_CreatedAt DEFAULT (SYSUTCDATETIME()) FOR CreatedAt;

  ALTER TABLE inv.IntakePhoto
    ADD CONSTRAINT FK_IntakePhoto_Session
    FOREIGN KEY (IntakeSessionId) REFERENCES inv.IntakeSession(IntakeSessionId)
    ON DELETE CASCADE;

  -- Access pattern: list photos for a session in order
  CREATE INDEX IX_IntakePhoto_Session ON inv.IntakePhoto(IntakeSessionId, SortOrder)
    INCLUDE (IntakePhotoId, BlobPath, IsPrimary, CreatedAt);

  -- Prevent duplicate sort orders per session
  CREATE UNIQUE INDEX UX_IntakePhoto_SortOrder ON inv.IntakePhoto(IntakeSessionId, SortOrder);

  -- Enforce at most one primary photo per session
  CREATE UNIQUE INDEX UX_IntakePhoto_OnePrimary ON inv.IntakePhoto(IntakeSessionId) WHERE IsPrimary = 1;

  -- Optional dedupe within a session by blob path
  CREATE UNIQUE INDEX UX_IntakePhoto_Session_BlobPath ON inv.IntakePhoto(IntakeSessionId, BlobPath);
END
GO

/* =========================
   3) (Optional) helper view: open sessions only
   ========================= */
IF OBJECT_ID('inv.vwOpenIntakeSessions', 'V') IS NULL
BEGIN
  EXEC('
    CREATE VIEW inv.vwOpenIntakeSessions AS
    SELECT *
    FROM inv.IntakeSession
    WHERE Status = ''Open'' AND ExpiresAt > SYSUTCDATETIME()
  ');
END
GO
