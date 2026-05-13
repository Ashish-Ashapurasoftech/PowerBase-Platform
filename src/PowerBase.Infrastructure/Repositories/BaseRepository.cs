using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public abstract class BaseRepository
{
    protected readonly DbConnectionFactory ConnectionFactory;
    protected readonly IQueryContext QueryContext;

    protected BaseRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
    {
        ConnectionFactory = connectionFactory;
        QueryContext = queryContext;
    }
}
