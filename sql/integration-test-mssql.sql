-- =============================================================
-- RuleEval – MSSQL integration test setup
-- Spusť jednou ručně nebo jako část CI pipeline před testy.
-- Vyžaduje práva CREATE SCHEMA / CREATE TABLE / CREATE PROCEDURE.
-- =============================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'RuleEvalTest')
    EXEC sp_executesql N'CREATE SCHEMA [RuleEvalTest]';
GO

-- ---------------------------------------------------------------
-- Tabulka definic sloupců (vrací p_GetSchemaColBySchemaCode)
-- ---------------------------------------------------------------
IF OBJECT_ID('[RuleEvalTest].[SchemaCol]', 'U') IS NULL
CREATE TABLE [RuleEvalTest].[SchemaCol]
(
    SchemaColId  INT           IDENTITY(1,1) PRIMARY KEY,
    SchemaCode   NVARCHAR(100) NOT NULL,
    Name         NVARCHAR(100) NOT NULL,
    ColNr        INT           NOT NULL,  -- fyzická pozice: Col01, Col02 …
    [Order]      INT           NOT NULL,  -- logické pořadí pro poziční evaluaci
    [Type]       INT           NOT NULL,  -- 1 = Input, 2 = Output, 3 = PrimaryKey
    FieldName    NVARCHAR(100) NULL,
    MatcherKey   NVARCHAR(50)  NULL       -- 'regex', 'decimal-interval', 'equality', NULL = auto
);
GO

-- ---------------------------------------------------------------
-- Datová tabulka (vrací p_GetDataBySchemaCode)
-- ---------------------------------------------------------------
IF OBJECT_ID('[RuleEvalTest].[Data]', 'U') IS NULL
CREATE TABLE [RuleEvalTest].[Data]
(
    DataId     INT           IDENTITY(1,1) PRIMARY KEY,
    SchemaCode NVARCHAR(100) NOT NULL,
    Col01 NVARCHAR(500) NULL, Col02 NVARCHAR(500) NULL, Col03 NVARCHAR(500) NULL,
    Col04 NVARCHAR(500) NULL, Col05 NVARCHAR(500) NULL, Col06 NVARCHAR(500) NULL,
    Col07 NVARCHAR(500) NULL, Col08 NVARCHAR(500) NULL, Col09 NVARCHAR(500) NULL,
    Col10 NVARCHAR(500) NULL, Col11 NVARCHAR(500) NULL, Col12 NVARCHAR(500) NULL,
    Col13 NVARCHAR(500) NULL, Col14 NVARCHAR(500) NULL, Col15 NVARCHAR(500) NULL,
    Col16 NVARCHAR(500) NULL, Col17 NVARCHAR(500) NULL, Col18 NVARCHAR(500) NULL,
    Col19 NVARCHAR(500) NULL, Col20 NVARCHAR(500) NULL
);
GO

-- ---------------------------------------------------------------
-- Testovací data – schema "RuleEvalTest_Pricing"
-- Vstupy: Segment (regex), Age (decimal-interval)
-- Výstup: Formula
-- ---------------------------------------------------------------
DELETE FROM [RuleEvalTest].[SchemaCol] WHERE SchemaCode = 'RuleEvalTest_Pricing';
INSERT INTO [RuleEvalTest].[SchemaCol] (SchemaCode, Name, ColNr, [Order], [Type], FieldName, MatcherKey)
VALUES
    ('RuleEvalTest_Pricing', 'Segment', 1, 1, 1, 'Segment', 'regex'),
    ('RuleEvalTest_Pricing', 'Age',     2, 2, 1, 'Age',     'decimal-interval'),
    ('RuleEvalTest_Pricing', 'Formula', 3, 3, 2, 'Formula', NULL);

DELETE FROM [RuleEvalTest].[Data] WHERE SchemaCode = 'RuleEvalTest_Pricing';
INSERT INTO [RuleEvalTest].[Data] (SchemaCode, Col01, Col02, Col03)
VALUES
    ('RuleEvalTest_Pricing', '.*Perspektiva.*', 'INTERVAL<15;24>', 'C2/240'),
    ('RuleEvalTest_Pricing', '.*Standard.*',    'INTERVAL<25;65>', 'D3/120');
GO

-- ---------------------------------------------------------------
-- Testovací data – schema "RuleEvalTest_OrderVsColNr"
-- ColNr a Order se záměrně liší:
--   Order=1 → ColNr=2 (fyzicky Col02 = segment pattern)
--   Order=2 → ColNr=1 (fyzicky Col01 = age pattern)
-- Poziční volání: FromPositional(segmentValue, ageValue)
-- ---------------------------------------------------------------
DELETE FROM [RuleEvalTest].[SchemaCol] WHERE SchemaCode = 'RuleEvalTest_OrderVsColNr';
INSERT INTO [RuleEvalTest].[SchemaCol] (SchemaCode, Name, ColNr, [Order], [Type], FieldName, MatcherKey)
VALUES
    ('RuleEvalTest_OrderVsColNr', 'Segment', 2, 1, 1, 'Segment', 'regex'),
    ('RuleEvalTest_OrderVsColNr', 'Age',     1, 2, 1, 'Age',     'decimal-interval'),
    ('RuleEvalTest_OrderVsColNr', 'Result',  3, 3, 2, 'Result',  NULL);

DELETE FROM [RuleEvalTest].[Data] WHERE SchemaCode = 'RuleEvalTest_OrderVsColNr';
INSERT INTO [RuleEvalTest].[Data] (SchemaCode, Col01, Col02, Col03)
VALUES
    --  Col01 = Age pattern,     Col02 = Segment pattern,  Col03 = výstup
    ('RuleEvalTest_OrderVsColNr', 'INTERVAL<15;24>', '.*Perspektiva.*', 'OK');
GO

-- ---------------------------------------------------------------
-- Stored procedure – definice sloupců
-- ---------------------------------------------------------------
CREATE OR ALTER PROCEDURE [RuleEvalTest].[p_GetSchemaColBySchemaCode]
    @Code NVARCHAR(100)
AS
    SELECT Name, ColNr, [Order], [Type], FieldName, MatcherKey
    FROM   [RuleEvalTest].[SchemaCol]
    WHERE  SchemaCode = @Code
    ORDER  BY [Order];
GO

-- ---------------------------------------------------------------
-- Stored procedure – datové řádky
-- ---------------------------------------------------------------
CREATE OR ALTER PROCEDURE [RuleEvalTest].[p_GetDataBySchemaCode]
    @Code NVARCHAR(100)
AS
    SELECT Col01, Col02, Col03, Col04, Col05, Col06, Col07, Col08, Col09, Col10,
           Col11, Col12, Col13, Col14, Col15, Col16, Col17, Col18, Col19, Col20
    FROM   [RuleEvalTest].[Data]
    WHERE  SchemaCode = @Code;
GO
