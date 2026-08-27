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
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task SearchRecords_CrossTenant_UsesTargetTenantSearchService()
    {
        // Arrange
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var writeService = Substitute.For<IRecordWriteService>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var adminRepo = Substitute.For<IAdminRepository>();
        var tenantRepo = Substitute.For<ITenantRepository>();

        var parentQueryContext = Substitute.For<IQueryContext>();
        parentQueryContext.TenantId.Returns(6L);
        parentQueryContext.UserId.Returns(999L);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var childQueryContext = Substitute.For<IQueryContext>();
        var targetSearchService = Substitute.For<IPipelineRecordSearchService>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IQueryContext)).Returns(childQueryContext);
        serviceProvider.GetService(typeof(ITenantRepository)).Returns(tenantRepo);
        
        // Target scoped services
        serviceProvider.GetService(typeof(IRecordRepository)).Returns(recordRepo);
        serviceProvider.GetService(typeof(IAppTableRepository)).Returns(tableRepo);
        serviceProvider.GetService(typeof(IAppFieldRepository)).Returns(fieldRepo);
        serviceProvider.GetService(typeof(IRecordWriteService)).Returns(writeService);
        serviceProvider.GetService(typeof(IPipelineTriggerInterceptor)).Returns(Substitute.For<IPipelineTriggerInterceptor>());
        serviceProvider.GetService(typeof(ITenantUnitOfWork)).Returns(Substitute.For<ITenantUnitOfWork>());
        serviceProvider.GetService(typeof(IPipelineStepIdempotencyRepository)).Returns(Substitute.For<IPipelineStepIdempotencyRepository>());
        serviceProvider.GetService(typeof(IFileStorageService)).Returns(Substitute.For<IFileStorageService>());
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(targetSearchService);

        var connectionGuid = Guid.NewGuid();
        adminRepo.GetTenantIdByPublicIdAsync(connectionGuid, Arg.Any<CancellationToken>()).Returns(8L);
        tenantRepo.IsActiveMemberAsync(999L, Arg.Any<CancellationToken>()).Returns(true);

        var table = new AppTable { Id = 1, PublicId = Guid.NewGuid() };
        tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        var outerSearchService = Substitute.For<IPipelineRecordSearchService>();
        var outerServiceProvider = Substitute.For<IServiceProvider>();
        outerServiceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(outerSearchService);

        var engine = new PipelineEngine(
            pipelineRepo,
            recordRepo,
            writeService,
            tableRepo,
            fieldRepo,
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(),
            parentQueryContext,
            scopeFactory,
            outerServiceProvider,
            adminRepo,
            tenantRepo,
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );

        var step = new PipelineStep
        {
            Type = "query",
            Subtype = "search-records",
            ConfigJson = JsonSerializer.Serialize(new { connectionPublicId = connectionGuid.ToString(), tableId = Guid.NewGuid().ToString() })
        };

        var contextDict = new Dictionary<string, object> { { "_CreatedBy", 999L } };

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task<string>)method!.Invoke(engine, new object[] { step, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1", CancellationToken.None })!;
        await task;

        // Assert: Dynamic target tenant search service was called, outer/owner was NOT
        await targetSearchService.Received(1).SearchAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>());
        await outerSearchService.DidNotReceive().SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchRecords_SameTenant_RemainsOwnerScoped()
    {
        // Arrange
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var writeService = Substitute.For<IRecordWriteService>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var adminRepo = Substitute.For<IAdminRepository>();
        var tenantRepo = Substitute.For<ITenantRepository>();

        var parentQueryContext = Substitute.For<IQueryContext>();
        parentQueryContext.TenantId.Returns(6L);
        parentQueryContext.UserId.Returns(999L);

        var table = new AppTable { Id = 1, PublicId = Guid.NewGuid() };
        tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        var outerSearchService = Substitute.For<IPipelineRecordSearchService>();
        var outerServiceProvider = Substitute.For<IServiceProvider>();
        outerServiceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(outerSearchService);

        var engine = new PipelineEngine(
            pipelineRepo,
            recordRepo,
            writeService,
            tableRepo,
            fieldRepo,
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(),
            parentQueryContext,
            Substitute.For<IServiceScopeFactory>(),
            outerServiceProvider,
            adminRepo,
            tenantRepo,
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );

        var step = new PipelineStep
        {
            Type = "query",
            Subtype = "search-records",
            // No connectionPublicId = same tenant / owner scope
            ConfigJson = JsonSerializer.Serialize(new { tableId = Guid.NewGuid().ToString() })
        };

        var contextDict = new Dictionary<string, object> { { "_CreatedBy", 999L } };

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task<string>)method!.Invoke(engine, new object[] { step, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1", CancellationToken.None })!;
        await task;

        // Assert: Outer/owner search service was called
        await outerSearchService.Received(1).SearchAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchThenCreate_MultiTenantScopesRemainIsolated()
    {
        // Arrange
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var adminRepo = Substitute.For<IAdminRepository>();
        var tenantRepo = Substitute.For<ITenantRepository>();

        var parentQueryContext = Substitute.For<IQueryContext>();
        parentQueryContext.TenantId.Returns(6L);
        parentQueryContext.UserId.Returns(999L);

        // Connections setup: connection8 resolves to Tenant 8, connection9 resolves to Tenant 9
        var connGuid8 = Guid.NewGuid();
        var connGuid9 = Guid.NewGuid();
        adminRepo.GetTenantIdByPublicIdAsync(connGuid8, Arg.Any<CancellationToken>()).Returns(8L);
        adminRepo.GetTenantIdByPublicIdAsync(connGuid9, Arg.Any<CancellationToken>()).Returns(9L);
        tenantRepo.IsActiveMemberAsync(999L, Arg.Any<CancellationToken>()).Returns(true);

        // Mock scope resolution for Tenant 8 and Tenant 9
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        
        var scope8 = Substitute.For<IServiceScope>();
        var serviceProvider8 = Substitute.For<IServiceProvider>();
        var queryContext8 = Substitute.For<IQueryContext>();
        var searchService8 = Substitute.For<IPipelineRecordSearchService>();
        var recordRepo8 = Substitute.For<IRecordRepository>();
        var tableRepo8 = Substitute.For<IAppTableRepository>();
        var fieldRepo8 = Substitute.For<IAppFieldRepository>();
        var writeService8 = Substitute.For<IRecordWriteService>();

        scope8.ServiceProvider.Returns(serviceProvider8);
        serviceProvider8.GetService(typeof(IQueryContext)).Returns(queryContext8);
        serviceProvider8.GetService(typeof(ITenantRepository)).Returns(tenantRepo);
        serviceProvider8.GetService(typeof(IPipelineRecordSearchService)).Returns(searchService8);
        serviceProvider8.GetService(typeof(IRecordRepository)).Returns(recordRepo8);
        serviceProvider8.GetService(typeof(IAppTableRepository)).Returns(tableRepo8);
        serviceProvider8.GetService(typeof(IAppFieldRepository)).Returns(fieldRepo8);
        serviceProvider8.GetService(typeof(IRecordWriteService)).Returns(writeService8);
        serviceProvider8.GetService(typeof(IPipelineTriggerInterceptor)).Returns(Substitute.For<IPipelineTriggerInterceptor>());
        serviceProvider8.GetService(typeof(ITenantUnitOfWork)).Returns(Substitute.For<ITenantUnitOfWork>());
        serviceProvider8.GetService(typeof(IPipelineStepIdempotencyRepository)).Returns(Substitute.For<IPipelineStepIdempotencyRepository>());
        serviceProvider8.GetService(typeof(IFileStorageService)).Returns(Substitute.For<IFileStorageService>());

        var scope9 = Substitute.For<IServiceScope>();
        var serviceProvider9 = Substitute.For<IServiceProvider>();
        var queryContext9 = Substitute.For<IQueryContext>();
        var recordRepo9 = Substitute.For<IRecordRepository>();
        var tableRepo9 = Substitute.For<IAppTableRepository>();
        var fieldRepo9 = Substitute.For<IAppFieldRepository>();
        var writeService9 = Substitute.For<IRecordWriteService>();

        scope9.ServiceProvider.Returns(serviceProvider9);
        serviceProvider9.GetService(typeof(IQueryContext)).Returns(queryContext9);
        serviceProvider9.GetService(typeof(ITenantRepository)).Returns(tenantRepo);
        serviceProvider9.GetService(typeof(IRecordRepository)).Returns(recordRepo9);
        serviceProvider9.GetService(typeof(IAppTableRepository)).Returns(tableRepo9);
        serviceProvider9.GetService(typeof(IAppFieldRepository)).Returns(fieldRepo9);
        serviceProvider9.GetService(typeof(IRecordWriteService)).Returns(writeService9);
        serviceProvider9.GetService(typeof(IPipelineTriggerInterceptor)).Returns(Substitute.For<IPipelineTriggerInterceptor>());
        serviceProvider9.GetService(typeof(ITenantUnitOfWork)).Returns(Substitute.For<ITenantUnitOfWork>());
        serviceProvider9.GetService(typeof(IPipelineStepIdempotencyRepository)).Returns(Substitute.For<IPipelineStepIdempotencyRepository>());
        serviceProvider9.GetService(typeof(IFileStorageService)).Returns(Substitute.For<IFileStorageService>());
        serviceProvider9.GetService(typeof(IPipelineRecordSearchService)).Returns(Substitute.For<IPipelineRecordSearchService>());

        // Return scope8 then scope9
        scopeFactory.CreateScope().Returns(scope8, scope9);

        // Mocks for tables
        var table8 = new AppTable { Id = 80, PublicId = Guid.NewGuid() };
        var table9 = new AppTable { Id = 90, PublicId = Guid.NewGuid() };
        tableRepo8.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table8);
        tableRepo9.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table9);
        fieldRepo8.ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        fieldRepo9.ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        var outerServiceProvider = Substitute.For<IServiceProvider>();

        var engine = new PipelineEngine(
            pipelineRepo,
            Substitute.For<IRecordRepository>(),
            Substitute.For<IRecordWriteService>(),
            Substitute.For<IAppTableRepository>(),
            Substitute.For<IAppFieldRepository>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(),
            parentQueryContext,
            scopeFactory,
            outerServiceProvider,
            adminRepo,
            tenantRepo,
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );

        // Execute Step 1 (Search Tenant 8)
        var step1 = new PipelineStep
        {
            Type = "query",
            Subtype = "search-records",
            ConfigJson = JsonSerializer.Serialize(new { connectionPublicId = connGuid8.ToString(), tableId = Guid.NewGuid().ToString() })
        };
        var contextDict = new Dictionary<string, object> { { "_CreatedBy", 999L } };

        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task<string>)method!.Invoke(engine, new object[] { step1, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1", CancellationToken.None })!;

        // Execute Step 2 (Create Tenant 9)
        var step2 = new PipelineStep
        {
            Type = "action",
            Subtype = "create-record",
            ConfigJson = JsonSerializer.Serialize(new { connectionPublicId = connGuid9.ToString(), tableId = Guid.NewGuid().ToString() })
        };
        await (Task<string>)method!.Invoke(engine, new object[] { step2, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_2", CancellationToken.None })!;

        // Assert: Verify isolation. Step 1 used Tenant 8 search service. Step 2 did NOT. Step 2 created on Tenant 9 repository.
        await searchService8.Received(1).SearchAsync(table8, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>());
        await recordRepo9.Received(1).CreateAsync(table9, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchRecords_ConnectionResolutionFailure_FailsClosed()
    {
        // Arrange
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var adminRepo = Substitute.For<IAdminRepository>();
        var tenantRepo = Substitute.For<ITenantRepository>();

        var parentQueryContext = Substitute.For<IQueryContext>();
        parentQueryContext.TenantId.Returns(6L);
        parentQueryContext.UserId.Returns(999L);

        var connectionGuid = Guid.NewGuid();
        // Return null for target tenant -> connection resolution fails
        adminRepo.GetTenantIdByPublicIdAsync(connectionGuid, Arg.Any<CancellationToken>()).Returns((long?)null);

        var outerSearchService = Substitute.For<IPipelineRecordSearchService>();
        var outerServiceProvider = Substitute.For<IServiceProvider>();
        outerServiceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(outerSearchService);

        var engine = new PipelineEngine(
            pipelineRepo,
            Substitute.For<IRecordRepository>(),
            Substitute.For<IRecordWriteService>(),
            Substitute.For<IAppTableRepository>(),
            Substitute.For<IAppFieldRepository>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(),
            parentQueryContext,
            Substitute.For<IServiceScopeFactory>(),
            outerServiceProvider,
            adminRepo,
            tenantRepo,
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );

        var step = new PipelineStep
        {
            Type = "query",
            Subtype = "search-records",
            ConfigJson = JsonSerializer.Serialize(new { connectionPublicId = connectionGuid.ToString(), tableId = Guid.NewGuid().ToString() })
        };

        var contextDict = new Dictionary<string, object> { { "_CreatedBy", 999L } };

        // Act & Assert: Must fail closed, throwing KeyNotFoundException or other error, and never query
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        var act = () => (Task<string>)method!.Invoke(engine, new object[] { step, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1", CancellationToken.None })!;
        
        await act.Should().ThrowAsync<Exception>();
        await outerSearchService.DidNotReceive().SearchAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchRecords_TargetMetadataAndDatabaseTenantMustMatch()
    {
        // Arrange
        var pipelineRepo = Substitute.For<IPipelineRepository>();
        var recordRepo = Substitute.For<IRecordRepository>();
        var tableRepo = Substitute.For<IAppTableRepository>();
        var fieldRepo = Substitute.For<IAppFieldRepository>();
        var adminRepo = Substitute.For<IAdminRepository>();
        var tenantRepo = Substitute.For<ITenantRepository>();
        var writeService = Substitute.For<IRecordWriteService>();

        var parentQueryContext = Substitute.For<IQueryContext>();
        parentQueryContext.TenantId.Returns(6L);
        parentQueryContext.UserId.Returns(999L);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var childQueryContext = Substitute.For<IQueryContext>();
        
        // Mock query execution to ensure the resolved search service matches target tenant DB
        var targetSearchService = Substitute.For<IPipelineRecordSearchService>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IQueryContext)).Returns(childQueryContext);
        serviceProvider.GetService(typeof(ITenantRepository)).Returns(tenantRepo);
        
        serviceProvider.GetService(typeof(IRecordRepository)).Returns(recordRepo);
        serviceProvider.GetService(typeof(IAppTableRepository)).Returns(tableRepo);
        serviceProvider.GetService(typeof(IAppFieldRepository)).Returns(fieldRepo);
        serviceProvider.GetService(typeof(IRecordWriteService)).Returns(writeService);
        serviceProvider.GetService(typeof(IPipelineTriggerInterceptor)).Returns(Substitute.For<IPipelineTriggerInterceptor>());
        serviceProvider.GetService(typeof(ITenantUnitOfWork)).Returns(Substitute.For<ITenantUnitOfWork>());
        serviceProvider.GetService(typeof(IPipelineStepIdempotencyRepository)).Returns(Substitute.For<IPipelineStepIdempotencyRepository>());
        serviceProvider.GetService(typeof(IFileStorageService)).Returns(Substitute.For<IFileStorageService>());
        serviceProvider.GetService(typeof(IPipelineRecordSearchService)).Returns(targetSearchService);

        var connectionGuid = Guid.NewGuid();
        adminRepo.GetTenantIdByPublicIdAsync(connectionGuid, Arg.Any<CancellationToken>()).Returns(8L);
        tenantRepo.IsActiveMemberAsync(999L, Arg.Any<CancellationToken>()).Returns(true);

        var table = new AppTable { Id = 1, PublicId = Guid.NewGuid() };
        tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);
        fieldRepo.ListByTableAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new List<AppField>());

        var engine = new PipelineEngine(
            pipelineRepo,
            recordRepo,
            writeService,
            tableRepo,
            fieldRepo,
            Substitute.For<IEmailService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IFileStorageService>(),
            Options.Create(new PipelineExecutionOptions()),
            Substitute.For<ILogger<PipelineEngine>>(),
            Substitute.For<IPipelineTriggerInterceptor>(),
            Substitute.For<ITenantUnitOfWork>(),
            Substitute.For<IPipelineAuditFormatter>(),
            parentQueryContext,
            scopeFactory,
            Substitute.For<IServiceProvider>(),
            adminRepo,
            tenantRepo,
            Substitute.For<IPipelineStepIdempotencyRepository>()
        );

        var step = new PipelineStep
        {
            Type = "query",
            Subtype = "search-records",
            ConfigJson = JsonSerializer.Serialize(new { connectionPublicId = connectionGuid.ToString(), tableId = Guid.NewGuid().ToString() })
        };

        var contextDict = new Dictionary<string, object> { { "_CreatedBy", 999L } };

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task<string>)method!.Invoke(engine, new object[] { step, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1", CancellationToken.None })!;

        // Assert: verify child query context received TenantId = 8, matching the target search service database tenant
        childQueryContext.Received(1).SetTenantId(8L);
        await targetSearchService.Received(1).SearchAsync(table, Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ResolvePipelineField_FidToken_UsesStableFidOnly()
    {
        var fields = new List<AppField>
        {
            new() { Id = 3, Fid = 15, Name = "FieldA" },
            new() { Id = 27, Fid = 3, Name = "FieldB" }
        };

        var method = typeof(PipelineEngine).GetMethod("ResolvePipelineField", BindingFlags.NonPublic | BindingFlags.Static);
        
        var resolved = (AppField)method!.Invoke(null, new object[] { "fid_3", fields })!;
        resolved.Should().NotBeNull();
        resolved.Fid.Should().Be(3);
        resolved.Id.Should().Be(27);
        resolved.Name.Should().Be("FieldB");
    }

    [Fact]
    public void ResolvePipelineField_FidToken_DoesNotMatchDatabaseId()
    {
        var fields = new List<AppField>
        {
            new() { Id = 3, Fid = 15, Name = "FieldA" },
            new() { Id = 27, Fid = 4, Name = "FieldB" }
        };

        var method = typeof(PipelineEngine).GetMethod("ResolvePipelineField", BindingFlags.NonPublic | BindingFlags.Static);
        
        var act = () => method!.Invoke(null, new object[] { "fid_3", fields });
        var exc = act.Should().Throw<TargetInvocationException>();
        exc.Which.InnerException.Should().BeOfType<PowerBase.Domain.Exceptions.PipelineNonRetryableException>();
    }

    [Fact]
    public void ResolvePipelineField_MissingFid_FailsClosed()
    {
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 10, Name = "FieldA" }
        };

        var method = typeof(PipelineEngine).GetMethod("ResolvePipelineField", BindingFlags.NonPublic | BindingFlags.Static);
        
        var act = () => method!.Invoke(null, new object[] { "fid_99", fields });
        var exc = act.Should().Throw<TargetInvocationException>();
        exc.Which.InnerException.Should().BeOfType<PowerBase.Domain.Exceptions.PipelineNonRetryableException>();
    }

    [Fact]
    public async Task SearchRecords_CrossTenant_RecordId_UsesStableFid()
    {
        // Arrange
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 3, Name = "S_recordId", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" },
            new() { Id = 2, Fid = 1, Name = "S_dateCreated", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "CreatedOn" }
        };

        var table = new AppTable { Id = 10, PublicId = Guid.NewGuid() };
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(fields);

        var config = new
        {
            tableId = table.PublicId.ToString(),
            filterField = "fid_3",
            filterValue = "14"
        };

        var step = new PipelineStep
        {
            Id = 268,
            Type = "query",
            Subtype = "search-records",
            ConfigJson = JsonSerializer.Serialize(config)
        };

        FilterGroup? capturedFilterTree = null;
        _pipelineRecordSearchService.SearchAsync(table, fields, Arg.Any<int?>(), Arg.Any<FilterGroup>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                capturedFilterTree = x.ArgAt<FilterGroup>(3);
                return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(new List<IReadOnlyDictionary<string, object?>>());
            });

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task<string>)method!.Invoke(_engine, new object[] {
            step, "{}", new Dictionary<string, object>(), new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
            _recordRepo, _tableRepo, _fieldRepo, _recordWriteService, Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineStepIdempotencyRepository>(), Substitute.For<IFileStorageService>(), _pipelineRecordSearchService, CancellationToken.None
        })!;

        // Assert
        capturedFilterTree.Should().NotBeNull();
        var node = capturedFilterTree.Nodes.Should().ContainSingle().Subject;
        node.Condition.Should().NotBeNull();
        node.Condition!.FieldId.Should().Be(3); // Stable Fid, not AppField.Id == 1
        node.Condition!.Value.Should().Be("14");
    }

    [Fact]
    public void SearchRecords_AdvancedFilter_UsesStableFid()
    {
        // Arrange
        var fields = new List<AppField>
        {
            new() { Id = 1, Fid = 3, Name = "S_recordId", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" },
            new() { Id = 2, Fid = 1, Name = "S_dateCreated", TypeCode = "DateTime", IsSystem = true, PhysicalColumnName = "CreatedOn" }
        };

        var triggerFilterGroup = new TriggerFilterGroup
        {
            LogicalOp = "AND",
            Rules = new List<TriggerFilterRule>
            {
                new() { Field = "fid_3", Operator = "greater_than", Value = "14" }
            }
        };

        var method = typeof(PipelineEngine).GetMethod("MapTriggerFilterGroupToDbFilterGroup", BindingFlags.NonPublic | BindingFlags.Instance);
        
        // Act
        var result = (FilterGroup)method!.Invoke(_engine, new object[] { triggerFilterGroup, fields, "{}", "path", new List<PipelineStep>() })!;

        // Assert
        result.Should().NotBeNull();
        var node = result.Nodes.Should().ContainSingle().Subject;
        node.Condition.Should().NotBeNull();
        node.Condition!.FieldId.Should().Be(3); // Stable Fid
        node.Condition!.Operator.Should().Be("gt");
        node.Condition!.Value.Should().Be("14");
    }

    [Fact]
    public async Task CreateRecord_CrossTenant_TargetField_UsesStableFid()
    {
        // Arrange
        var fields = new List<AppField>
        {
            new() { Id = 3, Fid = 15, Name = "FieldA", TypeCode = "text" },
            new() { Id = 27, Fid = 3, Name = "FieldB", TypeCode = "text" }
        };

        var table = new AppTable { Id = 10, PublicId = Guid.NewGuid() };
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(fields);

        var config = new
        {
            tableId = table.PublicId.ToString(),
            fieldMappings = new[]
            {
                new { field = "fid_3", value = "MappedValue" }
            }
        };

        var step = new PipelineStep
        {
            Id = 1,
            Type = "action",
            Subtype = "create-record",
            ConfigJson = JsonSerializer.Serialize(config)
        };

        IReadOnlyDictionary<long, object?>? capturedValues = null;
        _recordRepo.CreateAsync(table, fields, Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                capturedValues = new Dictionary<long, object?>(x.ArgAt<IReadOnlyDictionary<long, object?>>(2));
                return Task.FromResult(Guid.NewGuid());
            });

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task<string>)method!.Invoke(_engine, new object[] {
            step, "{}", new Dictionary<string, object>(), new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
            _recordRepo, _tableRepo, _fieldRepo, _recordWriteService, Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineStepIdempotencyRepository>(), Substitute.For<IFileStorageService>(), _pipelineRecordSearchService, CancellationToken.None
        })!;

        // Assert
        capturedValues.Should().NotBeNull();
        capturedValues!.ContainsKey(3).Should().BeTrue(); // Target Fid = 3 (FieldB)
        capturedValues!.ContainsKey(15).Should().BeFalse(); // Target Fid = 15 (FieldA) should not be mapped
        capturedValues[3].Should().Be("MappedValue");
    }

    [Fact]
    public async Task UpdateRecord_CrossTenant_TargetField_UsesStableFid()
    {
        // Arrange
        var fields = new List<AppField>
        {
            new() { Id = 3, Fid = 15, Name = "FieldA", TypeCode = "text" },
            new() { Id = 27, Fid = 3, Name = "FieldB", TypeCode = "text" }
        };

        var table = new AppTable { Id = 10, PublicId = Guid.NewGuid() };
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(fields);

        var config = new
        {
            tableId = table.PublicId.ToString(),
            targetRecordId = Guid.NewGuid().ToString(),
            fieldMappings = new[]
            {
                new { field = "fid_3", value = "UpdatedValue" }
            }
        };

        var step = new PipelineStep
        {
            Id = 1,
            Type = "action",
            Subtype = "update-record",
            ConfigJson = JsonSerializer.Serialize(config)
        };

        IReadOnlyDictionary<long, object?>? capturedValues = null;
        _recordWriteService.ApplyAsync(table, fields, Arg.Any<Guid>(), Arg.Any<IReadOnlyDictionary<long, object?>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Data.IDbTransaction?>(), Arg.Any<bool>(), Arg.Any<Action<PowerBase.Application.Common.Models.SearchIndexMessage>?>())
            .Returns(x =>
            {
                capturedValues = new Dictionary<long, object?>(x.ArgAt<IReadOnlyDictionary<long, object?>>(3));
                return Task.FromResult<IReadOnlyDictionary<long, object?>>(new Dictionary<long, object?>());
            });

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task<string>)method!.Invoke(_engine, new object[] {
            step, "{}", new Dictionary<string, object>(), new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
            _recordRepo, _tableRepo, _fieldRepo, _recordWriteService, Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineStepIdempotencyRepository>(), Substitute.For<IFileStorageService>(), _pipelineRecordSearchService, CancellationToken.None
        })!;

        // Assert
        capturedValues.Should().NotBeNull();
        capturedValues!.ContainsKey(3).Should().BeTrue(); // Target Fid = 3 (FieldB)
        capturedValues!.ContainsKey(15).Should().BeFalse(); // Target Fid = 15 (FieldA) should not be updated
        capturedValues[3].Should().Be("UpdatedValue");
    }

    [Fact]
    public async Task CommitUpsert_MergeField_UsesStableFid()
    {
        // Arrange
        var fields = new List<AppField>
        {
            new() { Id = 3, Fid = 15, Name = "FieldA", TypeCode = "text" },
            new() { Id = 27, Fid = 3, Name = "FieldB", TypeCode = "text" }
        };

        var table = new AppTable { Id = 10, PublicId = Guid.NewGuid() };
        _tableRepo.GetByPublicIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(fields);

        var step = new PipelineStep
        {
            Id = 1,
            Type = "action",
            Subtype = "commit-upsert",
            ConfigJson = JsonSerializer.Serialize(new { parentUpsertStepRefId = "parent_ref" })
        };

        var parentSession = new PipelineEngine.BulkUpsertSession
        {
            TableLabel = table.PublicId.ToString(),
            MergeKeyFid = "fid_3",
            Rows = new List<Dictionary<long, object?>>
            {
                new() { { 3, "MergeKeyValue" } } // Fid = 3 has value
            }
        };

        FilterGroup? capturedFilterTree = null;
        _recordRepo.ListAsync(table, fields, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FilterGroup>(), Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(x =>
            {
                capturedFilterTree = x.ArgAt<FilterGroup>(4);
                return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(new List<IReadOnlyDictionary<string, object?>>());
            });

        var contextDict = new Dictionary<string, object>();
        var sessions = new Dictionary<string, PipelineEngine.BulkUpsertSession>
        {
            { "parent_ref", parentSession }
        };
        contextDict["_bulkUpsertSessions"] = sessions;

        // Act
        var method = typeof(PipelineEngine).GetMethod("ExecuteStepWithServicesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task<string>)method!.Invoke(_engine, new object[] {
            step, "{}", contextDict, new List<PipelineStep>(), new Dictionary<string, object>(), 1L, new PipelineStepRun(), new List<PipelineEngine.RawStepAuditSnapshot>(), "step_1",
            _recordRepo, _tableRepo, _fieldRepo, _recordWriteService, Substitute.For<IPipelineTriggerInterceptor>(), Substitute.For<ITenantUnitOfWork>(), Substitute.For<IPipelineStepIdempotencyRepository>(), Substitute.For<IFileStorageService>(), _pipelineRecordSearchService, CancellationToken.None
        })!;

        // Assert
        capturedFilterTree.Should().NotBeNull();
        var node = capturedFilterTree.Nodes.Should().ContainSingle().Subject;
        node.Condition.Should().NotBeNull();
        node.Condition!.FieldId.Should().Be(3); // Stable Fid = 3, not AppField.Id
        node.Condition!.Value.Should().Be("MergeKeyValue");
    }
}

