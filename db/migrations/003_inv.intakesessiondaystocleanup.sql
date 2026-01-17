/* =========================================================
   Hard cleanup: purge expired/consumed intake sessions older than N days
   - Deletes rows from inv.IntakeSession; inv.IntakePhoto cascades via FK (ON DELETE CASCADE)
   - Safe batching to avoid long locks
   ========================================================= */

IF OBJECT_ID('inv.spPurgeExpiredIntakeSessions', 'P') IS NOT NULL
  DROP PROCEDURE inv.spPurgeExpiredIntakeSessions;
GO

CREATE PROCEDURE inv.spPurgeExpiredIntakeSessions
  @OlderThanDays INT = 7,
  @BatchSize INT = 500
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
  DECLARE @Cutoff DATETIME2(7) = DATEADD(DAY, -@OlderThanDays, @Now);

  /*
    Purge rule:
    - Status in ('Expired','Consumed','Abandoned')
    - UpdatedAt < cutoff (or CreatedAt, but UpdatedAt is better since Expire/Consume touches it)
    - Additionally, do NOT purge Open sessions even if old (defensive)
  */

  DECLARE @DeletedSessions INT = 0;

  WHILE (1 = 1)
  BEGIN
    ;WITH cte AS (
      SELECT TOP (@BatchSize) s.IntakeSessionId
      FROM inv.IntakeSession s
      WHERE s.Status IN ('Expired','Consumed','Abandoned')
        AND s.UpdatedAt < @Cutoff
      ORDER BY s.UpdatedAt ASC
    )
    DELETE s
    FROM inv.IntakeSession s
    JOIN cte ON cte.IntakeSessionId = s.IntakeSessionId;

    SET @DeletedSessions += @@ROWCOUNT;

    IF @@ROWCOUNT = 0 BREAK;
  END

  SELECT @DeletedSessions AS DeletedSessionCount, @Cutoff AS CutoffUtc;
END
GO
