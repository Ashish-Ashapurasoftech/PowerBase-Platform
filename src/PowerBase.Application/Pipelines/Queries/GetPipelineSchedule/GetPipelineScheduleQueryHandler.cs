using System;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Pipelines.Queries.GetPipelineSchedule;

public class GetPipelineScheduleQueryHandler
{
    private readonly IPipelineRepository _pipelineRepo;

    public GetPipelineScheduleQueryHandler(IPipelineRepository pipelineRepo)
    {
        _pipelineRepo = pipelineRepo;
    }

    public async Task<PipelineScheduleResult?> HandleAsync(GetPipelineScheduleQuery query, CancellationToken ct)
    {
        var pipelineId = await _pipelineRepo.GetIdByPublicIdAsync(query.PipelinePublicId, ct);
        var schedule = await _pipelineRepo.GetScheduleByPipelineIdAsync(pipelineId, ct);
        if (schedule == null) return null;

        return new PipelineScheduleResult
        {
            PublicId = schedule.PublicId,
            PipelinePublicId = query.PipelinePublicId,
            ScheduleType = schedule.ScheduleType,
            Interval = schedule.Interval,
            TimeOfDay = schedule.TimeOfDay,
            Weekdays = schedule.Weekdays,
            MonthDay = schedule.MonthDay,
            MonthOfYear = schedule.MonthOfYear,
            RelativeWeek = schedule.RelativeWeek,
            RelativeDay = schedule.RelativeDay,
            TimeZone = schedule.TimeZone,
            CronExpression = schedule.CronExpression,
            NextRunOn = schedule.NextRunOn,
            LastRunOn = schedule.LastRunOn
        };
    }
}
