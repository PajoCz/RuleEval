using Xunit;
using System.Data;
using RuleEval.Abstractions;
using RuleEval.Caching;
using RuleEval.Core;
using RuleEval.Database;
using RuleEval.Database.Abstractions;

namespace RuleEval.Tests.Integration;

public sealed class RuleEvalIntegrationTests
{
    [Fact]
    public void Mapper_MapsMetadataAndRows_ToDomainRuleSet()
    {
        var mapper = new DbRuleSetMapper();
        var ruleSet = mapper.Map(CreateDefinition());

        Assert.Equal("pricing", ruleSet.Key);
        Assert.Equal(2, ruleSet.InputFields.Length);
        Assert.Equal("formula", ruleSet.Rules[0].Outputs[0].Name);
        Assert.Equal("DataId", ruleSet.Rules[0].PrimaryKey?.Name);
    }

    [Fact]
    public async Task SqlServerSource_LoadsDefinition_FromExecutorContract()
    {
        var executor = new FakeExecutor();
        var source = new SqlServerRuleSetSource(executor, "Server=.;Database=RuleEval;", "dbo.GetColumns", "dbo.GetRows");

        var definition = await source.LoadAsync("pricing");

        Assert.Equal("pricing", definition.Key);
        Assert.Contains(definition.Columns, column => column.Role == RuleFieldRole.Input);
        Assert.Contains(executor.Calls, call => call.CommandType == CommandType.StoredProcedure);
    }

    [Fact]
    public async Task PostgreSqlSource_LoadsDefinition_FromExecutorContract()
    {
        var executor = new FakeExecutor();
        var source = new PostgreSqlRuleSetSource(executor, "Host=localhost;Database=ruleeval;", "public.get_columns", "public.get_rows");

        var definition = await source.LoadAsync("pricing");

        Assert.Equal("pricing", definition.Key);
        Assert.Contains(executor.Calls, call => call.CommandType == CommandType.Text);
        Assert.Contains(executor.Calls, call => call.CommandText.Contains("select * from public.get_columns", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Repository_Load_WithCache_And_GetFirstOutput_Works()
    {
        var source = new StubSource(CreateDefinition());
        var repository = new RuleSetRepository(source, new MemoryRuleSetCache());
        var output = await repository.GetFirstOutputAsync("pricing", EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m), "formula");

        Assert.Equal("C2/240", output);
    }

    [Fact]
    public async Task Repository_EvaluateFirst_ReturnsNoMatch_ForUnknownInput()
    {
        var repository = new RuleSetRepository(new StubSource(CreateDefinition()), new NoCacheRuleSetCache());
        var result = await repository.EvaluateFirstAsync("pricing", EvaluationContext.FromPositional("Unknown", 99m));

        Assert.Equal(EvaluationStatus.NoMatch, result.Status);
    }

    [Fact]
    public void Mapper_Throws_ForInvalidMetadata()
    {
        var mapper = new DbRuleSetMapper();
        var definition = new DbRuleSetDefinition("broken", [], [new RuleSetRowData(null, new Dictionary<string, object?>())]);

        Assert.Throws<InvalidRuleDefinitionException>(() => mapper.Map(definition));
    }

    [Fact]
    public async Task SqlServerSource_PrimaryKeyFromFirstDataColumn()
    {
        var executor = new FakeExecutor();
        var source = new SqlServerRuleSetSource(executor, "Server=.;Database=RuleEval;", "dbo.GetColumns", "dbo.GetRows");

        var definition = await source.LoadAsync("pricing");

        Assert.All(definition.Rows, row => Assert.Equal("TranslatorDataId", row.PrimaryKey?.Name));
        Assert.Equal(42, definition.Rows[0].PrimaryKey?.Value);
    }

    [Fact]
    public async Task SqlServerSource_NoSchemaDefinedPrimaryKey_MetadataOnlyHasInputsAndOutputs()
    {
        var executor = new FakeExecutor();
        var source = new SqlServerRuleSetSource(executor, "Server=.;Database=RuleEval;", "dbo.GetColumns", "dbo.GetRows");

        var definition = await source.LoadAsync("pricing");

        Assert.DoesNotContain(definition.Columns, col => col.Role != RuleFieldRole.Input && col.Role != RuleFieldRole.Output);
    }

    [Fact]
    public async Task SqlServerSource_PrimaryKey_DoesNotAffectMatchingBehavior()
    {
        var executor = new FakeExecutor();
        var source = new SqlServerRuleSetSource(executor, "Server=.;Database=RuleEval;", "dbo.GetColumns", "dbo.GetRows");
        var repository = new RuleSetRepository(source, new NoCacheRuleSetCache());

        // Matching succeeds based on Col01/Col02 patterns — PK value (42) plays no role
        var result = await repository.EvaluateFirstAsync("pricing", EvaluationContext.FromPositional("7BN Perspektiva Důchod", 15m));

        Assert.Equal(EvaluationStatus.Matched, result.Status);
        Assert.Equal("TranslatorDataId", result.Match?.PrimaryKey?.Name);
        Assert.Equal(42, result.Match?.PrimaryKey?.Value);
    }

    [Fact]
    public async Task SqlServerSource_NamedEvaluation_UsesCodes()
    {
        var executor = new FakeExecutor();
        var source = new SqlServerRuleSetSource(executor, "Server=.;Database=RuleEval;", "dbo.GetColumns", "dbo.GetRows");
        var repository = new RuleSetRepository(source, new NoCacheRuleSetCache());

        var result = await repository.EvaluateFirstAsync("pricing",
            EvaluationContext.FromNamed(new Dictionary<string, object?> { ["segment"] = "7BN Perspektiva Důchod", ["age"] = 15m }));

        Assert.Equal(EvaluationStatus.Matched, result.Status);
    }

    private static DbRuleSetDefinition CreateDefinition()
        => new(
            "pricing",
            [
                new RuleSetColumnDefinition("segment", 1, RuleFieldRole.Input, 1),
                new RuleSetColumnDefinition("age", 2, RuleFieldRole.Input, 2),
                new RuleSetColumnDefinition("formula", 3, RuleFieldRole.Output, 3),
            ],
            [
                new RuleSetRowData(
                    new PrimaryKeyValue("DataId", 1),
                    new Dictionary<string, object?>
                    {
                        ["Col01"] = ".*Perspektiva.*",
                        ["Col02"] = "INTERVAL<15;24>",
                        ["Col03"] = "C2/240",
                    }),
            ]);

    private sealed class StubSource : IRuleSetSource
    {
        private readonly DbRuleSetDefinition _definition;

        public StubSource(DbRuleSetDefinition definition)
        {
            _definition = definition;
        }

        public ValueTask<DbRuleSetDefinition> LoadAsync(string key, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_definition);
    }

    private sealed class FakeExecutor : IRelationalCommandExecutor
    {
        public List<(string CommandText, CommandType CommandType)> Calls { get; } = [];

        public ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string connectionString, string commandText, IReadOnlyDictionary<string, object?> parameters, CommandType commandType, CancellationToken cancellationToken = default)
        {
            Calls.Add((commandText, commandType));
            if (commandText.Contains("Columns", StringComparison.OrdinalIgnoreCase) || commandText.Contains("get_columns", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                [
                    new Dictionary<string, object?>
                    {
                        ["Code"] = "segment",
                        ["ColNr"] = 1,
                        ["Order"] = 1,
                        ["Type"] = 0,
                    },
                    new Dictionary<string, object?>
                    {
                        ["Code"] = "age",
                        ["ColNr"] = 2,
                        ["Order"] = 2,
                        ["Type"] = 0,
                    },
                    new Dictionary<string, object?>
                    {
                        ["Code"] = "formula",
                        ["ColNr"] = 3,
                        ["Order"] = 3,
                        ["Type"] = 1,
                    },
                ]);
            }

            return ValueTask.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
            [
                new Dictionary<string, object?>
                {
                    ["TranslatorDataId"] = 42,
                    ["Col01"] = ".*Perspektiva.*",
                    ["Col02"] = "INTERVAL<15;24>",
                    ["Col03"] = "C2/240",
                },
            ]);
        }
    }
}
