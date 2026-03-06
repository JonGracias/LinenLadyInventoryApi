-- ============================================
-- Create inv.AdminUsers table
-- ============================================

CREATE TABLE inv.AdminUsers (
    Id            INT IDENTITY(1,1) PRIMARY KEY,
    Email         NVARCHAR(255)  NOT NULL UNIQUE,
    PasswordHash  NVARCHAR(512)  NOT NULL,        -- BCrypt hash (cost factor 12)
    DisplayName   NVARCHAR(100)  NOT NULL,
    Role          NVARCHAR(50)   NOT NULL DEFAULT 'admin',  -- admin | superadmin
    IsActive      BIT            NOT NULL DEFAULT 1,
    LastLoginAt   DATETIME2      NULL,
    CreatedAt     DATETIME2      NOT NULL DEFAULT GETUTCDATE(),
    UpdatedAt     DATETIME2      NOT NULL DEFAULT GETUTCDATE()
);

-- Fast email lookups on login
CREATE INDEX IX_AdminUsers_Email ON inv.AdminUsers (Email);

-- ============================================
-- Seed a default superadmin
-- Password: Admin1234!
-- IMPORTANT: Change this password on first login.
-- Generate a fresh BCrypt hash at https://bcrypt-generator.com (cost 12)
-- and UPDATE this row before going to production.
-- ============================================
INSERT INTO inv.AdminUsers (Email, PasswordHash, DisplayName, Role)
VALUES (
    'admin@example.com',
    '$2a$12$REPLACE_WITH_REAL_BCRYPT_HASH',
    'Super Admin',
    'superadmin'
);