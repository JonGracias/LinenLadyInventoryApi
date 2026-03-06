-- /migrations/add_inventory_ai_meta.sql
-- Creates inv.InventoryAiMeta to store admin notes and AI-generated keywords
-- Run once against your database
-- @suppress-warnings
/* CREATE TABLE inv.InventoryAiMeta (
    MetaId                int             NOT NULL IDENTITY(1,1),
    InventoryId           int             NOT NULL,
    AdminNotes            nvarchar(max)   NULL,
    KeywordsJson          nvarchar(max)   NULL,
    KeywordsGeneratedAt   datetime2(7)    NULL,
    CreatedAt             datetime2(7)    NOT NULL CONSTRAINT DF_InventoryAiMeta_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt             datetime2(7)    NOT NULL CONSTRAINT DF_InventoryAiMeta_UpdatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_InventoryAiMeta        PRIMARY KEY CLUSTERED (MetaId),
    -- @suppress-warnings
    CONSTRAINT FK_InventoryAiMeta_Item   FOREIGN KEY (InventoryId) REFERENCES inv.Inventory(InventoryId),
    CONSTRAINT UQ_InventoryAiMeta_Item   UNIQUE (InventoryId)
);

CREATE INDEX IX_InventoryAiMeta_InventoryId ON inv.InventoryAiMeta (InventoryId); */

ALTER TABLE inv.InventoryAiMeta
    ADD SeoJson          nvarchar(max) NULL,
        SeoGeneratedAt   datetime2(7)  NULL;