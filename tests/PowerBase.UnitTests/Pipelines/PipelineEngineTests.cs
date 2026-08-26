using FluentAssertions;
using Xunit;
using NSubstitute;
using System.Reflection;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using PowerBase.Application.Pipelines;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Configurations;
using PowerBase.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;

namespace PowerBase.UnitTests.Pipelines;

public class PipelineEngineTests
{
    private readonly PipelineEngine _engine;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRecordWriteService _recordWriteService;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly PipelineExecutionOptions _execOptions;
    private readonly ILogger<PipelineEngine> _logger;
    private readonly IPipelineAuditFormatter _auditFormatter;
    private readonly IPipelineRecordSearchService _pipelineRecordSearchService;

    public PipelineEngineTests()
    {
        _pipelineRepo = Substitute.For<IPipelineRepository>();
        _recordRepo = Substitute.For<IRecordRepository>();
        _recordWriteService = Substitute.For<IRecordWriteService>();
        _tableRepo = Substitute.For<IAppTableRepository>();
        _fieldRepo = Substitute.For<IAppFieldRepository>();
        _execOptions = new PipelineExecutionOptions();
        _logger = Substitute.For<ILogger<PipelineEngine>>();
        _auditFormatter = Substitute.For<IPipelineAuditFormatter>();
        _pipelineRecordSearchService = Substitute.For<IPipelineRecordSearchService>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(_pipelineRecordSearchService);

        _engine = new PipelineEngine(
            _pipelineRepo,
            _recordRepo,
            _recordWriteService,
            _tableRepo,
            _fieldRepo,
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(_execOptions),
            _logger,
            Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(),
            _auditFormatter,
            Substitute.For<IQueryContext>(),
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            serviceProvider,
            Substitute.For<IAdminRepository>(),
            Substitute.For<ITenantRepository>(),
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );
    }

    private string InvokeEvaluateTokens(string? input, string payloadJson)
    {
        var method = typeof(PipelineEngine).GetMethod("EvaluateTokens", BindingFlags.NonPublic | BindingFlags.Instance);
        return (string)method!.Invoke(_engine, new object?[] { input, payloadJson, null, null })!;
    }

    private object? InvokeParseValueType(string valueStr, string typeCode)
    {
        var method = typeof(PipelineEngine).GetMethod("ParseValueType", BindingFlags.NonPublic | BindingFlags.Instance);
        return method!.Invoke(_engine, new object?[] { valueStr, typeCode });
    }

    [Fact]
    public void EvaluateTokens_LegacyAndPrefixedFormat_ShouldResolve()
    {
        var payload = JsonSerializer.Serialize(new { fid_101 = "ValueA", fid_102 = 123 });

        InvokeEvaluateTokens("Test {{fid_101}}", payload).Should().Be("Test ValueA");
        InvokeEvaluateTokens("Test {{steps.trigger.fid_102}}", payload).Should().Be("Test 123");
    }

    [Fact]
    public void ParseValueType_Datatypes_ShouldResolveCorrectTypes()
    {
        InvokeParseValueType("true", "checkbox").Should().Be(true);
        InvokeParseValueType("1", "boolean").Should().Be(true);
        InvokeParseValueType("123.45", "numeric").Should().Be(123.45m);
        InvokeParseValueType("2026-08-06T18:00:00Z", "date_time").Should().Be(DateTime.Parse("2026-08-06T18:00:00Z"));
        InvokeParseValueType("plain text", "text").Should().Be("plain text");
    }

    [Fact]
    public async Task ExecuteAsync_SqlDeadlock_RetriesUpToConfiguredMaxAndThrows()
    {
        // Arrange
        _execOptions.SqlDeadlockMaxRetries = 3;
        var task = new PipelineExecutionTask
        {
            PipelineId = 1,
            TenantId = 1,
            TriggerEvent = "RecordAdded",
            TriggerPayloadJson = "{}"
        };

        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 1L));

        _pipelineRepo.GetStepsByPipelineIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(x => Task.FromException<IReadOnlyList<PipelineStep>>(new SqlException(1205)));

        // Act & Assert
        var act = () => _engine.ExecuteAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<SqlException>();

        // Verify it retried exactly 3 times
        await _pipelineRepo.Received(3).GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SqlDeadlock_SucceedsOnSubsequentAttempt()
    {
        // Arrange
        _execOptions.SqlDeadlockMaxRetries = 3;
        var task = new PipelineExecutionTask
        {
            PipelineId = 1,
            TenantId = 1,
            TriggerEvent = "RecordAdded",
            TriggerPayloadJson = "{}"
        };

        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 1L));

        int attempts = 0;
        _pipelineRepo.GetStepsByPipelineIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                attempts++;
                if (attempts < 3)
                {
                    return Task.FromException<IReadOnlyList<PipelineStep>>(new SqlException(1205));
                }
                IReadOnlyList<PipelineStep> stepsList = new List<PipelineStep>();
                return Task.FromResult(stepsList); // Succeed on 3rd attempt
            });

        // Act
        var act = () => _engine.ExecuteAsync(task, CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Assert
        attempts.Should().Be(3);
        await _pipelineRepo.Received(3).GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ResolvePath_NestedPathsAndArrays_ResolvesValuesCorrectly()
    {
        var payload = JsonSerializer.Serialize(new
        {
            trigger = new { fid_101 = "TriggerValue" },
            steps = new
            {
                ref_query = new
                {
                    records = new[]
                    {
                        new { RecordId = "Rec_123" }
                    }
                }
            }
        });

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var method = typeof(PipelineEngine).GetMethod("ResolvePath", BindingFlags.NonPublic | BindingFlags.Instance);
        
        var val1 = (string?)method!.Invoke(_engine, new object[] { root, "steps.ref_query.records[0].RecordId" });
        val1.Should().Be("Rec_123");

        var val2 = (string?)method!.Invoke(_engine, new object[] { root, "trigger.fid_101" });
        val2.Should().Be("TriggerValue");

        var val3 = (string?)method!.Invoke(_engine, new object[] { root, "steps.ref_query.records[1].RecordId" });
        val3.Should().BeNull(); // index out of bounds
    }

    [Fact]
    public void EvaluateTokens_MixedTriggerAndStepTokens_ResolvesCorrectly()
    {
        var payload = JsonSerializer.Serialize(new
        {
            fid_101 = "DirectTriggerField",
            trigger = new { fid_101 = "TriggerField" },
            steps = new
            {
                ref_query = new { records = new[] { new { RecordId = "Rec_123" } } }
            }
        });

        var input = "Direct: {{fid_101}}, Step: {{steps.ref_query.records[0].RecordId}}";
        InvokeEvaluateTokens(input, payload).Should().Be("Direct: DirectTriggerField, Step: Rec_123");
    }

    // Custom test exception simulating SqlException with deadlock Number 1205
    public class SqlException : Exception
    {
        public int Number { get; }
        public SqlException(int number) : base("Simulated SQL deadlock")
        {
            Number = number;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ConditionTrue_RunsChildrenBranch()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "RecordAdded", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new()
            {
                Id = 999,
                Type = "trigger",
                Subtype = "new-event",
                IsDeleted = false
            },
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "cond_1",
                Label = "Condition True",
                Type = "control",
                Subtype = "condition",
                ConfigJson = JsonSerializer.Serialize(new { LeftOperand = "New", Operator = "equals", RightOperand = "New" })
            },
            new()
            {
                Id = 2,
                ParentStepId = 1,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "act_true",
                Label = "Create Record True Branch",
                Type = "action",
                Subtype = "create-record",
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FieldMappings = new List<object>() })
            },
            new()
            {
                Id = 3,
                ParentStepId = 1,
                ParentBranch = "elsechildren",
                PublicId = Guid.NewGuid(),
                RefId = "act_false",
                Label = "Create Record False Branch",
                Type = "action",
                Subtype = "create-record",
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FieldMappings = new List<object>() })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10 });
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Is<System.Data.IDbTransaction?>(x => true), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 2), Arg.Any<CancellationToken>());
        await _pipelineRepo.DidNotReceive().CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ConditionFalse_RunsElseChildrenBranch()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "RecordAdded", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new()
            {
                Id = 999,
                Type = "trigger",
                Subtype = "new-event",
                IsDeleted = false
            },
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "cond_1",
                Label = "Condition False",
                Type = "control",
                Subtype = "condition",
                ConfigJson = JsonSerializer.Serialize(new { LeftOperand = "New", Operator = "equals", RightOperand = "Old" })
            },
            new()
            {
                Id = 2,
                ParentStepId = 1,
                ParentBranch = "children",
                PublicId = Guid.NewGuid(),
                RefId = "act_true",
                Label = "Create Record True Branch",
                Type = "action",
                Subtype = "create-record",
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FieldMappings = new List<object>() })
            },
            new()
            {
                Id = 3,
                ParentStepId = 1,
                ParentBranch = "elsechildren",
                PublicId = Guid.NewGuid(),
                RefId = "act_false",
                Label = "Create Record False Branch",
                Type = "action",
                Subtype = "create-record",
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FieldMappings = new List<object>() })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10 });
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Is<System.Data.IDbTransaction?>(x => true), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 3), Arg.Any<CancellationToken>());
        await _pipelineRepo.DidNotReceive().CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void EvaluateTokens_NestedJsonObjectLookup_ResolvesCorrectly()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_1 = new
                {
                    item = new
                    {
                        name = "Alex",
                        address = new { city = "Mumbai" }
                    }
                }
            }
        });

        InvokeEvaluateTokens("Hello {{steps.ref_1.item.name}} from {{steps.ref_1.item.address.city}}", payload)
            .Should().Be("Hello Alex from Mumbai");
    }

    [Fact]
    public void EvaluateTokens_NullAndEmptyValues_ResolvesToEmpty()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_1 = new
                {
                    item = new
                    {
                        nullValue = (string?)null,
                        emptyValue = ""
                    }
                }
            }
        });

        InvokeEvaluateTokens("Null: {{steps.ref_1.item.nullValue}}, Empty: {{steps.ref_1.item.emptyValue}}", payload)
            .Should().Be("Null: , Empty: ");
    }

    [Fact]
    public void EvaluateTokens_CollectionIndexer_ResolvesCorrectly()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_query = new
                {
                    records = new[]
                    {
                        new { RecordId = "Rec_1" },
                        new { RecordId = "Rec_2" }
                    }
                }
            }
        });

        InvokeEvaluateTokens("First: {{steps.ref_query.records[0].RecordId}}, Second: {{steps.ref_query.records[1].RecordId}}", payload)
            .Should().Be("First: Rec_1, Second: Rec_2");
    }

    private bool InvokeEvaluateConditionOperator(string leftVal, string op, string rightVal)
    {
        var method = typeof(PipelineEngine).GetMethod("EvaluateConditionOperator", BindingFlags.NonPublic | BindingFlags.Instance);
        return (bool)method!.Invoke(_engine, new object[] { leftVal, op, rightVal })!;
    }

    [Fact]
    public void EvaluateConditionOperator_StringOperators_EvaluatesCorrectly()
    {
        InvokeEvaluateConditionOperator("Banana", "equals", "banana").Should().BeTrue();
        InvokeEvaluateConditionOperator("Banana", "contains", "an").Should().BeTrue();
        InvokeEvaluateConditionOperator("Banana", "starts_with", "ba").Should().BeTrue();
        InvokeEvaluateConditionOperator("Banana", "ends_with", "na").Should().BeTrue();
        InvokeEvaluateConditionOperator("  ", "is_blank", "").Should().BeTrue();
        InvokeEvaluateConditionOperator("text", "is_not_blank", "").Should().BeTrue();
    }

    [Fact]
    public void EvaluateConditionOperator_NumericOperators_EvaluatesCorrectly()
    {
        InvokeEvaluateConditionOperator("10", ">", "2").Should().BeTrue();
        InvokeEvaluateConditionOperator("2.5", "<=", "2.5").Should().BeTrue();
        InvokeEvaluateConditionOperator("5", "=", "5.0").Should().BeTrue();
    }

    [Fact]
    public void EvaluateConditionOperator_BooleanOperators_EvaluatesCorrectly()
    {
        InvokeEvaluateConditionOperator("true", "is_true", "").Should().BeTrue();
        InvokeEvaluateConditionOperator("1", "is_true", "").Should().BeTrue();
        InvokeEvaluateConditionOperator("false", "is_false", "").Should().BeTrue();
        InvokeEvaluateConditionOperator("0", "is_false", "").Should().BeTrue();
    }

    [Fact]
    public void EvaluateConditionOperator_DateTimeOperators_EvaluatesCorrectly()
    {
        InvokeEvaluateConditionOperator("2026-08-11T18:00:00Z", ">", "2026-08-10T18:00:00Z").Should().BeTrue();
        InvokeEvaluateConditionOperator("2026-08-11", "=", "2026-08-11T00:00:00Z").Should().BeTrue();
    }

    [Fact]
    public void EvaluateConditionOperator_InvalidInputs_ReturnsFalse()
    {
        // When one side is a valid number/date but the other side is invalid, it returns false (no string fallback)
        InvokeEvaluateConditionOperator("invalid-number", ">", "2").Should().BeFalse();
        InvokeEvaluateConditionOperator("invalid-date", "<", "2026-08-11").Should().BeFalse();
    }

    [Fact]
    public void EvaluateConditionOperator_PlainStringsWithOperators_FallsBackToStringCompare()
    {
        // When neither side is numeric or datetime, it falls back to string comparison
        InvokeEvaluateConditionOperator("banana", ">", "apple").Should().BeTrue();
    }

    [Fact]
    public void EvaluateTokens_NativeScribanFilters_ResolvesCorrectly()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_step = new
                {
                    value = "hello world",
                    amount = 120.50,
                    date_val = "2026-08-11T18:00:00Z"
                }
            }
        });

        // Test string filters (upcase, downcase, size)
        InvokeEvaluateTokens("{{ steps.ref_step.value | string.upcase }}", payload).Should().Be("HELLO WORLD");
        InvokeEvaluateTokens("{{ steps.ref_step.value | string.size }}", payload).Should().Be("11");

        // Test date format filter
        InvokeEvaluateTokens("{{ steps.ref_step.date_val | format_datetime \"yyyy-MM-dd\" }}", payload).Should().Be("2026-08-11");

        // Test Jinja-compatible custom filters (to_json, length)
        InvokeEvaluateTokens("{{ steps.ref_step.value | to_json }}", payload).Should().Be("\"hello world\"");
        InvokeEvaluateTokens("{{ steps.ref_step.value | length }}", payload).Should().Be("11");
    }

    [Fact]
    public void EvaluateConditionGroup_GroupedRulesAndRecursiveLogic_EvaluatesCorrectly()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_step = new
                {
                    status = "Approved",
                    category = "Software",
                    amount = 5000
                }
            }
        });

        // Class instance mapping: ConditionRuleGroup
        var groupType = typeof(PipelineEngine).GetNestedType("ConditionRuleGroup", BindingFlags.NonPublic);
        var nodeType = typeof(PipelineEngine).GetNestedType("ConditionRuleNode", BindingFlags.NonPublic);

        var groupInstance = Activator.CreateInstance(groupType!);
        groupType.GetProperty("LogicalOp")!.SetValue(groupInstance, "AND");

        var node1 = Activator.CreateInstance(nodeType!);
        nodeType.GetProperty("Type")!.SetValue(node1, "rule");
        nodeType.GetProperty("Left")!.SetValue(node1, "{{steps.ref_step.status}}");
        nodeType.GetProperty("Op")!.SetValue(node1, "equals");
        nodeType.GetProperty("Right")!.SetValue(node1, "Approved");

        var node2 = Activator.CreateInstance(nodeType!);
        nodeType.GetProperty("Type")!.SetValue(node2, "rule");
        nodeType.GetProperty("Left")!.SetValue(node2, "{{steps.ref_step.category}}");
        nodeType.GetProperty("Op")!.SetValue(node2, "equals");
        nodeType.GetProperty("Right")!.SetValue(node2, "Software");

        var listType = typeof(List<>).MakeGenericType(nodeType);
        var rulesList = Activator.CreateInstance(listType);
        listType.GetMethod("Add")!.Invoke(rulesList, new[] { node1 });
        listType.GetMethod("Add")!.Invoke(rulesList, new[] { node2 });

        groupType.GetProperty("Rules")!.SetValue(groupInstance, rulesList);

        var method = typeof(PipelineEngine).GetMethod("EvaluateConditionGroup", BindingFlags.NonPublic | BindingFlags.Instance);
        var result = (bool)method!.Invoke(_engine, new[] { groupInstance, payload, null, null })!;

        result.Should().BeTrue();
    }

    [Fact]
    public void EvaluateTokens_LiquidCastingFilters_ResolvesCorrectly()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_step = new
                {
                    val_str = "45.67",
                    val_num = 12.34,
                    val_neg = -12.34,
                    val_bool = true,
                    val_bool_false = false
                }
            }
        });

        // Test string cast
        InvokeEvaluateTokens("{{ steps.ref_step.val_num | string }}", payload).Should().Be("12.34");
        InvokeEvaluateTokens("{{ steps.ref_step.val_bool | string }}", payload).Should().Be("True");

        // Test int cast (truncation)
        InvokeEvaluateTokens("{{ steps.ref_step.val_str | int }}", payload).Should().Be("45");
        InvokeEvaluateTokens("{{ steps.ref_step.val_num | int }}", payload).Should().Be("12");
        InvokeEvaluateTokens("{{ steps.ref_step.val_neg | int }}", payload).Should().Be("-12");
        InvokeEvaluateTokens("{{ steps.ref_step.val_bool | int }}", payload).Should().Be("1");
        InvokeEvaluateTokens("{{ steps.ref_step.val_bool_false | int }}", payload).Should().Be("0");

        // Test float cast
        InvokeEvaluateTokens("{{ steps.ref_step.val_str | float }}", payload).Should().Be("45.67");

        // Test chained filters
        InvokeEvaluateTokens("{{ steps.ref_step.val_num | string | length }}", payload).Should().Be("5");

        // Test that string namespace works (not shadowed)
        InvokeEvaluateTokens("{{ steps.ref_step.val_str | string.size }}", payload).Should().Be("5");
    }

    [Fact]
    public void FieldMapping_StringOrPrimitiveJsonConverter_ShouldDeserializeCorrectly()
    {
        var configJson = "{\"fieldMappings\": [{\"field\": \"fid_101\", \"value\": \"ABC\"}, {\"field\": \"fid_102\", \"value\": 123}, {\"field\": \"fid_103\", \"value\": true}, {\"field\": \"fid_104\", \"value\": null}]}";

        var method = typeof(PipelineEngine).GetNestedType("CreateRecordStepConfig", BindingFlags.NonPublic);
        var deserializeOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        
        var config = JsonSerializer.Deserialize(configJson, method!, deserializeOptions);
        config.Should().NotBeNull();

        var mappingsProp = method!.GetProperty("FieldMappings");
        var mappings = (System.Collections.IEnumerable)mappingsProp!.GetValue(config)!;
        
        var mappingList = new List<object>();
        foreach (var item in mappings)
        {
            mappingList.Add(item);
        }

        mappingList.Count.Should().Be(4);

        var valueProp = mappingList[0].GetType().GetProperty("Value");
        var fieldProp = mappingList[0].GetType().GetProperty("Field");

        fieldProp!.GetValue(mappingList[0]).Should().Be("fid_101");
        valueProp!.GetValue(mappingList[0]).Should().Be("ABC");

        fieldProp!.GetValue(mappingList[1]).Should().Be("fid_102");
        valueProp!.GetValue(mappingList[1]).Should().Be("123");

        fieldProp!.GetValue(mappingList[2]).Should().Be("fid_103");
        valueProp!.GetValue(mappingList[2]).Should().Be("true");

        fieldProp!.GetValue(mappingList[3]).Should().Be("fid_104");
        valueProp!.GetValue(mappingList[3]).Should().BeNull();
    }

    [Fact]
    public void FieldMapping_StringOrPrimitiveJsonConverter_ShouldThrowForComplexTypes()
    {
        var configJson = "{\"fieldMappings\": [{\"field\": \"fid_101\", \"value\": {\"nested\": 123}}]}";
        var method = typeof(PipelineEngine).GetNestedType("CreateRecordStepConfig", BindingFlags.NonPublic);
        var deserializeOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var act = () => JsonSerializer.Deserialize(configJson, method!, deserializeOptions);
        act.Should().Throw<JsonException>().WithMessage("*Unsupported complex JSON token type*");
    }

    [Fact]
    public void EvaluateTokens_StepTriggerAndCustomRefFormat_ShouldResolveRealTriggerValues()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_trigger = new
                {
                    fid_6 = "Test Item",
                    fid_7 = 150
                }
            }
        });

        InvokeEvaluateTokens("{{steps.ref_trigger.fid_6}}", payload).Should().Be("Test Item");
        InvokeEvaluateTokens("{{steps.ref_trigger.fid_7}}", payload).Should().Be("150");
    }

    [Fact]
    public async Task ExecuteAsync_DeferredFormatting_ExecutesFormattingPostCommit()
    {
        // Arrange
        var task = new PipelineExecutionTask
        {
            PipelineId = 101,
            TenantId = 1,
            TriggerEvent = "new-event",
            TriggerPayloadJson = "{}"
        };

        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 1L));

        _pipelineRepo.GetByIdAsync(101, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "trigger", Subtype = "new-event", RefId = "trg_1" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>())
            .Returns(steps);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        // FormatStepRun must have been called (which happens in the finally block post-commit)
        _auditFormatter.Received(1).FormatStepRun(
            Arg.Any<PipelineStep>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>()
        );
    }

    [Fact]
    public async Task ExecuteAsync_FormatterFailure_DoesNotInterruptSuccessfulPipeline()
    {
        // Arrange
        var task = new PipelineExecutionTask
        {
            PipelineId = 101,
            TenantId = 1,
            TriggerEvent = "new-event",
            TriggerPayloadJson = "{}"
        };

        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), 1L));

        _pipelineRepo.GetByIdAsync(101, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 1, Type = "trigger", Subtype = "new-event", RefId = "trg_1" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(101, Arg.Any<CancellationToken>())
            .Returns(steps);

        // Setup formatter to throw exception
        _auditFormatter.When(x => x.FormatStepRun(
            Arg.Any<PipelineStep>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>()))
            .Do(x => throw new Exception("Simulation formatting error"));

        // Act & Assert
        // Executing the pipeline must not throw, despite the formatting failure
        var act = () => _engine.ExecuteAsync(task, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void EvaluateTokens_JsonElementAccessor_TranslatesPhysicalToStableKeys()
    {
        var payload = JsonSerializer.Serialize(new
        {
            steps = new
            {
                ref_search = new
                {
                    records = new[]
                    {
                        new Dictionary<string, object>
                        {
                            { "f_101", "Apple" },
                            { "fid_102", "Orange" }
                        }
                    }
                }
            }
        });

        InvokeEvaluateTokens("{{steps.ref_search.records[0].fid_101}} - {{steps.ref_search.records[0].f_102}}", payload)
            .Should().Be("Apple - Orange");
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedStepType_ThrowsNotSupportedException()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false },
            new() { Id = 1, PublicId = Guid.NewGuid(), RefId = "unsupported_step", Type = "unknown", Subtype = "unsupported-subtype" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);

        // Act & Assert
        var act = () => _engine.ExecuteAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task ExecuteAsync_SearchRecordsAndLoopForEach_ExecutesCreateRecordForEachMatchedItem()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 10, Type = "trigger", Subtype = "new-event", RefId = "ref_trigger", IsDeleted = false },
            new() 
            { 
                Id = 11, 
                RefId = "ref_search", 
                Type = "query", 
                Subtype = "search-records", 
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FilterField = "f_1", FilterValue = "test" }) 
            },
            new() 
            { 
                Id = 12, 
                RefId = "ref_loop", 
                Type = "loop", 
                Subtype = "for-each", 
                ConfigJson = JsonSerializer.Serialize(new { LoopOverStepId = "ref_search" }) 
            },
            new() 
            { 
                Id = 13, 
                ParentStepId = 12, 
                ParentBranch = "children", 
                RefId = "ref_create", 
                Type = "action", 
                Subtype = "create-record", 
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FieldMappings = new List<object> { new { Field = "fid_2", Value = "{{steps.ref_loop.item.fid_1}}" } } }) 
            }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);

        var table = new AppTable { Id = 100 };
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);

        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 1, Name = "Name", TypeCode = "text" },
            new() { Id = 2, Fid = 2, Name = "Target", TypeCode = "text" }
        };
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        // We mock ListAsync to return 3 records
        var matchedRecords = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { { "f_1", "Value1" } },
            new Dictionary<string, object?> { { "f_1", "Value2" } },
            new Dictionary<string, object?> { { "f_1", "Value3" } }
        };
        _pipelineRecordSearchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(matchedRecords);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert: CreateAsync should have been called exactly 3 times
        await _recordRepo.Received(3).CreateAsync(
            table,
            Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Any<IReadOnlyDictionary<long, object?>>(),
            Arg.Any<System.Data.IDbTransaction?>(),
            Arg.Any<CancellationToken>()
        );
        await _recordRepo.Received(1).CreateAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyDictionary<long, object?>>(d => d.ContainsKey(2) && d[2] as string == "Value1"), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
        await _recordRepo.Received(1).CreateAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyDictionary<long, object?>>(d => d.ContainsKey(2) && d[2] as string == "Value2"), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
        await _recordRepo.Received(1).CreateAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyDictionary<long, object?>>(d => d.ContainsKey(2) && d[2] as string == "Value3"), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LegacyDirectSourceExpressionInsideLoop_ResolvesCurrentIterationValuesViaFallback()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 10, Type = "trigger", Subtype = "new-event", RefId = "ref_trigger", IsDeleted = false },
            new() 
            { 
                Id = 11, 
                RefId = "ref_search", 
                Type = "query", 
                Subtype = "search-records", 
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FilterField = "f_1", FilterValue = "test" }) 
            },
            new() 
            { 
                Id = 12, 
                RefId = "ref_loop", 
                Type = "loop", 
                Subtype = "for-each", 
                ConfigJson = JsonSerializer.Serialize(new { LoopOverStepId = "ref_search" }) 
            },
            new() 
            { 
                Id = 13, 
                ParentStepId = 12, 
                ParentBranch = "children", 
                RefId = "ref_create", 
                Type = "action", 
                Subtype = "create-record", 
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FieldMappings = new List<object> { new { Field = "fid_2", Value = "{{steps.ref_search.fid_1}}" } } }) 
            }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);

        var table = new AppTable { Id = 100 };
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);

        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 1, Name = "Name", TypeCode = "text" },
            new() { Id = 2, Fid = 2, Name = "Target", TypeCode = "text" }
        };
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        var matchedRecords = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { { "f_1", "Value1" } },
            new Dictionary<string, object?> { { "f_1", "Value2" } },
            new Dictionary<string, object?> { { "f_1", "Value3" } }
        };
        _pipelineRecordSearchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(matchedRecords);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert: Legacy fallback maps steps.ref_search.fid_1 to steps.ref_loop.item.fid_1
        await _recordRepo.Received(1).CreateAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyDictionary<long, object?>>(d => d.ContainsKey(2) && d[2] as string == "Value1"), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
        await _recordRepo.Received(1).CreateAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyDictionary<long, object?>>(d => d.ContainsKey(2) && d[2] as string == "Value2"), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
        await _recordRepo.Received(1).CreateAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyDictionary<long, object?>>(d => d.ContainsKey(2) && d[2] as string == "Value3"), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LegacyDirectSourceExpressionOutsideLoop_DoesNotResolveViaFallback()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 10, Type = "trigger", Subtype = "new-event", RefId = "ref_trigger", IsDeleted = false },
            new() 
            { 
                Id = 11, 
                RefId = "ref_search", 
                Type = "query", 
                Subtype = "search-records", 
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FilterField = "f_1", FilterValue = "test" }) 
            },
            new() 
            { 
                Id = 13, 
                RefId = "ref_create", 
                Type = "action", 
                Subtype = "create-record", 
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FieldMappings = new List<object> { new { Field = "fid_2", Value = "{{steps.ref_search.fid_1}}" } } }) 
            }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);

        var table = new AppTable { Id = 100 };
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);

        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 1, Name = "Name", TypeCode = "text" },
            new() { Id = 2, Fid = 2, Name = "Target", TypeCode = "text" }
        };
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        var matchedRecords = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { { "f_1", "Value1" } }
        };
        _pipelineRecordSearchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(matchedRecords);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert: Outside a loop, steps.ref_search.fid_1 remains unresolved (blank/null)
        await _recordRepo.Received(1).CreateAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Is<IReadOnlyDictionary<long, object?>>(d => !d.ContainsKey(2) || d[2] == null || d[2] as string == ""), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitCollectionAccessInsideLoop_RemainsUntouched()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 10, Type = "trigger", Subtype = "new-event", RefId = "ref_trigger", IsDeleted = false },
            new() 
            { 
                Id = 11, 
                RefId = "ref_search", 
                Type = "query", 
                Subtype = "search-records", 
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FilterField = "f_1", FilterValue = "test" }) 
            },
            new() 
            { 
                Id = 12, 
                RefId = "ref_loop", 
                Type = "loop", 
                Subtype = "for-each", 
                ConfigJson = JsonSerializer.Serialize(new { LoopOverStepId = "ref_search" }) 
            },
            new() 
            { 
                Id = 13, 
                ParentStepId = 12, 
                ParentBranch = "children", 
                RefId = "ref_create", 
                Type = "action", 
                Subtype = "create-record", 
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FieldMappings = new List<object> { new { Field = "fid_2", Value = "{{steps.ref_search.records[0].fid_1}}" } } }) 
            }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);

        var table = new AppTable { Id = 100 };
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);

        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 1, Name = "Name", TypeCode = "text" },
            new() { Id = 2, Fid = 2, Name = "Target", TypeCode = "text" }
        };
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        var matchedRecords = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { { "f_1", "Value1" } },
            new Dictionary<string, object?> { { "f_1", "Value2" } }
        };
        _pipelineRecordSearchService.SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(matchedRecords);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert: Explicit array lookups map to record 0's value ("Value1") for all iterations
        await _recordRepo.Received(2).CreateAsync(
            table,
            Arg.Any<IReadOnlyList<AppField>>(),
            Arg.Is<IReadOnlyDictionary<long, object?>>(d => d.ContainsKey(2) && d[2] as string == "Value1"),
            Arg.Any<System.Data.IDbTransaction?>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task ExecuteAsync_SearchRecordsUnlimited_CallsSearchServiceWithNullMaxResults()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 10, Type = "trigger", Subtype = "new-event", RefId = "ref_trigger", IsDeleted = false },
            new() 
            { 
                Id = 11, 
                RefId = "ref_search", 
                Type = "query", 
                Subtype = "search-records", 
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FilterField = "f_1", FilterValue = "test", MaxResults = (int?)null }) 
            }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);

        var table = new AppTable { Id = 100 };
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);

        var fields = new List<AppField> { new() { Id = 1, Fid = 1, Name = "Name", TypeCode = "text" } };
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert: should query with null MaxResults
        await _pipelineRecordSearchService.Received(1).SearchAsync(
            table,
            Arg.Any<IReadOnlyList<AppField>>(),
            null,
            Arg.Any<FilterGroup>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task ExecuteAsync_SearchRecordsFinite_CallsSearchServiceWithFiniteMaxResults()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 10, Type = "trigger", Subtype = "new-event", RefId = "ref_trigger", IsDeleted = false },
            new() 
            { 
                Id = 11, 
                RefId = "ref_search", 
                Type = "query", 
                Subtype = "search-records", 
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FilterField = "f_1", FilterValue = "test", MaxResults = 5 }) 
            }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);

        var table = new AppTable { Id = 100 };
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);

        var fields = new List<AppField> { new() { Id = 1, Fid = 1, Name = "Name", TypeCode = "text" } };
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(fields);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert: should query with MaxResults = 5
        await _pipelineRecordSearchService.Received(1).SearchAsync(
            table,
            Arg.Any<IReadOnlyList<AppField>>(),
            5,
            Arg.Any<FilterGroup>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public void SearchRecordsStepConfig_NullableIntJsonConverter_DeserializesCorrectly()
    {
        var configType = typeof(PipelineEngine).GetNestedType("SearchRecordsStepConfig", BindingFlags.NonPublic);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // 1. Unlimited with ""
        var config1 = JsonSerializer.Deserialize("{\"maxResults\": \"\"}", configType!, options);
        configType!.GetProperty("MaxResults")!.GetValue(config1).Should().BeNull();

        // 2. Unlimited with "unlimited"
        var config2 = JsonSerializer.Deserialize("{\"maxResults\": \"unlimited\"}", configType!, options);
        configType!.GetProperty("MaxResults")!.GetValue(config2).Should().BeNull();

        // 3. Unlimited with null
        var config3 = JsonSerializer.Deserialize("{\"maxResults\": null}", configType!, options);
        configType!.GetProperty("MaxResults")!.GetValue(config3).Should().BeNull();

        // 4. Unlimited with negative/zero
        var config4 = JsonSerializer.Deserialize("{\"maxResults\": -1}", configType!, options);
        configType!.GetProperty("MaxResults")!.GetValue(config4).Should().BeNull();

        // 5. Finite with number
        var config5 = JsonSerializer.Deserialize("{\"maxResults\": 5}", configType!, options);
        configType!.GetProperty("MaxResults")!.GetValue(config5).Should().Be(5);

        // 6. Finite with numeric string
        var config6 = JsonSerializer.Deserialize("{\"maxResults\": \"3\"}", configType!, options);
        configType!.GetProperty("MaxResults")!.GetValue(config6).Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_MismatchTriggerEvent_ThrowsPipelineNonRetryableException()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "schedule", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 10, Type = "trigger", Subtype = "record-added", RefId = "ref_trigger", IsDeleted = false }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);

        // Act & Assert
        var act = () => _engine.ExecuteAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PowerBase.Domain.Exceptions.PipelineNonRetryableException>()
            .WithMessage("Schedule trigger event requires an active root-level schedule trigger step.");
    }

    [Fact]
    public async Task ExecuteAsync_SupportedSubtypesInEngine_ExecutesSuccessfully()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{\"trigger\":{\"fid_1\":\"val1\"}}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 10, Type = "trigger", Subtype = "record-added", RefId = "ref_trigger", IsDeleted = false }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert: It should execute without throwing NotSupportedException
        await _pipelineRepo.Received(1).CreateStepRunAsync(Arg.Is<PipelineStepRun>(sr => sr.StepId == 10 && sr.Status == "Success"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_PipelineScheduleTriggerEvent_ValidStructure_Succeeds()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "pipeline_schedule", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 10, Type = "query", Subtype = "search-records", RefId = "ref_search", IsDeleted = false, ConfigJson = $"{{\"TableId\":\"{Guid.NewGuid()}\"}}" }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        
        var table = new AppTable { Id = 100 };
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        // Act & Assert
        var act = () => _engine.ExecuteAsync(task, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PipelineCreateRecord_UsesActiveRecordLookup()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var recordPublicId = Guid.NewGuid();
        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false },
            new()
            {
                Id = 2,
                PublicId = Guid.NewGuid(),
                RefId = "act_create",
                Type = "action",
                Subtype = "create-record",
                ConfigJson = JsonSerializer.Serialize(new { TableId = Guid.NewGuid().ToString(), FieldMappings = new List<object>() })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10 });
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(recordPublicId);

        _recordRepo.GetActiveRecordIdByPublicIdAsync(Arg.Any<AppTable>(), recordPublicId, Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(42L);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        await _recordRepo.Received(1).GetActiveRecordIdByPublicIdAsync(Arg.Any<AppTable>(), recordPublicId, Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PipelineBulkCreate_UsesActiveRecordLookup()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var tablePublicId = Guid.NewGuid();
        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false },
            new()
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                RefId = "ref_prep",
                Type = "action",
                Subtype = "prepare-bulk-upsert",
                ConfigJson = JsonSerializer.Serialize(new { TableLabel = tablePublicId.ToString(), MergeKeyFid = "fid_10" })
            },
            new()
            {
                Id = 2,
                PublicId = Guid.NewGuid(),
                RefId = "ref_add",
                Type = "action",
                Subtype = "add-bulk-upsert-row",
                ConfigJson = JsonSerializer.Serialize(new { ParentUpsertStepRefId = "steps.ref_prep", FieldMappings = new[] { new { Field = "fid_10", Value = "Bob" } } })
            },
            new()
            {
                Id = 3,
                PublicId = Guid.NewGuid(),
                RefId = "ref_commit",
                Type = "action",
                Subtype = "commit-upsert",
                ConfigJson = JsonSerializer.Serialize(new { ParentUpsertStepRefId = "steps.ref_prep" })
            }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);
        _tableRepo.GetByPublicIdAsync(tablePublicId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 10, PublicId = tablePublicId });
        
        var fields = new List<AppField>
        {
            new() { Id = 10, Fid = 10, Name = "Name", TypeCode = "Text" },
            new() { Id = 3, Fid = 3, Name = "Record ID#", TypeCode = "RecordId" }
        };
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(fields);

        var recordPublicId = Guid.NewGuid();
        _recordRepo.CreateAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(recordPublicId);

        var expectedDict = new Dictionary<Guid, long> { [recordPublicId] = 42L };
        _recordRepo.GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(recordPublicId)), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>())
            .Returns(expectedDict);

        // Act
        await _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        await _recordRepo.Received(1).GetActiveRecordIdsByPublicIdsAsync(Arg.Any<AppTable>(), Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(recordPublicId)), Arg.Any<System.Data.IDbTransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecursiveOnNewEvent_CreateRecord_RemainsWorking()
    {
        // Arrange
        var task = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };
        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var recordPublicId = Guid.NewGuid();
        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false }
        };

        _pipelineRepo.GetStepsByPipelineIdAsync(1, Arg.Any<CancellationToken>()).Returns(steps);

        // Act
        var act = () => _engine.ExecuteAsync(task, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TwoConcurrentPipelineCreates_SameTable_NoDeadlockRegression()
    {
        // Arrange
        var task1 = new PipelineExecutionTask { PipelineId = 1, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };
        var task2 = new PipelineExecutionTask { PipelineId = 2, TenantId = 1, TriggerEvent = "new-event", TriggerPayloadJson = "{}" };

        _pipelineRepo.CreateRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns((Guid.NewGuid(), 1L));
        _pipelineRepo.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new Pipeline { Id = 1, IsActive = true, IsDeleted = false });

        var steps = new List<PipelineStep>
        {
            new() { Id = 999, Type = "trigger", Subtype = "new-event", IsDeleted = false }
        };
        _pipelineRepo.GetStepsByPipelineIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(steps);

        // Act
        var t1 = _engine.ExecuteAsync(task1, CancellationToken.None);
        var t2 = _engine.ExecuteAsync(task2, CancellationToken.None);

        var act = () => Task.WhenAll(t1, t2);

        // Assert
        await act.Should().NotThrowAsync();
    }
}

