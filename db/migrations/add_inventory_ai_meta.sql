-- ============================================================
-- Migration: Customer & Reservation Schema
-- Schema:    cust
-- Created:   2025
-- Run order: Execute this entire script once against your
--            LinenLady SQL database.
-- ============================================================

-- ── Create schema ────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'cust')
BEGIN
    EXEC('CREATE SCHEMA cust');
END
GO

-- ============================================================
-- cust.Customer
-- Linked to Clerk via ClerkUserId
-- ============================================================
CREATE TABLE cust.Customer (
    CustomerId      INT             NOT NULL IDENTITY(1,1),
    ClerkUserId     NVARCHAR(128)   NOT NULL,           -- Clerk's user_xxxx id
    Email           NVARCHAR(320)   NOT NULL,
    FirstName       NVARCHAR(100)   NULL,
    LastName        NVARCHAR(100)   NULL,
    Phone           NVARCHAR(30)    NULL,
    IsEmailVerified BIT             NOT NULL DEFAULT 0,
    IsActive        BIT             NOT NULL DEFAULT 1,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_Customer PRIMARY KEY (CustomerId),
    CONSTRAINT UQ_Customer_ClerkUserId UNIQUE (ClerkUserId),
    CONSTRAINT UQ_Customer_Email       UNIQUE (Email)
);
GO

-- ============================================================
-- cust.CustomerAddress
-- ============================================================
CREATE TABLE cust.CustomerAddress (
    AddressId   INT             NOT NULL IDENTITY(1,1),
    CustomerId  INT             NOT NULL,
    Label       NVARCHAR(50)    NOT NULL DEFAULT 'Home',  -- Home / Work / Other
    Street1     NVARCHAR(200)   NOT NULL,
    Street2     NVARCHAR(200)   NULL,
    City        NVARCHAR(100)   NOT NULL,
    State       NVARCHAR(50)    NOT NULL,
    Zip         NVARCHAR(20)    NOT NULL,
    Country     NVARCHAR(50)    NOT NULL DEFAULT 'US',
    IsDefault   BIT             NOT NULL DEFAULT 0,
    CreatedAt   DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt   DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_CustomerAddress  PRIMARY KEY (AddressId),
    CONSTRAINT FK_CustomerAddress_Customer
        FOREIGN KEY (CustomerId) REFERENCES cust.Customer(CustomerId)
        ON DELETE CASCADE
);
GO

-- ============================================================
-- cust.CustomerPreference
-- One row per customer per category they want alerts for.
-- Category values match the frontend Category type.
-- ============================================================
CREATE TABLE cust.CustomerPreference (
    PreferenceId    INT             NOT NULL IDENTITY(1,1),
    CustomerId      INT             NOT NULL,
    Category        NVARCHAR(50)    NOT NULL,   -- 'tablecloth' | 'napkin' | etc.
    NotifyOnNew     BIT             NOT NULL DEFAULT 1,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_CustomerPreference PRIMARY KEY (PreferenceId),
    CONSTRAINT UQ_CustomerPreference UNIQUE (CustomerId, Category),
    CONSTRAINT FK_CustomerPreference_Customer
        FOREIGN KEY (CustomerId) REFERENCES cust.Customer(CustomerId)
        ON DELETE CASCADE
);
GO

-- ============================================================
-- cust.Reservation
-- Status flow:
--   Pending → Confirmed → PaymentSent → Completed
--                       ↘ Expired
--             ↘ Cancelled
-- ============================================================
CREATE TABLE cust.Reservation (
    ReservationId       INT             NOT NULL IDENTITY(1,1),
    CustomerId          INT             NOT NULL,
    InventoryId         INT             NOT NULL,   -- FK to inv.Inventory
    Status              NVARCHAR(20)    NOT NULL DEFAULT 'Pending',
        -- Pending | Confirmed | PaymentSent | Completed | Expired | Cancelled
    ReservedAt          DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    ExpiresAt           DATETIME2       NOT NULL,   -- ReservedAt + 48h
    PaymentSentAt       DATETIME2       NULL,
    CompletedAt         DATETIME2       NULL,
    CancelledAt         DATETIME2       NULL,
    CustomerNotes       NVARCHAR(1000)  NULL,       -- buyer's message at reservation
    InternalNotes       NVARCHAR(1000)  NULL,       -- Noemi's notes
    SquarePaymentLinkId NVARCHAR(200)   NULL,
    SquarePaymentLinkUrl NVARCHAR(500)  NULL,
    SquareOrderId       NVARCHAR(200)   NULL,
    AmountCents         INT             NOT NULL,   -- snapshot of price at reservation
    CreatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAt           DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_Reservation PRIMARY KEY (ReservationId),
    CONSTRAINT FK_Reservation_Customer
        FOREIGN KEY (CustomerId) REFERENCES cust.Customer(CustomerId),
    CONSTRAINT CK_Reservation_Status CHECK (
        Status IN ('Pending','Confirmed','PaymentSent','Completed','Expired','Cancelled')
    )
);
GO

-- Index for fast lookup by inventory item (check if reserved)
CREATE INDEX IX_Reservation_InventoryId_Status
    ON cust.Reservation (InventoryId, Status);
GO

-- Index for customer history
CREATE INDEX IX_Reservation_CustomerId
    ON cust.Reservation (CustomerId);
GO

-- ============================================================
-- cust.Message
-- Customer <-> Noemi direct messages, threaded by reservation
-- or standalone.
-- ============================================================
CREATE TABLE cust.Message (
    MessageId       INT             NOT NULL IDENTITY(1,1),
    CustomerId      INT             NOT NULL,
    ReservationId   INT             NULL,           -- optional thread link
    Direction       NVARCHAR(10)    NOT NULL,       -- 'Inbound' | 'Outbound'
    Body            NVARCHAR(4000)  NOT NULL,
    IsRead          BIT             NOT NULL DEFAULT 0,
    SentAt          DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_Message PRIMARY KEY (MessageId),
    CONSTRAINT FK_Message_Customer
        FOREIGN KEY (CustomerId) REFERENCES cust.Customer(CustomerId),
    CONSTRAINT FK_Message_Reservation
        FOREIGN KEY (ReservationId) REFERENCES cust.Reservation(ReservationId),
    CONSTRAINT CK_Message_Direction CHECK (Direction IN ('Inbound','Outbound'))
);
GO

-- ============================================================
-- cust.Notification
-- Audit log of every email/notification sent
-- ============================================================
CREATE TABLE cust.Notification (
    NotificationId  INT             NOT NULL IDENTITY(1,1),
    CustomerId      INT             NOT NULL,
    ReservationId   INT             NULL,
    Type            NVARCHAR(50)    NOT NULL,
        -- ReservationConfirmed | PaymentLinkSent | PaymentReceived
        -- ReservationExpired   | ReservationCancelled | NewArrivalAlert
    Channel         NVARCHAR(20)    NOT NULL DEFAULT 'Email',
    SentAt          DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    Success         BIT             NOT NULL DEFAULT 1,
    ErrorMessage    NVARCHAR(500)   NULL,

    CONSTRAINT PK_Notification PRIMARY KEY (NotificationId),
    CONSTRAINT FK_Notification_Customer
        FOREIGN KEY (CustomerId) REFERENCES cust.Customer(CustomerId),
    CONSTRAINT FK_Notification_Reservation
        FOREIGN KEY (ReservationId) REFERENCES cust.Reservation(ReservationId)
);
GO

-- ============================================================
-- Helpful view: active reservations with item + customer info
-- ============================================================
CREATE VIEW cust.vw_ActiveReservations AS
SELECT
    r.ReservationId,
    r.Status,
    r.ReservedAt,
    r.ExpiresAt,
    r.AmountCents,
    r.SquarePaymentLinkUrl,
    r.CustomerNotes,
    r.InternalNotes,
    c.CustomerId,
    c.Email,
    c.FirstName,
    c.LastName,
    c.Phone,
    i.InventoryId,
    i.Sku,
    i.Name        AS ItemName,
    i.PublicId    AS ItemPublicId
FROM cust.Reservation r
JOIN cust.Customer    c ON c.CustomerId  = r.CustomerId
JOIN inv.Inventory    i ON i.InventoryId = r.InventoryId
WHERE r.Status IN ('Pending','Confirmed','PaymentSent');
GO

PRINT 'cust schema migration completed successfully.';
GO
