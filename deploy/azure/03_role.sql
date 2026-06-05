/* ============================================================================
   Azure SQL Database — least-privilege read-only app user.
   Azure SQL uses a CONTAINED user (password lives in the database), not a server login.
   Run while CONNECTED to AnalystDB.
   Change the password and keep it in sync with the app's connection string.
   ============================================================================ */

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'analyst_ro')
    CREATE USER analyst_ro WITH PASSWORD = 'Readonly#Analyst1', DEFAULT_SCHEMA = gold;
GO

/* Read ONLY the gold schema; deny writes/DDL. */
GRANT SELECT ON SCHEMA::gold TO analyst_ro;
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER ON SCHEMA::gold TO analyst_ro;
GO

PRINT 'Azure read-only user analyst_ro configured (SELECT on gold only).';
GO
