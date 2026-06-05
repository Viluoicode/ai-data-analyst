/* ============================================================================
   03_readonly_role.sql  —  The real security backstop (DB-level least privilege).
   The application queries through this login, which CANNOT write and can read
   ONLY the gold schema. Even a bug in the app-level SQL validator cannot cause
   a write or read outside gold, because this principal has no such permission.
   ============================================================================ */

USE master;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'analyst_ro')
    CREATE LOGIN analyst_ro WITH PASSWORD = 'Readonly#Analyst1', CHECK_POLICY = OFF;
GO

USE AnalystDB;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'analyst_ro')
    CREATE USER analyst_ro FOR LOGIN analyst_ro;
GO

/* Grant read ONLY on the gold schema.
   Deliberately NOT db_datareader (that would expose every schema).            */
GRANT SELECT ON SCHEMA::gold TO analyst_ro;

/* Defense in depth: explicitly deny writes / DDL / exec on gold.              */
DENY INSERT, UPDATE, DELETE, EXECUTE, ALTER ON SCHEMA::gold TO analyst_ro;
GO

/* Resolve unqualified table names (e.g. FROM FactOrderItem) to gold, so an LLM
   that occasionally omits the schema prefix still executes correctly.         */
ALTER USER analyst_ro WITH DEFAULT_SCHEMA = gold;
GO

PRINT 'Read-only principal analyst_ro configured (SELECT on gold only).';
GO
