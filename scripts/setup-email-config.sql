/*
    ContentManagement - initial administrator SMTP configuration.

    Run against the ContentManagement database AFTER applying migration
    202610090003_AddSystemSettings.

    Replace the placeholder values before execution. This script stores the SMTP
    password in plaintext, matching the current SystemSettings implementation.
    Restrict database access and do not commit real credentials to source control.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @SmtpHost NVARCHAR(255) = N'smtp.your-provider.example';
DECLARE @SmtpPort INT = 587;
DECLARE @SmtpUseSsl BIT = 1;
DECLARE @SmtpUsername NVARCHAR(255) = N'your-smtp-username';
DECLARE @SmtpPassword NVARCHAR(2048) = N'REPLACE_WITH_SMTP_PASSWORD';
DECLARE @SmtpFromEmail NVARCHAR(254) = N'noreply@your-domain.example';
DECLARE @SmtpFromName NVARCHAR(200) = N'ContentManagement';
DECLARE @TestRecipientEmail NVARCHAR(254) = N'admin@your-domain.example';

IF @SmtpHost = N'smtp.your-provider.example'
   OR @SmtpPassword = N'REPLACE_WITH_SMTP_PASSWORD'
   OR @SmtpFromEmail = N'noreply@your-domain.example'
   OR @TestRecipientEmail = N'admin@your-domain.example'
BEGIN
    THROW 50001, 'Replace the SMTP placeholder values before running this script.', 1;
END;

IF @SmtpPort NOT BETWEEN 1 AND 65535
BEGIN
    THROW 50002, 'SMTP port must be between 1 and 65535.', 1;
END;

BEGIN TRANSACTION;

IF EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE Id = 1)
BEGIN
    UPDATE dbo.SystemSettings
    SET SmtpHost = @SmtpHost,
        SmtpPort = @SmtpPort,
        SmtpUseSsl = @SmtpUseSsl,
        SmtpUsername = @SmtpUsername,
        SmtpPassword = @SmtpPassword,
        SmtpFromEmail = @SmtpFromEmail,
        SmtpFromName = @SmtpFromName,
        TestRecipientEmail = @TestRecipientEmail,
        UpdatedUtc = SYSUTCDATETIME()
    WHERE Id = 1;
END
ELSE
BEGIN
    INSERT INTO dbo.SystemSettings
    (
        Id, SmtpHost, SmtpPort, SmtpUseSsl, SmtpUsername, SmtpPassword,
        SmtpFromEmail, SmtpFromName, TestRecipientEmail,
        StaleFileAgeDays, CleanupIntervalHours, DeletionGracePeriodDays,
        UpdatedUtc, LastCleanupUtc
    )
    VALUES
    (
        1, @SmtpHost, @SmtpPort, @SmtpUseSsl, @SmtpUsername, @SmtpPassword,
        @SmtpFromEmail, @SmtpFromName, @TestRecipientEmail,
        90, 24, 7, SYSUTCDATETIME(), NULL
    );
END;

COMMIT TRANSACTION;

-- Verify non-secret fields only. Never select or print SmtpPassword.
SELECT Id, SmtpHost, SmtpPort, SmtpUseSsl, SmtpUsername,
       SmtpFromEmail, SmtpFromName, TestRecipientEmail, UpdatedUtc
FROM dbo.SystemSettings
WHERE Id = 1;
