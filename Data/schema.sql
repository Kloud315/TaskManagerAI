IF DB_ID(N'TaskManagerAI') IS NULL
BEGIN
    CREATE DATABASE TaskManagerAI;
END;
GO

USE TaskManagerAI;
GO

IF OBJECT_ID(N'dbo.Tasks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Tasks
    (
        Id INT IDENTITY(1, 1) PRIMARY KEY,
        Title NVARCHAR(500) NOT NULL,
        IsDone BIT NOT NULL CONSTRAINT DF_Tasks_IsDone DEFAULT (0),
        DueDate DATETIME2 NULL
    );
END;
