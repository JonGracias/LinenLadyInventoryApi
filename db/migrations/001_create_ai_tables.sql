/* =========================================================
   inv schema
   ========================================================= */
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'inv')
BEGIN
    EXEC(N'CREATE SCHEMA inv');
END
GO

/* =========================================================
   inv.InventoryVector (create if missing)
   Stores embeddings for inventory items
   ========================================================= */
IF OBJECT_ID(N'inv.InventoryVector', N'U') IS NULL
BEGIN
    CREATE TABLE inv.InventoryVector
    (
        VectorId        INT IDENTITY(1,1) NOT NULL,
        InventoryId     INT              NOT NULL,

        -- e.g. 'item_text' or 'item_text_v1'
        VectorPurpose   NVARCHAR(50)     NOT NULL,

        -- e.g. 'text-embedding-3-large' (or your deployment/model label)
        Model           NVARCHAR(100)    NOT NULL,

        Dimensions      INT              NOT NULL,

        -- SHA-256 of the exact text embedded (for idempotency / refresh detection)
        ContentHash     BINARY(32)       NOT NULL,

        -- JSON array of floats: [0.0123, -0.98, ...]
        VectorJson      NVARCHAR(MAX)    NOT NULL,

        CreatedAt       DATETIME2(7)     NOT NULL
            CONSTRAINT DF_InventoryVector_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt       DATETIME2(7)     NOT NULL
            CONSTRAINT DF_InventoryVector_UpdatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_InventoryVector PRIMARY KEY CLUSTERED (VectorId),
        CONSTRAINT FK_InventoryVector_Inventory
            FOREIGN KEY (InventoryId) REFERENCES inv.Inventory(InventoryId),

        CONSTRAINT CK_InventoryVector_Dimensions_Positive CHECK (Dimensions > 0)
    );

    -- Enforce one vector per (item, purpose, model)
    CREATE UNIQUE INDEX UX_InventoryVector_ItemPurposeModel
        ON inv.InventoryVector (InventoryId, VectorPurpose, Model);

    -- Helpful for “is it stale?” checks
    CREATE INDEX IX_InventoryVector_ItemModelHash
        ON inv.InventoryVector (InventoryId, Model, ContentHash);
END
GO

/* If table exists but defaults missing, add them */
IF OBJECT_ID(N'inv.InventoryVector', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.default_constraints
        WHERE name = N'DF_InventoryVector_CreatedAt'
          AND parent_object_id = OBJECT_ID(N'inv.InventoryVector', N'U')
    )
    BEGIN
        ALTER TABLE inv.InventoryVector
        ADD CONSTRAINT DF_InventoryVector_CreatedAt
        DEFAULT (SYSUTCDATETIME()) FOR CreatedAt;
    END

    IF NOT EXISTS (
        SELECT 1 FROM sys.default_constraints
        WHERE name = N'DF_InventoryVector_UpdatedAt'
          AND parent_object_id = OBJECT_ID(N'inv.InventoryVector', N'U')
    )
    BEGIN
        ALTER TABLE inv.InventoryVector
        ADD CONSTRAINT DF_InventoryVector_UpdatedAt
        DEFAULT (SYSUTCDATETIME()) FOR UpdatedAt;
    END
END
GO
