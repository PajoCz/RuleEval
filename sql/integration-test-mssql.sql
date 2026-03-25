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
-- Nový kontrakt: Code, ColNr, Order, Type (0=Input, 1=Output)
-- ---------------------------------------------------------------
IF OBJECT_ID('[RuleEvalTest].[SchemaCol]', 'U') IS NULL
CREATE TABLE [RuleEvalTest].[SchemaCol]
(
    SchemaColId  INT           IDENTITY(1,1) PRIMARY KEY,
    SchemaCode   NVARCHAR(100) NOT NULL,
    Code         NVARCHAR(100) NOT NULL,  -- logický název pole (vstup nebo výstup)
    ColNr        INT           NOT NULL,  -- fyzická pozice: Col01, Col02 …
    [Order]      INT           NOT NULL,  -- logické pořadí pro poziční evaluaci
    [Type]       INT           NOT NULL   -- 0 = Input, 1 = Output
);
GO

-- ---------------------------------------------------------------
-- Datová tabulka (vrací p_GetDataBySchemaCode)
-- První sloupec = technický identifikátor řádku (diagnostika)
-- ---------------------------------------------------------------
IF OBJECT_ID('[RuleEvalTest].[Data]', 'U') IS NULL
CREATE TABLE [RuleEvalTest].[Data]
(
    TranslatorDataId INT           IDENTITY(1,1) PRIMARY KEY,
    SchemaCode       NVARCHAR(100) NOT NULL,
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
-- Type: 0 = Input, 1 = Output
-- ---------------------------------------------------------------
DELETE FROM [RuleEvalTest].[SchemaCol] WHERE SchemaCode = 'RuleEvalTest_Pricing';
INSERT INTO [RuleEvalTest].[SchemaCol] (SchemaCode, Code, ColNr, [Order], [Type])
VALUES
    ('RuleEvalTest_Pricing', 'Segment', 1, 1, 0),
    ('RuleEvalTest_Pricing', 'Age',     2, 2, 0),
    ('RuleEvalTest_Pricing', 'Formula', 3, 3, 1);

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
INSERT INTO [RuleEvalTest].[SchemaCol] (SchemaCode, Code, ColNr, [Order], [Type])
VALUES
    ('RuleEvalTest_OrderVsColNr', 'Segment', 2, 1, 0),
    ('RuleEvalTest_OrderVsColNr', 'Age',     1, 2, 0),
    ('RuleEvalTest_OrderVsColNr', 'Result',  3, 3, 1);

DELETE FROM [RuleEvalTest].[Data] WHERE SchemaCode = 'RuleEvalTest_OrderVsColNr';
INSERT INTO [RuleEvalTest].[Data] (SchemaCode, Col01, Col02, Col03)
VALUES
    --  Col01 = Age pattern,     Col02 = Segment pattern,  Col03 = výstup
    ('RuleEvalTest_OrderVsColNr', 'INTERVAL<15;24>', '.*Perspektiva.*', 'OK');
GO

-- ---------------------------------------------------------------
-- Stored procedure – definice sloupců
-- Vrací: Code, ColNr, Order, Type
-- ---------------------------------------------------------------
CREATE OR ALTER PROCEDURE [RuleEvalTest].[p_GetSchemaColBySchemaCode]
    @Code NVARCHAR(100)
AS
    SELECT Code, ColNr, [Order], [Type]
    FROM   [RuleEvalTest].[SchemaCol]
    WHERE  SchemaCode = @Code
    ORDER  BY [Order];
GO

-- ---------------------------------------------------------------
-- Stored procedure – datové řádky
-- První sloupec (TranslatorDataId) = diagnostický identifikátor
-- ---------------------------------------------------------------
CREATE OR ALTER PROCEDURE [RuleEvalTest].[p_GetDataBySchemaCode]
    @Code NVARCHAR(100)
AS
    SELECT TranslatorDataId,
           Col01, Col02, Col03, Col04, Col05, Col06, Col07, Col08, Col09, Col10,
           Col11, Col12, Col13, Col14, Col15, Col16, Col17, Col18, Col19, Col20
    FROM   [RuleEvalTest].[Data]
    WHERE  SchemaCode = @Code;
GO
